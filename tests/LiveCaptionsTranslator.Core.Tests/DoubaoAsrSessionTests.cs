using System.Text;

using LiveCaptionsTranslator.Core.Asr;
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

    [Fact]
    public async Task No_session_is_opened_until_the_first_ensure()
    {
        int created = 0;
        var transport = new FakeTransport();
        transport.Enqueue(Response("TaskStarted"));
        transport.Enqueue(Response("SessionStarted"));

        await using var streamer = new DoubaoAsrStreamer(() =>
        {
            created++;
            return new DoubaoAsrSession(DeviceId, AppKey, () => transport);
        });

        // Nothing playing yet: no connection, no handshake.
        Assert.Equal(0, created);
        Assert.Equal(0, streamer.SessionsOpened);
        Assert.Empty(transport.Sent);

        await streamer.EnsureSessionAsync();

        Assert.Equal(1, created);
        Assert.Equal(1, streamer.SessionsOpened);
        Assert.Equal("StartTask", Str(transport.Sent[0], 5));
    }

    [Fact]
    public async Task Invalidating_a_session_reconnects_on_the_next_ensure()
    {
        int created = 0;
        var transports = new List<FakeTransport>();

        await using var streamer = new DoubaoAsrStreamer(() =>
        {
            var transport = new FakeTransport();
            transport.Enqueue(Response("TaskStarted"));
            transport.Enqueue(Response("SessionStarted"));
            transports.Add(transport);
            created++;
            return new DoubaoAsrSession(DeviceId, AppKey, () => transport);
        });

        var first = await streamer.EnsureSessionAsync();
        Assert.Same(first, await streamer.EnsureSessionAsync());

        await streamer.InvalidateAsync();

        var second = await streamer.EnsureSessionAsync();

        Assert.NotSame(first, second);
        Assert.Equal(2, created);
        Assert.Equal(2, streamer.SessionsOpened);
        Assert.False(transports[0].IsOpen);
    }

    [Fact]
    public async Task Reconnect_gives_up_after_the_configured_number_of_failures()
    {
        var transport = new FakeTransport();
        // Every handshake is rejected.
        for (int i = 0; i < 10; i++)
            transport.Enqueue(Response("TaskFailed", status: 45000001));

        await using var streamer = new DoubaoAsrStreamer(
            () => new DoubaoAsrSession(DeviceId, AppKey, () => transport),
            new DoubaoStreamOptions { MaxConsecutiveFailures = 2 });

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<DoubaoSessionException>(() => streamer.EnsureSessionAsync());
            await streamer.InvalidateAsync();
        }

        Assert.Equal(3, streamer.ConsecutiveFailures);

        var error = await Assert.ThrowsAsync<DoubaoSessionException>(() => streamer.EnsureSessionAsync());
        Assert.Contains("停止重连", error.Message);
    }

    [Fact]
    public async Task A_healthy_session_resets_the_failure_counter()
    {
        var transport = new FakeTransport();
        transport.Enqueue(Response("TaskFailed", status: 45000001));
        transport.Enqueue(Response("TaskStarted"));
        transport.Enqueue(Response("SessionStarted"));

        await using var streamer = new DoubaoAsrStreamer(
            () => new DoubaoAsrSession(DeviceId, AppKey, () => transport));

        await Assert.ThrowsAsync<DoubaoSessionException>(() => streamer.EnsureSessionAsync());
        Assert.Equal(1, streamer.ConsecutiveFailures);

        await streamer.EnsureSessionAsync();
        streamer.MarkHealthy();

        Assert.Equal(0, streamer.ConsecutiveFailures);
    }
}

public class DoubaoTranscriptBuilderTests
{
    private static DoubaoResultSegment Interim(string text, double start) =>
        new(text, IsInterim: true, IsVadFinished: false, IsForceFinished: false,
            IsVadTimeout: false, IsStreamFinish: false) { StartTime = start };

    private static DoubaoResultSegment Final(string text, double start) =>
        new(text, IsInterim: false, IsVadFinished: true, IsForceFinished: false,
            IsVadTimeout: false, IsStreamFinish: false) { StartTime = start };

    [Fact]
    public void A_growing_segment_replaces_itself()
    {
        var builder = new DoubaoTranscriptBuilder();

        Assert.Equal("今天天", builder.Append(Interim("今天天", 0)).Text);
        Assert.Equal("今天天气不错。", builder.Append(Interim("今天天气不错。", 0)).Text);

        // Same start_time means the same segment, so nothing has been committed yet.
        Assert.Equal(string.Empty, builder.Committed);
        Assert.Equal("今天天气不错。", builder.Current);
    }

    [Fact]
    public void A_new_start_time_commits_the_previous_segment()
    {
        var builder = new DoubaoTranscriptBuilder();

        builder.Append(Interim("今天天气不错。", 0));
        builder.Append(Interim("我", 1.81));

        // The observed stream: 0 -> 1.81 starts the second sentence.
        Assert.Equal("今天天气不错。我", builder.Text);
        Assert.Equal("今天天气不错。", builder.Committed);

        builder.Append(Interim("我们一起去公园散步吧。", 1.81));

        Assert.Equal("今天天气不错。我们一起去公园散步吧。", builder.Text);
    }

    [Fact]
    public void A_final_segment_is_committed_and_resets_the_state()
    {
        var builder = new DoubaoTranscriptBuilder();

        builder.Append(Interim("今天天气不错。", 0));
        var (text, isFinal) = builder.Append(Final("今天天气不错。", 0));

        Assert.True(isFinal);
        Assert.Equal("今天天气不错。", text);
        Assert.Equal(string.Empty, builder.Current);

        // A later segment continues from the committed text.
        builder.Append(Interim("我们", 1.81));
        Assert.Equal("今天天气不错。我们", builder.Text);
    }

    [Fact]
    public void Missing_start_times_still_accumulate()
    {
        var builder = new DoubaoTranscriptBuilder();

        // Both report -1, which must not look like a new segment every time.
        builder.Append(Interim("甲", -1));
        builder.Append(Interim("乙", -1));

        Assert.Equal("乙", builder.Text);
    }
}

public class DoubaoProviderStreamingTests
{
    /// <summary>Replays canned responses and records everything the provider sends.</summary>
    private sealed class CapturingTransport : IDoubaoTransport
    {
        // A real socket blocks until a message arrives; a queue that returns null when empty made the
        // receive side "finish" instantly, which is not what happens in production.
        private readonly System.Threading.Channels.Channel<byte[]?> incoming =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]?>();

        public List<byte[]> Sent { get; } = [];

        public bool IsOpen { get; private set; } = true;

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            Sent.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await incoming.Reader.ReadAsync(cancellationToken);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Enqueue(byte[] data) => incoming.Writer.TryWrite(data);
    }

    private static byte[] Response(string messageType, int status = DoubaoStatusCodes.Ok)
    {
        var buffer = new List<byte>();
        DoubaoProtoWriter.WriteString(buffer, 4, messageType);
        DoubaoProtoWriter.WriteTag(buffer, 5, DoubaoWireType.Varint);
        DoubaoProtoWriter.WriteVarint(buffer, (ulong)status);
        return buffer.ToArray();
    }

    private static (string Method, int FrameState, int AudioLength) Decode(byte[] frame)
    {
        var reader = new DoubaoProtoReader(frame);
        string method = string.Empty;
        int frameState = 0;
        int audioLength = 0;

        while (reader.TryReadTag(out int field, out int wireType))
        {
            switch (field)
            {
                case 5 when wireType == DoubaoWireType.LengthDelimited:
                    method = reader.ReadString();
                    break;
                case 7 when wireType == DoubaoWireType.LengthDelimited:
                    audioLength = reader.ReadLengthDelimited().Length;
                    break;
                case 9 when wireType == DoubaoWireType.Varint:
                    frameState = (int)reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return (method, frameState, audioLength);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;

        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
                throw new TimeoutException("condition was not met in time");

            await Task.Delay(20);
        }
    }

    private static async Task StopGracefullyAsync(DoubaoAsrProvider provider, CapturingTransport transport)
    {
        var stop = provider.StopAsync();

        await WaitUntil(() => transport.Sent.Any(frame => Decode(frame).Method == "FinishSession"));
        transport.Enqueue(Response("SessionFinished"));

        await stop;
    }

    [Fact]
    public async Task The_streaming_path_sends_first_middle_and_last()
    {
        var transport = new CapturingTransport();
        transport.Enqueue(Response("TaskStarted"));
        transport.Enqueue(Response("SessionStarted"));

        var audio = new FakeAudioSource();

        await using var provider = new DoubaoAsrProvider(
            new DoubaoCredentials("1782778396951964", "app-key", string.Empty, string.Empty),
            () => transport);

        await provider.StartAsync(new AsrContext
        {
            Sink = new RecordingSink(),
            Options = new AsrOptions(),
            AudioSource = audio,
        });

        // Two 40 ms chunks: enough for two 20 ms Opus frames with nothing left over.
        audio.EmitChunk();
        audio.EmitChunk();

        await WaitUntil(() => transport.Sent.Count >= 4);

        await StopGracefullyAsync(provider, transport);

        var frames = transport.Sent.Select(Decode).ToList();

        Assert.Contains(frames, frame => frame.Method == "TaskRequest" && frame.FrameState == (int)DoubaoFrameState.First);
        Assert.Contains(frames, frame => frame.Method == "TaskRequest" && frame.FrameState == (int)DoubaoFrameState.Middle);
        Assert.Contains(frames, frame => frame.Method == "TaskRequest" && frame.FrameState == (int)DoubaoFrameState.Last);
        Assert.Contains(frames, frame => frame.Method == "FinishSession");

        // The protocol messages come first, then audio.
        Assert.Equal("StartTask", frames[0].Method);
        Assert.Equal("StartSession", frames[1].Method);
    }

    [Fact]
    public async Task No_connection_is_made_while_nothing_plays()
    {
        var transport = new CapturingTransport();

        await using var provider = new DoubaoAsrProvider(
            new DoubaoCredentials("1782778396951964", "app-key", string.Empty, string.Empty),
            () => transport);

        await provider.StartAsync(new AsrContext
        {
            Sink = new RecordingSink(),
            Options = new AsrOptions(),
            AudioSource = new FakeAudioSource(),
        });

        await Task.Delay(400);

        Assert.Empty(transport.Sent);

        await provider.StopAsync();
    }

    [Fact]
    public async Task Starting_without_credentials_is_refused()
    {
        await using var provider = new DoubaoAsrProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartAsync(new AsrContext
        {
            Sink = new RecordingSink(),
            Options = new AsrOptions(),
            AudioSource = new FakeAudioSource(),
        }));
    }

    [Theory]
    [InlineData("600", 600)]
    [InlineData("200", 200)]
    [InlineData("5000", 5000)]
    [InlineData("50", 200)]      // too small: ignored
    [InlineData("9000", 200)]    // too large: ignored
    [InlineData("abc", 200)]     // garbage: ignored
    public async Task The_segmentation_window_comes_from_the_host_settings(string configured, int expected)
    {
        var transport = new CapturingTransport();
        transport.Enqueue(Response("TaskStarted"));
        transport.Enqueue(Response("SessionStarted"));

        var audio = new FakeAudioSource();

        await using var provider = new DoubaoAsrProvider(
            new DoubaoCredentials("1782778396951964", "app-key", string.Empty, string.Empty),
            () => transport,
            new DoubaoStreamOptions { VadWindowMs = 200 });

        await provider.StartAsync(new AsrContext
        {
            Sink = new RecordingSink(),
            Options = new AsrOptions
            {
                Settings = new Dictionary<string, string> { [DoubaoSettingKeys.VadWindowMs] = configured },
            },
            AudioSource = audio,
        });

        audio.EmitChunk();
        await WaitUntil(() => transport.Sent.Count >= 3);

        // The StartSession payload carries the window; find it by content rather than by index so a
        // reconnect cannot shift what we are looking at.
        string? sessionFrame = transport.Sent
            .Select(frame => System.Text.Encoding.UTF8.GetString(frame))
            .FirstOrDefault(text => text.Contains("end_smooth_window_ms"));

        Assert.NotNull(sessionFrame);
        Assert.Contains($"\"end_smooth_window_ms\":{expected}", sessionFrame);

        await StopGracefullyAsync(provider, transport);
    }
}

public class DoubaoProviderStallTests
{
    private sealed class ScriptedTransport : IDoubaoTransport
    {
        // A real socket blocks until a message arrives; a queue that returns null when empty made the
        // receive side "finish" instantly, which is not what happens in production.
        private readonly System.Threading.Channels.Channel<byte[]?> incoming =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]?>();

        public List<byte[]> Sent { get; } = [];

        public bool IsOpen { get; private set; } = true;

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            Sent.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await incoming.Reader.ReadAsync(cancellationToken);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Enqueue(byte[] data) => incoming.Writer.TryWrite(data);
    }

    private static byte[] Response(string messageType, int status = DoubaoStatusCodes.Ok)
    {
        var buffer = new List<byte>();
        DoubaoProtoWriter.WriteString(buffer, 4, messageType);
        DoubaoProtoWriter.WriteTag(buffer, 5, DoubaoWireType.Varint);
        DoubaoProtoWriter.WriteVarint(buffer, (ulong)status);
        return buffer.ToArray();
    }

    [Fact]
    public async Task A_session_that_dies_mid_stream_does_not_leave_the_provider_spinning()
    {
        var transports = new List<ScriptedTransport>();

        ScriptedTransport Factory()
        {
            var transport = new ScriptedTransport();
            transport.Enqueue(Response("TaskStarted"));
            transport.Enqueue(Response("SessionStarted"));
            transports.Add(transport);
            return transport;
        }

        var audio = new FakeAudioSource();
        var sink = new RecordingSink();

        await using var provider = new DoubaoAsrProvider(
            new DoubaoCredentials("1782778396951964", "app-key", string.Empty, string.Empty),
            () =>
            {
                var transport = Factory();
                // The first session dies as soon as it starts; the second is healthy.
                if (transports.Count == 1)
                {
                    transport.Enqueue(Response("SessionFailed", 45000001));
                }

                return transport;
            },
            new DoubaoStreamOptions { MaxConsecutiveFailures = 5, ReconnectDelay = TimeSpan.FromMilliseconds(50) });

        await provider.StartAsync(new AsrContext
        {
            Sink = sink,
            Options = new AsrOptions(),
            AudioSource = audio,
        });

        audio.EmitChunk();

        // The send loop must notice the dead receive side and reconnect rather than streaming into
        // a dead socket forever (that is the "audio keeps playing, captions stop" hang).
        var started = Environment.TickCount64;
        while (transports.Count < 2 && Environment.TickCount64 - started < 8000)
        {
            audio.EmitChunk();
            await Task.Delay(50);
        }

        Assert.True(transports.Count >= 2, "the provider never reconnected after the session died");

        await provider.StopAsync();
    }
}
