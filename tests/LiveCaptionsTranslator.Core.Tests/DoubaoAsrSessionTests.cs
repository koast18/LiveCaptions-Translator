using System.Text;

using LiveCaptionsTranslator.Core.Doubao;

namespace LiveCaptionsTranslator.Core.Tests;

public class DoubaoAsrSessionTests
{
    private const string DeviceId = "1782778396951964";
    private const string AppKey = "TOKEN-abc123";

    /// <summary>Scripted socket: records what was sent and replays canned responses.</summary>
    private sealed class FakeTransport : IDoubaoTransport
    {
        private readonly Queue<byte[]?> incoming = new();

        public List<byte[]> Sent { get; } = [];

        public Uri? Uri { get; private set; }

        public IReadOnlyDictionary<string, string>? Headers { get; private set; }

        public bool IsOpen { get; private set; }

        public int ReceiveCount { get; private set; }

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            Uri = uri;
            Headers = headers;
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            Sent.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveCount++;
            return Task.FromResult(incoming.Count > 0 ? incoming.Dequeue() : null);
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }

        public void Enqueue(byte[] data) => incoming.Enqueue(data);

        public void Close() => incoming.Enqueue(null);
    }

    private static byte[] Response(
        string messageType,
        int status = DoubaoStatusCodes.Ok,
        string? resultJson = null,
        string? taskId = null)
    {
        var buffer = new List<byte>();

        if (!string.IsNullOrEmpty(taskId))
            DoubaoProtoWriter.WriteString(buffer, 2, taskId);

        if (!string.IsNullOrEmpty(messageType))
            DoubaoProtoWriter.WriteString(buffer, 4, messageType);

        if (status != 0)
        {
            DoubaoProtoWriter.WriteTag(buffer, 5, DoubaoWireType.Varint);
            DoubaoProtoWriter.WriteVarint(buffer, (ulong)status);
        }

        if (resultJson is not null)
            DoubaoProtoWriter.WriteString(buffer, 7, resultJson);

        return buffer.ToArray();
    }

    private static string ResultsJson(params string[] texts) =>
        "{\"results\":[" +
        string.Join(",", texts.Select(text => $"{{\"text\":\"{text}\",\"is_interim\":true}}")) +
        "]}";

    private static Dictionary<int, object> Fields(byte[] frame)
    {
        var reader = new DoubaoProtoReader(frame);
        var fields = new Dictionary<int, object>();

        while (reader.TryReadTag(out int field, out int wireType))
        {
            fields[field] = wireType == DoubaoWireType.Varint
                ? (int)reader.ReadVarint()
                : reader.ReadLengthDelimited().ToArray();
        }

        return fields;
    }

    private static string Str(byte[] frame, int field) =>
        Encoding.UTF8.GetString((byte[])Fields(frame)[field]);

    private static int Num(byte[] frame, int field) => (int)Fields(frame)[field];

    private static bool Has(byte[] frame, int field) => Fields(frame).ContainsKey(field);

    private static (DoubaoAsrSession Session, FakeTransport Transport) Build()
    {
        var transport = new FakeTransport();
        var session = new DoubaoAsrSession(DeviceId, AppKey, () => transport);
        return (session, transport);
    }

    private static async Task<(DoubaoAsrSession Session, FakeTransport Transport)> OpenedAsync()
    {
        var (session, transport) = Build();
        transport.Enqueue(Response("TaskStarted", taskId: "task-1"));
        transport.Enqueue(Response("SessionStarted"));
        await session.OpenAsync();
        await session.StartTaskAsync();
        await session.StartSessionAsync();
        return (session, transport);
    }

    [Fact]
    public async Task The_socket_uri_and_headers_match_the_endpoint()
    {
        var (session, transport) = Build();
        await session.OpenAsync();

        Assert.NotNull(transport.Uri);
        Assert.Equal("wss", transport.Uri!.Scheme);
        Assert.Equal("frontier-audio-ime-ws.doubao.com", transport.Uri.Host);
        Assert.Equal("/ocean/api/v1/ws", transport.Uri.AbsolutePath);
        Assert.Contains("aid=401734", transport.Uri.Query);
        Assert.Contains($"device_id={DeviceId}", transport.Uri.Query);

        Assert.NotNull(transport.Headers);
        Assert.Equal("v2", transport.Headers!["proto-version"]);
        Assert.Equal("true", transport.Headers["x-custom-keepalive"]);
        Assert.Equal(DoubaoDeviceProfile.UserAgent, transport.Headers["User-Agent"]);
    }

    [Fact]
    public async Task Start_task_and_start_session_are_exchanged_before_any_audio()
    {
        var (session, transport) = await OpenedAsync();

        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal("StartTask", Str(transport.Sent[0], 5));
        Assert.Equal("ASR", Str(transport.Sent[0], 3));
        Assert.Equal(AppKey, Str(transport.Sent[0], 2));
        Assert.False(Has(transport.Sent[0], 6));
        Assert.False(Has(transport.Sent[0], 7));

        Assert.Equal("StartSession", Str(transport.Sent[1], 5));
        string payload = Str(transport.Sent[1], 6);
        Assert.Contains("\"format\":\"speech_opus\"", payload);
        Assert.Contains("\"sample_rate\":16000", payload);
        Assert.Contains($"\"did\":\"{DeviceId}\"", payload);
        Assert.Contains("\"enable_asr_twopass\":true", payload);

        Assert.Equal("task-1", session.TaskId);
    }

    [Fact]
    public async Task Every_message_gets_its_own_request_id()
    {
        var (session, transport) = await OpenedAsync();

        await session.SendAudioAsync([1, 2, 3], DoubaoFrameState.First, 1000);
        await session.FinishAsync();

        Assert.Equal(4, session.RequestIds.Count);
        Assert.Equal(session.RequestIds.Count, session.RequestIds.Distinct().Count());

        // The request id that goes on the wire is the one recorded.
        Assert.Equal(session.RequestIds[2], Str(transport.Sent[2], 8));
    }

    [Fact]
    public async Task Audio_packets_carry_frame_state_timestamp_and_payload()
    {
        var (session, transport) = await OpenedAsync();

        await session.SendAudioAsync([9, 8, 7], DoubaoFrameState.First, 1_700_000_000_000);
        await session.SendAudioAsync([6, 5], DoubaoFrameState.Middle, 1_700_000_000_020);
        await session.SendAudioAsync([], DoubaoFrameState.Last, 1_700_000_000_040);

        var first = transport.Sent[2];
        Assert.Equal("TaskRequest", Str(first, 5));
        Assert.Equal((int)DoubaoFrameState.First, Num(first, 9));
        Assert.Equal(new byte[] { 9, 8, 7 }, (byte[])Fields(first)[7]);
        Assert.Contains("\"timestamp_ms\":1700000000000", Str(first, 6));

        Assert.Equal((int)DoubaoFrameState.Middle, Num(transport.Sent[3], 9));
        Assert.Equal((int)DoubaoFrameState.Last, Num(transport.Sent[4], 9));

        // The last packet carries no audio, so field 7 is absent.
        Assert.False(Has(transport.Sent[4], 7));
        Assert.Equal(3, session.SentPackets);
    }

    [Fact]
    public async Task Finish_session_is_sent_without_consuming_a_response()
    {
        var (session, transport) = await OpenedAsync();
        int readsBefore = transport.ReceiveCount;

        await session.FinishAsync();

        Assert.Equal("FinishSession", Str(transport.Sent[^1], 5));
        Assert.Equal(readsBefore, transport.ReceiveCount);
    }

    [Fact]
    public async Task Pump_reports_every_segment_until_the_session_finishes()
    {
        var (session, transport) = await OpenedAsync();

        transport.Enqueue(Response("TaskResult", resultJson: ResultsJson("你好")));
        transport.Enqueue(Response("TaskResult", resultJson: ResultsJson("你好，世界")));
        transport.Enqueue(Response("SessionFinished"));

        var segments = new List<DoubaoResultSegment>();

        await session.PumpAsync(segment =>
        {
            segments.Add(segment);
            return Task.CompletedTask;
        });

        Assert.Equal(2, segments.Count);
        Assert.Equal("你好", segments[0].Text);
        Assert.Equal("你好，世界", segments[1].Text);
        Assert.All(segments, segment => Assert.True(segment.IsInterim));
        Assert.All(segments, segment => Assert.False(segment.IsFinal));
    }

    [Fact]
    public async Task A_finished_segment_is_flagged_final()
    {
        var (session, transport) = await OpenedAsync();

        transport.Enqueue(Response("TaskResult",
            resultJson: "{\"results\":[{\"text\":\"完成\",\"is_vad_finished\":true}]}"));
        transport.Enqueue(Response("SessionFinished"));

        var segments = new List<DoubaoResultSegment>();
        await session.PumpAsync(segment =>
        {
            segments.Add(segment);
            return Task.CompletedTask;
        });

        Assert.True(segments[0].IsFinal);
        Assert.Equal("完成", segments[0].Text);
    }

    [Fact]
    public async Task A_session_failure_is_raised_with_the_server_message()
    {
        var (session, transport) = await OpenedAsync();

        transport.Enqueue(Response("SessionFailed", status: 45000001));

        var error = await Assert.ThrowsAsync<DoubaoSessionException>(() => session.PumpAsync());

        Assert.Contains("45000001", error.Message);
        Assert.Contains("SessionFailed", error.Message);
    }

    [Fact]
    public async Task A_rejected_start_task_is_raised()
    {
        var (session, transport) = Build();
        transport.Enqueue(Response("TaskFailed", status: 45000001, taskId: null));

        await session.OpenAsync();

        var error = await Assert.ThrowsAsync<DoubaoSessionException>(() => session.StartTaskAsync());

        Assert.Contains("StartTask", error.Message);
    }

    [Fact]
    public async Task A_closed_socket_ends_the_pump_without_throwing()
    {
        var (session, transport) = await OpenedAsync();

        transport.Close();

        await session.PumpAsync();
    }

    [Fact]
    public async Task A_closed_socket_during_a_handshake_is_reported()
    {
        var (session, transport) = Build();
        transport.Close();

        await session.OpenAsync();

        await Assert.ThrowsAsync<DoubaoSessionException>(() => session.StartTaskAsync());
    }

    [Fact]
    public async Task Sending_before_opening_is_rejected()
    {
        var (session, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAudioAsync([1], DoubaoFrameState.First, 0));
    }

    [Fact]
    public void The_session_config_matches_the_reference_shape()
    {
        string config = DoubaoSessionConfig.Build("did-1", vadWindowMs: 500);

        using var document = System.Text.Json.JsonDocument.Parse(config);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("audio_info").GetProperty("channel").GetInt32());
        Assert.Equal("speech_opus", root.GetProperty("audio_info").GetProperty("format").GetString());
        Assert.Equal(16000, root.GetProperty("audio_info").GetProperty("sample_rate").GetInt32());
        Assert.True(root.GetProperty("enable_punctuation").GetBoolean());
        Assert.False(root.GetProperty("enable_speech_rejection").GetBoolean());

        var extra = root.GetProperty("extra");
        Assert.Equal("did-1", extra.GetProperty("did").GetString());
        Assert.Equal(500, extra.GetProperty("end_smooth_window_ms").GetInt32());
        Assert.Equal("oime", extra.GetProperty("app_name").GetString());
        Assert.Equal("tool", extra.GetProperty("input_mode").GetString());
        Assert.True(extra.GetProperty("use_twopass_retry").GetBoolean());
    }
}
