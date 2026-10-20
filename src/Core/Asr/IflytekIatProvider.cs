using System.Text;
using System.Threading.Channels;

using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Core.Asr
{
    /// <summary>
    /// Speech recognition through iFlytek's streaming IAT endpoint.
    /// <para>
    /// The provider consumes <see cref="IAudioSource"/> PCM, slices it into 40 ms frames and
    /// streams them over a signed WebSocket. A session is re-established automatically when the
    /// connection drops; text recognised before the drop is kept so the transcript keeps growing.
    /// </para>
    /// </summary>
    public sealed class IflytekIatProvider : IAsrProvider
    {
        public const string ProviderId = "iflytek-iat";

        /// <summary>
        /// Language/accent pairs offered to the user. The endpoint answers <c>code 0</c> for every
        /// value it is handed, so a language is only marked <c>Verified</c> once a real speech
        /// sample has been recognised through it - see the language table in the README.
        /// </summary>
        private static readonly AsrLanguage[] Languages =
        {
            // Verified by tests/fixtures/iflytek-zh-cn-16k.wav in the CI integration job.
            new("zh_cn", "中文（普通话）", "mandarin", Verified: true),
            new("zh_cn", "中文（粤语）", "cantonese"),
            new("en_us", "English (US)", "mandarin"),
            new("ja", "日本語", "mandarin"),
            new("ko", "한국어", "mandarin"),
        };

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

        private readonly IflytekIatOptions? configuredOptions;
        private readonly Func<IIflytekTransport> transportFactory;

        private IflytekIatOptions settings = new();
        private Channel<byte[]>? queue;
        private CancellationTokenSource? cancellation;
        private Task? worker;
        private IAudioSource? audioSource;
        private EventHandler<AudioFrameEventArgs>? frameHandler;

        public IflytekIatProvider(
            IflytekIatOptions? options = null,
            Func<IIflytekTransport>? transportFactory = null)
        {
            configuredOptions = options;
            this.transportFactory = transportFactory ?? (() => new WebSocketIflytekTransport());
        }

        public string Id => ProviderId;

        public string DisplayName => "讯飞语音听写（IAT）";

        public bool RequiresAudioSource => true;

        public IReadOnlyList<AsrLanguage> SupportedLanguages => Languages;

        /// <summary>Number of sessions opened so far; more than one means a reconnect happened.</summary>
        public int SessionsOpened { get; private set; }

        public async Task StartAsync(AsrContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.AudioSource is null)
                throw new InvalidOperationException("iflytek-iat needs an audio source to recognise from.");

            // An explicitly configured provider (tests) keeps its options; otherwise the
            // language, accent and credentials come from the host settings.
            settings = configuredOptions ?? IflytekIatOptions.FromAsrOptions(context.Options);

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

            // Let the worker flush its final frame before tearing the session down.
            queue?.Writer.TryComplete();

            var running = worker;
            worker = null;

            if (running is not null)
            {
                if (await Task.WhenAny(running, Task.Delay(settings.GracefulStopTimeout)) != running)
                    await CancelAsync(cancellation);

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
            var committed = new StringBuilder();
            int failures = 0;
            int idleCloses = 0;

            while (!token.IsCancellationRequested)
            {
                // Do not hold a connection open while nothing is playing: WASAPI loopback emits
                // nothing at all when the machine is silent, and the service closes an empty stream.
                if (!await WaitForInputAsync(channel, token))
                    break;

                var session = new IflytekIatSession(settings, transportFactory());
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task<bool>? pump = null;
                bool inputEnded = false;
                bool finished = false;
                bool failed = false;

                try
                {
                    await session.OpenAsync(sessionCts.Token);
                    SessionsOpened++;

                    if (settings.PadWithLeadingSilence && committed.Length == 0)
                        await session.SendLeadingSilenceAsync(sessionCts.Token);

                    pump = session.PumpAsync(
                        text => PublishPartial(context.Sink, committed, text),
                        sessionCts.Token);

                    inputEnded = await SendLoopAsync(session, channel, pump, sessionCts.Token);

                    if (inputEnded)
                    {
                        // The input is over: give the service a moment to flush its last results.
                        finished = await TryDrainAsync(pump, settings.GracefulStopTimeout);

                        if (finished)
                            context.Sink.OnFinal(committed + session.Transcript.Text);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    failed = true;
                    failures++;
                    context.Sink.OnNotice($"[WARNING] 讯飞连接异常：{exception.Message}");
                }
                finally
                {
                    await CancelAsync(sessionCts);

                    if (pump is not null)
                    {
                        try
                        {
                            await pump;
                        }
                        catch (Exception)
                        {
                            // Already reported through the catch above.
                        }
                    }

                    await session.DisposeAsync();
                }

                // A session that actually streamed audio proves the link works again.
                if (!failed && session.SentFrames > 0)
                {
                    failures = 0;
                    idleCloses = 0;
                }

                // `inputEnded` means the host is shutting the provider down, not a dropped link.
                if (token.IsCancellationRequested || inputEnded)
                    break;

                // Keep whatever was recognised so the transcript does not restart on reconnect.
                committed.Append(session.Transcript.Text);

                // Nothing was ever streamed, so the socket closed on an idle stream rather than on
                // a real failure: retry quietly, but not forever.
                if (!failed && session.SentFrames == 0)
                {
                    idleCloses++;

                    if (idleCloses > settings.MaxReconnectAttempts)
                    {
                        context.Sink.OnError(new IflytekIatException(
                            -1, $"讯飞连接连续 {idleCloses} 次被服务端关闭，已停止重连"));
                        break;
                    }
                }
                else if (failures > settings.MaxReconnectAttempts)
                {
                    context.Sink.OnError(new IflytekIatException(
                        -1, $"重连 {failures} 次仍未成功，已停止讯飞识别"));
                    break;
                }
                else
                {
                    context.Sink.OnNotice(
                        $"[WARNING] 讯飞连接已断开，正在重连（{failures}/{settings.MaxReconnectAttempts}）…");
                }

                try
                {
                    await Task.Delay(settings.ReconnectDelay, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            context.Sink.OnNotice(string.Empty);
        }

        /// <summary>
        /// Drains audio from the queue and streams it. Returns <c>true</c> when the input ended
        /// (so the final frame was sent), <c>false</c> when the session ended first.
        /// </summary>
        private async Task<bool> SendLoopAsync(
            IflytekIatSession session,
            Channel<byte[]> channel,
            Task<bool> pump,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (pump.IsFaulted)
                    await pump;

                if (pump.IsCompleted)
                    return false;

                if (channel.Reader.TryRead(out var pcm))
                {
                    await session.SendAudioAsync(pcm, token);
                    continue;
                }

                using var poll = CancellationTokenSource.CreateLinkedTokenSource(token);
                poll.CancelAfter(PollInterval);

                try
                {
                    if (!await channel.Reader.WaitToReadAsync(poll.Token))
                    {
                        // Input finished: flush and let the service finish the transcript.
                        await session.FinishAsync(token);
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Nothing captured for one interval: keep the stream alive with silence.
                    // Otherwise the service closes the idle connection and we reconnect in a loop.
                    await session.SendAudioAsync(IflytekProtocol.SilenceChunk(), token);
                }
            }

            return false;
        }

        /// <summary>
        /// Blocks until at least one captured chunk is queued. Returns <c>false</c> when the input
        /// has ended (the host completed the channel).
        /// </summary>
        private static async Task<bool> WaitForInputAsync(Channel<byte[]> channel, CancellationToken token)
        {
            try
            {
                // WaitToReadAsync does not consume, so the send loop still sees the chunk.
                return await channel.Reader.WaitToReadAsync(token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static async Task<bool> TryDrainAsync(Task<bool> pump, TimeSpan timeout)
        {
            try
            {
                return await pump.WaitAsync(timeout);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Task PublishPartial(IAsrSink sink, StringBuilder committed, string text)
        {
            sink.OnPartial(committed.Length == 0 ? text : committed + text);
            return Task.CompletedTask;
        }

        private static async Task CancelAsync(CancellationTokenSource? source)
        {
            if (source is null)
                return;

            try
            {
                await source.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
