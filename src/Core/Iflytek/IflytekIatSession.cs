namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>
    /// One IAT session: connect, stream PCM as 40 ms frames, and reassemble the results.
    /// Reconnecting is the caller's job - see <see cref="Asr.IflytekIatProvider"/>.
    /// </summary>
    public sealed class IflytekIatSession : IAsyncDisposable
    {
        private readonly IflytekIatOptions options;
        private readonly IIflytekTransport transport;
        private readonly TimeProvider timeProvider;

        private bool firstFrameSent;

        public IflytekIatSession(
            IflytekIatOptions options,
            IIflytekTransport transport,
            TimeProvider? timeProvider = null)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>Text assembled from this session's results.</summary>
        public IflytekTranscript Transcript { get; } = new();

        public int SentFrames { get; private set; }

        public long SentBytes { get; private set; }

        /// <summary>The URL used for this session, useful for diagnostics.</summary>
        public string? SignedUrl { get; private set; }

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            SignedUrl = IflytekSignature.BuildSignedUrl(
                options.Credentials, timeProvider.GetUtcNow());

            await transport.ConnectAsync(new Uri(SignedUrl), cancellationToken);
        }

        /// <summary>Sends PCM, slicing it into protocol-sized frames.</summary>
        public async Task SendAudioAsync(
            ReadOnlyMemory<byte> pcm,
            CancellationToken cancellationToken = default)
        {
            foreach (var piece in IflytekProtocol.ChunkPcm(pcm.Span, options.ChunkBytes))
                await SendFrameAsync(firstFrameSent ? 1 : 0, piece, cancellationToken);
        }

        public Task SendLeadingSilenceAsync(CancellationToken cancellationToken = default) =>
            SendAudioAsync(IflytekProtocol.LeadingSilence(), cancellationToken);

        /// <summary>Sends the final frame, which tells the service to flush its results.</summary>
        public async Task FinishAsync(CancellationToken cancellationToken = default)
        {
            if (!firstFrameSent)
            {
                await transport.SendAsync(
                    IflytekProtocol.BuildFrame(0, ReadOnlySpan<byte>.Empty, options.Credentials, options.Business),
                    cancellationToken);

                firstFrameSent = true;
            }

            await SendFrameAsync(2, [], cancellationToken);
        }

        /// <summary>
        /// Reads results until the service ends the stream.
        /// Returns <c>true</c> on a normal end and <c>false</c> when the socket closed first.
        /// </summary>
        public async Task<bool> PumpAsync(
            Func<string, Task>? onPartial = null,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.ReceiveTimeout);

                string? raw;
                try
                {
                    raw = await transport.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IflytekIatException(-1, "等待讯飞识别结果超时");
                }

                if (raw is null)
                    return false;

                var message = IflytekResultParser.ParseMessage(raw);

                if (message.IsError)
                    throw new IflytekIatException(message.Code, message.Message ?? string.Empty);

                if (message.Result is { } result)
                {
                    string text = Transcript.Feed(result);

                    if (onPartial is not null)
                        await onPartial(text);
                }

                if (message.IsFinal)
                    return true;
            }
        }

        public ValueTask DisposeAsync() => transport.DisposeAsync();

        private async Task SendFrameAsync(
            int status,
            ReadOnlySpan<byte> audio,
            CancellationToken cancellationToken)
        {
            string frame = IflytekProtocol.BuildFrame(status, audio, options.Credentials, options.Business);

            await transport.SendAsync(frame, cancellationToken);

            firstFrameSent = true;
            SentFrames++;
            SentBytes += audio.Length;
        }
    }
}
