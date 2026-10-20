using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>Endpoint, headers and session configuration of the ASR socket.</summary>
    public static class DoubaoEndpoints
    {
        public const string AsrWebSocketUrl = "wss://frontier-audio-ime-ws.doubao.com/ocean/api/v1/ws";

        public const string ProtoVersion = "v2";

        public const string KeepAliveHeader = "x-custom-keepalive";

        public static string BuildWebSocketUri(string deviceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

            return $"{AsrWebSocketUrl}?aid={DoubaoDeviceProfile.Aid}&device_id={Uri.EscapeDataString(deviceId)}";
        }

        public static IReadOnlyDictionary<string, string> BuildHeaders() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = DoubaoDeviceProfile.UserAgent,
                ["proto-version"] = ProtoVersion,
                [KeepAliveHeader] = "true",
            };
    }

    /// <summary>Builds the JSON payload of <c>StartSession</c>.</summary>
    public static class DoubaoSessionConfig
    {
        public const int DefaultVadWindowMs = 200;

        public static string Build(string deviceId, int vadWindowMs = DefaultVadWindowMs, bool useTwoPassRetry = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

            var builder = new StringBuilder(320);
            builder.Append("{\"audio_info\":{\"channel\":1,\"format\":\"speech_opus\",\"sample_rate\":16000}")
                   .Append(",\"enable_punctuation\":true")
                   .Append(",\"enable_speech_rejection\":false")
                   .Append(",\"extra\":{")
                   .Append("\"app_name\":\"oime\"")
                   .Append(",\"cell_compress_rate\":8")
                   .Append(",\"did\":").Append(JsonSerializer.Serialize(deviceId))
                   .Append(",\"enable_asr_threepass\":true")
                   .Append(",\"enable_asr_twopass\":true")
                   .Append(",\"input_mode\":\"tool\"")
                   .Append(",\"end_smooth_window_ms\":").Append(vadWindowMs)
                   .Append(",\"use_twopass_retry\":").Append(useTwoPassRetry ? "true" : "false")
                   .Append("}}");

            return builder.ToString();
        }
    }

    /// <summary>One entry of <c>results[]</c> inside a <c>TaskResult</c>.</summary>
    public sealed record DoubaoResultSegment(
        string Text,
        bool IsInterim,
        bool IsVadFinished,
        bool IsForceFinished,
        bool IsVadTimeout,
        bool IsStreamFinish)
    {
        /// <summary>Start of the recognised span in seconds; a new value means a new segment.</summary>
        public double StartTime { get; init; }

        public double EndTime { get; init; }

        /// <summary>True when the service will not revise this segment any more.</summary>
        public bool IsFinal =>
            IsStreamFinish || IsForceFinished || IsVadFinished || IsVadTimeout;
    }

    /// <summary>Decodes the <c>result_json</c> string that rides inside <see cref="DoubaoAsrResponse"/>.</summary>
    public static class DoubaoResultParser
    {
        public static IReadOnlyList<DoubaoResultSegment> Parse(string? resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson))
                return Array.Empty<DoubaoResultSegment>();

            using var document = JsonDocument.Parse(resultJson);

            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<DoubaoResultSegment>();
            }

            var segments = new List<DoubaoResultSegment>(results.GetArrayLength());

            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                segments.Add(new DoubaoResultSegment(
                    Text: ReadString(item, "text"),
                    IsInterim: ReadBool(item, "is_interim"),
                    IsVadFinished: ReadBool(item, "is_vad_finished"),
                    IsForceFinished: ReadBool(item, "is_force_finished"),
                    IsVadTimeout: ReadBool(item, "is_vad_timeout"),
                    IsStreamFinish: ReadBool(item, "stream_asr_finish"))
                {
                    StartTime = ReadDouble(item, "start_time"),
                    EndTime = ReadDouble(item, "end_time"),
                });
            }

            return segments;
        }

        /// <summary>Concatenates the text of every segment; each result carries its whole segment.</summary>
        public static string JoinText(IEnumerable<DoubaoResultSegment> segments) =>
            string.Concat(segments.Select(segment => segment.Text));

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static double ReadDouble(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : -1;

        private static bool ReadBool(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) &&
            value.ValueKind is JsonValueKind.True;
    }

    /// <summary>
    /// Turns the service's per-segment stream into the accumulated transcript the caption pipeline
    /// expects. A result revises the current segment; a result with a different <c>start_time</c>
    /// starts a new one and commits the previous text.
    /// </summary>
    public sealed class DoubaoTranscriptBuilder
    {
        private readonly System.Text.StringBuilder committed = new();
        private string current = string.Empty;
        private double currentStart = -1;

        /// <summary>Text already committed, i.e. segments that will not be revised again.</summary>
        public string Committed => committed.ToString();

        /// <summary>The segment currently being revised.</summary>
        public string Current => current;

        /// <summary>Full transcript seen so far.</summary>
        public string Text => committed + current;

        /// <summary>Applies one result and returns the transcript plus whether it was finalised.</summary>
        public (string Text, bool IsFinal) Append(DoubaoResultSegment segment)
        {
            ArgumentNullException.ThrowIfNull(segment);

            if (currentStart >= 0 && segment.StartTime > currentStart + 1e-6)
            {
                committed.Append(current);
                current = string.Empty;
            }

            if (segment.IsFinal)
            {
                committed.Append(segment.Text);
                current = string.Empty;
                currentStart = -1;

                return (committed.ToString(), true);
            }

            currentStart = segment.StartTime;
            current = segment.Text;

            return (committed + current, false);
        }
    }

    /// <summary>The socket half of a session; abstracted so the flow can be tested without a network.</summary>
    public interface IDoubaoTransport : IAsyncDisposable
    {
        bool IsOpen { get; }

        Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);

        Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

        /// <summary>Returns <c>null</c> when the server closed the connection.</summary>
        Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken);
    }

    /// <summary>Real transport, backed by <see cref="ClientWebSocket"/>.</summary>
    public sealed class WebSocketDoubaoTransport : IDoubaoTransport
    {
        private readonly ClientWebSocket socket = new();

        public bool IsOpen => socket.State == WebSocketState.Open;

        public Task ConnectAsync(
            Uri uri,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            foreach (var header in headers)
                socket.Options.SetRequestHeader(header.Key, header.Value);

            return socket.ConnectAsync(uri, cancellationToken);
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
            // The Memory overload returns ValueTask on .NET 8.
            socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken).AsTask();

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];

            while (true)
            {
                var result = await socket.ReceiveAsync(chunk, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                buffer.Write(chunk, 0, result.Count);

                if (result.EndOfMessage)
                    return buffer.ToArray();
            }
        }

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
