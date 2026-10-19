using System.Net.WebSockets;
using System.Text;

namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>
    /// The WebSocket half of an IAT session. Abstracted so the reconnect logic can be tested
    /// without a network connection.
    /// </summary>
    public interface IIflytekTransport : IAsyncDisposable
    {
        bool IsOpen { get; }

        Task ConnectAsync(Uri url, CancellationToken cancellationToken);

        Task SendAsync(string frame, CancellationToken cancellationToken);

        /// <summary>Returns <c>null</c> when the server closed the connection.</summary>
        Task<string?> ReceiveAsync(CancellationToken cancellationToken);
    }

    /// <summary>Real transport, backed by <see cref="ClientWebSocket"/>.</summary>
    public sealed class WebSocketIflytekTransport : IIflytekTransport
    {
        private readonly ClientWebSocket socket = new();
        private readonly byte[] buffer = new byte[64 * 1024];

        public bool IsOpen => socket.State == WebSocketState.Open;

        public Task ConnectAsync(Uri url, CancellationToken cancellationToken) =>
            socket.ConnectAsync(url, cancellationToken);

        public Task SendAsync(string frame, CancellationToken cancellationToken) =>
            socket.SendAsync(
                Encoding.UTF8.GetBytes(frame),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var payload = new MemoryStream();

            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                payload.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(payload.ToArray());
            }
        }

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
