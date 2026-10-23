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
    /// expects. A result revises the current segment; a result with a later <c>start_time</c> starts
    /// a new one and commits the previous text.
    /// <para>
    /// The start time of one segment is re-aligned by the service's second/third pass and its reported
    /// value <em>jitters</em> (0.02-0.4 s between consecutive results, recorded in
    /// tests/fixtures/bili-roundtable.wire.txt). Comparing it exactly treated every wobble as a new
    /// segment, so the growing text was appended again and again - the repeated, nested subtitle
    /// blocks users reported. Results are grouped by proximity instead: consecutive segments in the
    /// recordings are at least 1.0 s apart, while one segment never moves more than ~0.5 s per
    /// result.
    /// </para>
    /// </summary>
    public sealed class DoubaoTranscriptBuilder
    {
        /// <summary>How far a segment's reported start may move between two results.</summary>
        public const double StartToleranceSeconds = 0.8;

        /// <summary>
        /// The service's final pass can re-decode a window that reaches back into the previous
        /// segment, so the new text starts with what was already committed. Below this many
        /// characters an overlap is a coincidence (a repeated word), not a re-decoded window.
        /// </summary>
        public const int MinimumOverlapChars = 8;

        /// <summary>Longest overlap searched for; a re-decoded window is a few hundred characters.</summary>
        public const int MaximumOverlapChars = 600;

        private readonly System.Text.StringBuilder committed = new();
        private string current = string.Empty;
        private double lastStart = -1;

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

            bool sameSegment = lastStart >= 0 &&
                               Math.Abs(segment.StartTime - lastStart) <= StartToleranceSeconds;
            bool olderSegment = lastStart >= 0 &&
                                segment.StartTime < lastStart - StartToleranceSeconds;

            if (olderSegment)
            {
                // A late revision of a segment that has already been handed over: its text is part of
                // the transcript already, so appending it would duplicate that sentence.
                return (committed + current, segment.IsFinal);
            }

            if (!sameSegment && lastStart >= 0)
            {
                // A later segment has started; what the previous one reported so far is all of it.
                Commit(current);
                current = string.Empty;
            }

            if (segment.IsFinal)
            {
                Commit(segment.Text);
                current = string.Empty;
                lastStart = -1;

                return (committed.ToString(), true);
            }

            lastStart = segment.StartTime;

            // The live text must not repeat what the finished line already shows when the service
            // re-decodes a window that reaches back into it.
            current = WithoutOverlap(committed.ToString(), segment.Text);

            return (committed + current, false);
        }

        /// <summary>
        /// Appends a segment, replacing the tail it re-decodes instead of repeating it. The final pass
        /// re-covers earlier audio, so without this the same words appear as two sentences.
        /// </summary>
        private void Commit(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            string sofar = committed.ToString();
            int overlap = Overlap(sofar, text);

            committed.Clear();
            if (overlap > 0)
                committed.Append(sofar[..^overlap]);

            committed.Append(text);
        }

        /// <summary>The segment text with a re-decoded prefix removed, so the live line only shows new words.</summary>
        private static string WithoutOverlap(string sofar, string text)
        {
            int overlap = Overlap(sofar, text);
            return overlap > 0 ? text[overlap..] : text;
        }

        /// <summary>Longest suffix of <paramref name="sofar"/> that is a prefix of <paramref name="text"/>.</summary>
        private static int Overlap(string sofar, string text)
        {
            int max = Math.Min(Math.Min(sofar.Length, text.Length), MaximumOverlapChars);

            for (int length = max; length >= MinimumOverlapChars; length--)
            {
                if (sofar.AsSpan(sofar.Length - length).SequenceEqual(text.AsSpan(0, length)))
                    return length;
            }

            return 0;
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
