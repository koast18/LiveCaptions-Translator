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
        /// Language/accent pairs the service accepted when probed. None of them are marked as
        /// verified: the endpoint answers <c>code 0</c> for every value it is handed, so only a
        /// real speech sample can prove that a language is actually recognised.
        /// </summary>
        private static readonly AsrLanguage[] Languages =
        {
            new("zh_cn", "中文（普通话）", "mandarin"),
            new("zh_cn", "中文（粤语）", "cantonese"),
            new("en_us", "English (US)", "mandarin"),
            new("ja", "日本語", "mandarin"),
            new("ko", "한국어", "mandarin"),
        };

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

        private readonly IflytekIatOptions options;
        private readonly Func<IIflytekTransport> transportFactory;

        private Channel<byte[]>? queue;
        private CancellationTokenSource? cancellation;
        private Task? worker;
        private IAudioSource? audioSource;
        private EventHandler<AudioFrameEventArgs>? frameHandler;

        public IflytekIatProvider(
            IflytekIatOptions? options = null,
            Func<IIflytekTransport>? transportFactory = null)
        {
            this.options = options ?? new IflytekIatOptions();
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
                if (await Task.WhenAny(running, Task.Delay(options.GracefulStopTimeout)) != running)
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

            while (!token.IsCancellationRequested)
            {
                var session = new IflytekIatSession(options, transportFactory());
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task<bool>? pump = null;
                bool inputEnded = false;
                bool finished = false;

                try
                {
                    await session.OpenAsync(sessionCts.Token);
                    SessionsOpened++;

                    if (options.PadWithLeadingSilence && committed.Length == 0)
                        await session.SendLeadingSilenceAsync(sessionCts.Token);

                    failures = 0;

                    pump = session.PumpAsync(
                        text => PublishPartial(context.Sink, committed, text),
                        sessionCts.Token);

                    inputEnded = await SendLoopAsync(session, channel, pump, sessionCts.Token);

                    if (inputEnded)
                    {
                        // The input is over: give the service a moment to flush its last results.
                        finished = await TryDrainAsync(pump, options.GracefulStopTimeout);

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

                // `inputEnded` means the host is shutting the provider down, not a dropped link.
                if (token.IsCancellationRequested || inputEnded)
                    break;

                // Keep whatever was recognised so the transcript does not restart on reconnect.
                committed.Append(session.Transcript.Text);

                if (failures > options.MaxReconnectAttempts)
                {
                    context.Sink.OnError(new IflytekIatException(
                        -1, $"重连 {failures} 次仍未成功，已停止讯飞识别"));
                    break;
                }

                context.Sink.OnNotice(
                    $"[WARNING] 讯飞连接已断开，正在重连（{failures}/{options.MaxReconnectAttempts}）…");

                try
                {
                    await Task.Delay(options.ReconnectDelay, token);
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
                    // Poll interval elapsed; loop round to notice a dead receive side.
                }
            }

            return false;
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
