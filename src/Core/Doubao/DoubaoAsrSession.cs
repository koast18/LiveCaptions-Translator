using System.Globalization;

namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>Raised when the service rejects a step of the session.</summary>
    public sealed class DoubaoSessionException : Exception
    {
        public DoubaoSessionException(string message)
            : base(message)
        {
        }

        public DoubaoSessionException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// One ASR session: <c>StartTask</c> → <c>StartSession</c> → many <c>TaskRequest</c> →
    /// <c>FinishSession</c>, with results read from the same socket.
    /// <para>
    /// Reconnecting is the provider's job; this type owns exactly one connection.
    /// </para>
    /// </summary>
    public sealed class DoubaoAsrSession : IAsyncDisposable
    {
        private readonly string deviceId;
        private readonly string appKey;
        private readonly Func<IDoubaoTransport> transportFactory;
        private readonly TimeProvider clock;
        private readonly int vadWindowMs;
        private readonly List<string> requestIds = [];

        private IDoubaoTransport? transport;

        public DoubaoAsrSession(
            string deviceId,
            string appKey,
            Func<IDoubaoTransport>? transportFactory = null,
            TimeProvider? clock = null,
            int vadWindowMs = DoubaoSessionConfig.DefaultVadWindowMs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

            this.deviceId = deviceId;
            this.appKey = appKey;
            this.transportFactory = transportFactory ?? (() => new WebSocketDoubaoTransport());
            this.clock = clock ?? TimeProvider.System;
            this.vadWindowMs = vadWindowMs;
        }

        public string? TaskId { get; private set; }

        public int SentPackets { get; private set; }

        /// <summary>Request ids issued so far, in order; each protocol message gets its own.</summary>
        public IReadOnlyList<string> RequestIds => requestIds;

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            transport = transportFactory();

            await transport.ConnectAsync(
                new Uri(DoubaoEndpoints.BuildWebSocketUri(deviceId)),
                DoubaoEndpoints.BuildHeaders(),
                cancellationToken);
        }

        public async Task StartTaskAsync(CancellationToken cancellationToken = default)
        {
            var response = await ExchangeAsync(DoubaoMethods.StartTask, null, cancellationToken);
            EnsureAccepted(response, DoubaoMethods.StartTask);
            TaskId = response.TaskId;
        }

        public async Task StartSessionAsync(CancellationToken cancellationToken = default)
        {
            var response = await ExchangeAsync(
                DoubaoMethods.StartSession,
                DoubaoSessionConfig.Build(deviceId, vadWindowMs),
                cancellationToken);

            EnsureAccepted(response, DoubaoMethods.StartSession);
        }

        /// <summary>Sends one Opus packet as a <c>TaskRequest</c>.</summary>
        public async Task SendAudioAsync(
            byte[] opusPacket,
            DoubaoFrameState frameState,
            long timestampMs,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(opusPacket);

            string payload = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"extra\":{{}},\"timestamp_ms\":{timestampMs}}}");

            await SendAsync(
                DoubaoAsrRequest.Encode(appKey, DoubaoMethods.TaskRequest, NewRequestId(), payload, opusPacket, frameState),
                cancellationToken);

            SentPackets++;
        }

        /// <summary>
        /// Sends <c>FinishSession</c>. It does not read: the pump is the only reader, so the final
        /// results are drained there until the service reports <c>SessionFinished</c>.
        /// </summary>
        public Task FinishAsync(CancellationToken cancellationToken = default) =>
            SendAsync(
                DoubaoAsrRequest.Encode(appKey, DoubaoMethods.FinishSession, NewRequestId()),
                cancellationToken);

        /// <summary>
        /// Reads messages until the session ends, invoking <paramref name="onSegment"/> for every
        /// result entry. Returns normally on <c>SessionFinished</c>.
        /// </summary>
        public async Task PumpAsync(
            Func<DoubaoResultSegment, Task>? onSegment = null,
            CancellationToken cancellationToken = default)
        {
            var active = transport ?? throw new InvalidOperationException("the session is not open");

            while (true)
            {
                byte[]? raw = await active.ReceiveAsync(cancellationToken);

                if (raw is null)
                    return;

                var response = DoubaoAsrResponse.Parse(raw);

                if (response.IsFailure)
                {
                    throw new DoubaoSessionException(
                        $"{response.MessageType} 失败：status={response.StatusCode} {response.StatusMessage}");
                }

                foreach (var segment in DoubaoResultParser.Parse(response.ResultJson))
                {
                    if (onSegment is not null)
                        await onSegment(segment);
                }

                if (response.IsFinal)
                    return;
            }
        }

        public ValueTask DisposeAsync() =>
            transport is null ? ValueTask.CompletedTask : transport.DisposeAsync();

        private async Task<DoubaoAsrResponse> ExchangeAsync(
            string method,
            string? payload,
            CancellationToken cancellationToken)
        {
            await SendAsync(
                DoubaoAsrRequest.Encode(appKey, method, NewRequestId(), payload),
                cancellationToken);

            var active = transport ?? throw new InvalidOperationException("the session is not open");
            byte[]? raw = await active.ReceiveAsync(cancellationToken);

            if (raw is null)
                throw new DoubaoSessionException($"{method} 期间连接被关闭");

            return DoubaoAsrResponse.Parse(raw);
        }

        private Task SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            var active = transport ?? throw new InvalidOperationException("the session is not open");
            return active.SendAsync(frame, cancellationToken);
        }

        private string NewRequestId()
        {
            string id = Guid.NewGuid().ToString();
            requestIds.Add(id);
            return id;
        }

        private static void EnsureAccepted(DoubaoAsrResponse response, string method)
        {
            if (response.IsOk)
                return;

            throw new DoubaoSessionException(
                $"{method} 被拒绝：status={response.StatusCode} " +
                $"message_type={response.MessageType} {response.StatusMessage}".TrimEnd());
        }
    }
}
