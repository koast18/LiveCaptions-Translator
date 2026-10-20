namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>How the streamer retries and how long it waits between attempts.</summary>
    public sealed class DoubaoStreamOptions
    {
        public int MaxConsecutiveFailures { get; init; } = 5;

        public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

        public int VadWindowMs { get; init; } = DoubaoSessionConfig.DefaultVadWindowMs;
    }

    /// <summary>
    /// Owns one live session at a time and re-establishes it when the link breaks.
    /// <para>
    /// Nothing is connected up front: <see cref="EnsureSessionAsync"/> is what opens the socket, so
    /// a machine that is not playing anything never holds an idle connection. The caller decides
    /// when to call it (the provider does so only once audio has arrived) and calls
    /// <see cref="Invalidate"/> when a session faults.
    /// </para>
    /// </summary>
    public sealed class DoubaoAsrStreamer : IAsyncDisposable
    {
        private readonly Func<DoubaoAsrSession> sessionFactory;
        private readonly DoubaoStreamOptions options;

        private DoubaoAsrSession? session;

        public DoubaoAsrStreamer(
            Func<DoubaoAsrSession> sessionFactory,
            DoubaoStreamOptions? options = null)
        {
            this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            this.options = options ?? new DoubaoStreamOptions();
        }

        /// <summary>Sessions opened so far; more than one means a reconnect happened.</summary>
        public int SessionsOpened { get; private set; }

        /// <summary>Consecutive failures since the last healthy session.</summary>
        public int ConsecutiveFailures { get; private set; }

        public DoubaoAsrSession? Current => session;

        /// <summary>
        /// Returns a session that has completed the StartTask / StartSession handshake, opening a
        /// new one when there is none.
        /// </summary>
        public async Task<DoubaoAsrSession> EnsureSessionAsync(CancellationToken cancellationToken = default)
        {
            if (session is not null)
                return session;

            if (ConsecutiveFailures > options.MaxConsecutiveFailures)
            {
                throw new DoubaoSessionException(
                    $"连续 {ConsecutiveFailures} 次失败，已停止重连");
            }

            var created = sessionFactory();

            try
            {
                await created.OpenAsync(cancellationToken);
                await created.StartTaskAsync(cancellationToken);
                await created.StartSessionAsync(cancellationToken);
            }
            catch (Exception)
            {
                await created.DisposeAsync();
                ConsecutiveFailures++;
                throw;
            }

            session = created;
            SessionsOpened++;
            return created;
        }

        /// <summary>Marks a healthy session, resetting the failure counter.</summary>
        public void MarkHealthy() => ConsecutiveFailures = 0;

        /// <summary>Drops the current session; the next <see cref="EnsureSessionAsync"/> reconnects.</summary>
        public async Task InvalidateAsync()
        {
            var active = session;
            session = null;

            if (active is null)
                return;

            await active.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            var active = session;
            session = null;

            if (active is not null)
                await active.DisposeAsync();
        }
    }
}
