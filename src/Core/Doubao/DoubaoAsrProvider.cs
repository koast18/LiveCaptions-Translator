using System.Threading.Channels;

using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>Well-known keys a host puts into <see cref="Asr.AsrOptions.Settings"/> for this provider.</summary>
    public static class DoubaoSettingKeys
    {
        public const string DeviceId = "doubao_device_id";

        public const string AppKey = "doubao_app_key";

        public const string InstallId = "doubao_install_id";

        public const string Cdid = "doubao_cdid";
    }

    /// <summary>
    /// Speech recognition through the Doubao IME endpoint.
    /// <para>
    /// Audio is captured by the host, encoded to 20 ms Opus packets and streamed over the socket
    /// built in <see cref="DoubaoAsrSession"/>; recognised text is pushed back through
    /// <see cref="IAsrSink"/>. No connection is opened until there is audio to send, so a silent
    /// machine never holds an idle session.
    /// </para>
    /// </summary>
    public sealed class DoubaoAsrProvider : IAsrProvider
    {
        public const string ProviderId = "doubao-asr";

        /// <summary>
        /// The service auto-detects the language (the session configuration has no language field),
        /// so the host's language setting does not drive recognition here.
        /// </summary>
        private static readonly AsrLanguage[] Languages =
        {
            new("auto", "自动检测（普通话）", null, Verified: false),
        };

        private readonly DoubaoCredentials? credentials;
        private readonly Func<IDoubaoTransport>? transportFactory;
        private readonly DoubaoStreamOptions streamOptions;

        private CancellationTokenSource? cancellation;
        private Task? worker;
        private IAudioSource? audioSource;
        private EventHandler<AudioFrameEventArgs>? frameHandler;
        private Channel<byte[]>? queue;

        public DoubaoAsrProvider(
            DoubaoCredentials? credentials = null,
            Func<IDoubaoTransport>? transportFactory = null,
            DoubaoStreamOptions? streamOptions = null)
        {
            this.credentials = credentials;
            this.transportFactory = transportFactory;
            this.streamOptions = streamOptions ?? new DoubaoStreamOptions();
        }

        public string Id => ProviderId;

        public string DisplayName => "豆包语音输入（Doubao IME）";

        public bool RequiresAudioSource => true;

        public bool LanguageIsHostControlled => false;

        public IReadOnlyList<AsrLanguage> SupportedLanguages => Languages;

        /// <summary>False until a device_id/app_key pair has been registered.</summary>
        public bool IsAvailable => credentials?.IsComplete == true;

        public string? UnavailableReason =>
            IsAvailable ? null : "豆包需要在设置里生成或填写设备凭证（device_id / app_key）。";

        /// <summary>Sessions opened so far; more than one means a reconnect happened.</summary>
        public int SessionsOpened { get; private set; }

        public async Task StartAsync(AsrContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.AudioSource is null)
                throw new InvalidOperationException("doubao-asr needs an audio source to recognise from.");

            if (credentials is not { IsComplete: true })
                throw new InvalidOperationException(UnavailableReason);

            audioSource = context.AudioSource;
            queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            frameHandler = (_, frame) => queue.Writer.TryWrite(frame.Pcm);
            audioSource.FrameAvailable += frameHandler;

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            worker = Task.Run(() => RunAsync(context, cancellation.Token), CancellationToken.None);

            await audioSource.StartAsync(cancellationToken);
        }

        public async Task StopAsync()
        {
            if (audioSource is not null && frameHandler is not null)
                audioSource.FrameAvailable -= frameHandler;

            frameHandler = null;
            queue?.Writer.TryComplete();

            var running = worker;
            worker = null;

            if (running is not null)
            {
                if (await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5))) != running)
                {
                    try
                    {
                        await cancellation!.CancelAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                try
                {
                    await running;
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (audioSource is not null)
            {
                await audioSource.StopAsync();
                audioSource = null;
            }

            cancellation?.Dispose();
            cancellation = null;
            queue = null;
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task RunAsync(AsrContext context, CancellationToken token)
        {
            var channel = queue!;
            var streamer = new DoubaoAsrStreamer(
                () => new DoubaoAsrSession(credentials!.DeviceId, credentials.AppKey, transportFactory, vadWindowMs: streamOptions.VadWindowMs),
                streamOptions);

            using var encoder = new DoubaoOpusEncoder();
            var framer = new DoubaoPcmFramer();

            long timestampMs = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Lazy: do not open a socket until there is something to recognise.
                    if (!await WaitForInputAsync(channel, token))
                        break;

                    DoubaoAsrSession session;

                    try
                    {
                        session = await streamer.EnsureSessionAsync(token);
                        SessionsOpened = streamer.SessionsOpened;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        context.Sink.OnNotice($"[WARNING] 豆包连接失败：{exception.Message}");
                        await DelayAsync(streamOptions.ReconnectDelay, token);
                        continue;
                    }

                    using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var pump = PumpAsync(session, context.Sink, sessionCts.Token);

                    try
                    {
                        bool ended = await SendLoopAsync(
                            session, channel, framer, encoder, context.Sink, token, sessionCts.Token,
                            () => timestampMs, value => timestampMs = value);

                        if (ended)
                        {
                            await session.FinishAsync(sessionCts.Token);
                            await WaitForPumpAsync(pump, TimeSpan.FromSeconds(10));
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        context.Sink.OnNotice($"[WARNING] 豆包连接异常：{exception.Message}");
                        await streamer.InvalidateAsync();
                        await DelayAsync(streamOptions.ReconnectDelay, token);
                        continue;
                    }
                    finally
                    {
                        try
                        {
                            await sessionCts.CancelAsync();
                        }
                        catch (ObjectDisposedException)
                        {
                        }

                        await streamer.InvalidateAsync();
                    }

                    if (channel.Reader.Completion.IsCompleted)
                        break;

                    streamer.MarkHealthy();
                }
            }
            finally
            {
                await streamer.DisposeAsync();
                context.Sink.OnNotice(string.Empty);
            }
        }

        private async Task<bool> SendLoopAsync(
            DoubaoAsrSession session,
            Channel<byte[]> channel,
            DoubaoPcmFramer framer,
            DoubaoOpusEncoder encoder,
            IAsrSink sink,
            CancellationToken token,
            CancellationToken sessionToken,
            Func<long> readTimestamp,
            Action<long> writeTimestamp)
        {
            bool first = true;
            bool any = false;

            while (!token.IsCancellationRequested)
            {
                if (channel.Reader.TryRead(out var pcm))
                {
                    any = true;

                    foreach (var frame in framer.Push(pcm))
                    {
                        var packets = encoder.Encode(frame);
                        long timestamp = readTimestamp();

                        await session.SendAudioAsync(
                            packets[0],
                            first ? DoubaoFrameState.First : DoubaoFrameState.Middle,
                            timestamp,
                            sessionToken);

                        first = false;
                        writeTimestamp(timestamp + DoubaoOpusEncoder.FrameMilliseconds);
                    }

                    continue;
                }

                // Nothing captured: keep the stream alive with silence rather than idling out.
                if (any)
                {
                    await session.SendAudioAsync(
                        encoder.EncodeFrame(new short[DoubaoOpusEncoder.FrameSamples]),
                        DoubaoFrameState.Middle,
                        readTimestamp(),
                        sessionToken);

                    writeTimestamp(readTimestamp() + DoubaoOpusEncoder.FrameMilliseconds);
                }

                try
                {
                    if (!await channel.Reader.WaitToReadAsync(sessionToken))
                        return true;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return false;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return false;
        }

        private static async Task PumpAsync(DoubaoAsrSession session, IAsrSink sink, CancellationToken token)
        {
            await session.PumpAsync(
                segment =>
                {
                    if (segment.IsFinal)
                        sink.OnFinal(segment.Text);
                    else
                        sink.OnPartial(segment.Text);

                    return Task.CompletedTask;
                },
                token);
        }

        private static async Task WaitForPumpAsync(Task pump, TimeSpan timeout)
        {
            try
            {
                await pump.WaitAsync(timeout);
            }
            catch (Exception)
            {
                // Best effort: results that arrived are already published.
            }
        }

        private static async Task<bool> WaitForInputAsync(Channel<byte[]> channel, CancellationToken token)
        {
            try
            {
                return await channel.Reader.WaitToReadAsync(token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static async Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            if (delay <= TimeSpan.Zero)
                return;

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Re-chunks arbitrary PCM into the exact 320-sample frames Opus needs, carrying the remainder
    /// across calls and zero padding the final partial frame on flush.
    /// </summary>
    public sealed class DoubaoPcmFramer
    {
        private readonly List<short> pending = new(DoubaoOpusEncoder.FrameSamples * 2);

        public IReadOnlyList<short[]> Push(ReadOnlySpan<byte> pcm)
        {
            for (int i = 0; i + 1 < pcm.Length; i += 2)
                pending.Add(BitConverter.ToInt16(pcm.Slice(i, 2)));

            var frames = new List<short[]>(pending.Count / DoubaoOpusEncoder.FrameSamples);

            while (pending.Count >= DoubaoOpusEncoder.FrameSamples)
            {
                var frame = new short[DoubaoOpusEncoder.FrameSamples];

                for (int i = 0; i < frame.Length; i++)
                    frame[i] = pending[i];

                pending.RemoveRange(0, DoubaoOpusEncoder.FrameSamples);
                frames.Add(frame);
            }

            return frames;
        }

        public IReadOnlyList<short[]> Flush()
        {
            if (pending.Count == 0)
                return Array.Empty<short[]>();

            var frame = new short[DoubaoOpusEncoder.FrameSamples];

            for (int i = 0; i < pending.Count; i++)
                frame[i] = pending[i];

            pending.Clear();
            return new[] { frame };
        }
    }
}
