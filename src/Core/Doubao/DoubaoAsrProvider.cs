using System.Threading.Channels;

using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Localization;
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

        /// <summary>
        /// Silence in milliseconds before the service closes a segment. Larger values give longer,
        /// calmer sentences; the reference client defaults to 200, which chops at every breath.
        /// </summary>
        public const string VadWindowMs = "doubao_vad_window_ms";
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

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// The service auto-detects the language (the session configuration has no language field),
        /// so the host's language setting does not drive recognition here.
        /// </summary>
        private static readonly AsrLanguage[] Languages =
        {
            // Verified by tests/fixtures/iflytek-zh-cn-16k.wav in the CI integration job: the
            // service detected the language and recognised the sample. Other languages are not
            // exercised, and the session configuration has no field to request one.
            new("auto", "自动检测（普通话）", null, Verified: true),
        };

        private readonly DoubaoCredentials? credentials;
        private readonly Func<IDoubaoTransport>? transportFactory;
        private readonly DoubaoStreamOptions streamOptions;

        private int vadWindowMs;

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
            IsAvailable ? null : CoreStrings.Get("Asr_DoubaoCredentialsMissing");

        /// <summary>Sessions opened so far; more than one means a reconnect happened.</summary>
        public int SessionsOpened { get; private set; }

        public async Task StartAsync(AsrContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.AudioSource is null)
                throw new InvalidOperationException("doubao-asr needs an audio source to recognise from.");

            if (credentials is not { IsComplete: true })
                throw new InvalidOperationException(UnavailableReason);

            vadWindowMs = ResolveVadWindowMs(context.Options);

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

        /// <summary>Reads the segmentation window from the host settings, clamped to a sane range.</summary>
        private int ResolveVadWindowMs(AsrOptions options)
        {
            if (options.Settings.TryGetValue(DoubaoSettingKeys.VadWindowMs, out string? raw) &&
                int.TryParse(raw, out int configured) &&
                configured is >= 100 and <= 5000)
            {
                return configured;
            }

            return streamOptions.VadWindowMs;
        }

        private async Task RunAsync(AsrContext context, CancellationToken token)
        {
            var channel = queue!;
            var streamer = new DoubaoAsrStreamer(
                () => new DoubaoAsrSession(credentials!.DeviceId, credentials.AppKey, transportFactory, vadWindowMs: vadWindowMs),
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
                        context.Sink.OnNotice(Localize(context, "Asr_ConnectFailed", exception.Message));
                        await DelayAsync(streamOptions.ReconnectDelay, token);
                        continue;
                    }

                    Volatile.Write(ref lastServerActivity, Environment.TickCount64);
                    long sessionStartedAt = Environment.TickCount64;

                    using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var pump = PumpAsync(session, context.Sink, sessionCts.Token);

                    try
                    {
                        bool ended = await SendLoopAsync(
                            session, channel, framer, encoder, pump, token, sessionCts.Token,
                            () => timestampMs, value => timestampMs = value, sessionStartedAt);

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
                    catch (AsrSessionStalledException)
                    {
                        // Planned recycle: say so, then open a new session straight away.
                        context.Sink.OnNotice(Localize(context, "Asr_SessionRecycled"));
                        await streamer.InvalidateAsync();
                        await DelayAsync(streamOptions.ReconnectDelay, token);
                        continue;
                    }
                    catch (Exception exception)
                    {
                        context.Sink.OnNotice(Localize(context, "Asr_ConnectionError", exception.Message));
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
            Task pump,
            CancellationToken token,
            CancellationToken sessionToken,
            Func<long> readTimestamp,
            Action<long> writeTimestamp,
            long sessionStartedAt)
        {
            bool first = true;
            bool any = false;

            while (!token.IsCancellationRequested)
            {
                // The receive side is the only thing that produces text. If it died or the service
                // ended the session, stop pushing audio into a dead socket and let the caller
                // reconnect - otherwise captions silently stop while the audio keeps flowing.
                if (pump.IsFaulted)
                    await pump;

                if (pump.IsCompleted)
                    return false;

                // A session that has lived its span, or one that has gone quiet while we keep sending
                // audio, is dead even though the socket still looks open. Hand it back to the caller so
                // it opens a fresh one instead of letting captions stop silently.
                if (Environment.TickCount64 - sessionStartedAt > streamOptions.MaxSessionDuration.TotalMilliseconds)
                    throw new AsrSessionStalledException("session age");

                if (Environment.TickCount64 - Volatile.Read(ref lastServerActivity) >
                    streamOptions.ServerSilenceTimeout.TotalMilliseconds)
                    throw new AsrSessionStalledException("no server activity");

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
                    long keepAliveAt = readTimestamp();

                    await session.SendAudioAsync(
                        encoder.EncodeFrame(new short[DoubaoOpusEncoder.FrameSamples]),
                        DoubaoFrameState.Middle,
                        keepAliveAt,
                        sessionToken);

                    writeTimestamp(keepAliveAt + DoubaoOpusEncoder.FrameMilliseconds);
                }

                try
                {
                    // Poll instead of blocking forever, so a dead pump is noticed even during a
                    // long silence.
                    using var poll = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                    poll.CancelAfter(PollInterval);

                    if (!await channel.Reader.WaitToReadAsync(poll.Token))
                    {
                        // The input ended: flush the tail and mark it LAST, the way the reference
                        // client closes a stream. Without it the service never sees a final frame.
                        var tail = framer.Flush();

                        if (tail.Count == 0)
                        {
                            await session.SendAudioAsync(
                                encoder.EncodeFrame(new short[DoubaoOpusEncoder.FrameSamples]),
                                DoubaoFrameState.Last,
                                readTimestamp(),
                                sessionToken);
                        }
                        else
                        {
                            foreach (var frame in tail)
                            {
                                await session.SendAudioAsync(
                                    encoder.Encode(frame)[0],
                                    DoubaoFrameState.Last,
                                    readTimestamp(),
                                    sessionToken);

                                writeTimestamp(readTimestamp() + DoubaoOpusEncoder.FrameMilliseconds);
                            }
                        }

                        return true;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && !sessionToken.IsCancellationRequested)
                {
                    // Just the poll interval: loop round and re-check the pump.
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads results and republishes them as an accumulated transcript: the service sends one
        /// segment at a time, and the caption pipeline expects the whole transcript.
        /// </summary>
        private long lastServerActivity;

        private async Task PumpAsync(DoubaoAsrSession session, IAsrSink sink, CancellationToken token)
        {
            var transcript = new DoubaoTranscriptBuilder();

            await session.PumpAsync(
                segment =>
                {
                    Volatile.Write(ref lastServerActivity, Environment.TickCount64);
                    var (text, isFinal) = transcript.Append(segment);

                    // The provider knows exactly which part of the transcript it will not revise:
                    // the builder commits a segment as soon as it is finished, and the caption
                    // pipeline only shows text that has settled (so a correction never duplicates).
                    string stableText = transcript.Committed;

                    if (isFinal)
                        sink.OnFinal(text, stableText);
                    else
                        sink.OnPartial(text, stableText);

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

        /// <summary>Uses the host localiser when one was injected, English otherwise.</summary>
        private static string Localize(AsrContext context, string key, params object?[] args) =>
            context.Localize?.Invoke(key, args) ?? key;

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
