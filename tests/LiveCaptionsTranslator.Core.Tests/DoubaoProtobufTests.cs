using System.Text.Json;

using LiveCaptionsTranslator.Core.Doubao;

namespace LiveCaptionsTranslator.Core.Tests;

/// <summary>
/// The protobuf bytes are compared against vectors produced by the reference client's own hand
/// written encoder (doubaoIME/run_asr.py), so this is not self-consistency theatre.
/// </summary>
public class DoubaoProtobufTests
{
    private static JsonDocument Golden()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "doubao-protobuf-golden.json");

        Assert.True(File.Exists(path), $"missing fixture: {path}");

        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void Frame_state_numbers_match_the_schema()
    {
        using var document = Golden();
        var states = document.RootElement.GetProperty("frameStates");

        Assert.Equal((int)DoubaoFrameState.Unspecified, states.GetProperty("unspecified").GetInt32());
        Assert.Equal((int)DoubaoFrameState.First, states.GetProperty("first").GetInt32());
        Assert.Equal((int)DoubaoFrameState.Middle, states.GetProperty("middle").GetInt32());
        Assert.Equal((int)DoubaoFrameState.Last, states.GetProperty("last").GetInt32());

        Assert.Equal(1, (int)DoubaoFrameState.First);
        Assert.Equal(3, (int)DoubaoFrameState.Middle);
        Assert.Equal(9, (int)DoubaoFrameState.Last);
    }

    [Fact]
    public void Encoded_requests_match_the_reference_byte_for_byte()
    {
        using var document = Golden();
        int checked_ = 0;

        foreach (var vector in document.RootElement.GetProperty("requests").EnumerateArray())
        {
            var args = vector.GetProperty("args");

            string token = args.GetProperty("token").GetString()!;
            string method = args.GetProperty("method_name").GetString()!;
            string requestId = args.GetProperty("request_id").GetString()!;

            string? payload = args.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.GetString()
                : null;

            byte[]? audio = args.TryGetProperty("audio_data_hex", out var audioElement)
                ? Convert.FromHexString(audioElement.GetString()!)
                : null;

            var frameState = (DoubaoFrameState)(args.TryGetProperty("frame_state", out var stateElement)
                ? stateElement.GetInt32()
                : 0);

            byte[] actual = DoubaoAsrRequest.Encode(token, method, requestId, payload, audio, frameState);
            string expectedHex = vector.GetProperty("hex").GetString()!;

            Assert.Equal(expectedHex, Convert.ToHexString(actual).ToLowerInvariant());
            checked_++;
        }

        Assert.Equal(6, checked_);
    }

    [Fact]
    public void Encoded_requests_round_trip_through_the_reader()
    {
        // Encode, walk the bytes back out with the reader, re-encode, and expect the same bytes.
        byte[] audio = [1, 2, 3, 4];
        byte[] encoded = DoubaoAsrRequest.Encode(
            "tok", DoubaoMethods.TaskRequest, "rid",
            payload: """{"extra":{}}""", audio: audio, frameState: DoubaoFrameState.Middle);

        var reader = new DoubaoProtoReader(encoded);
        var fields = new Dictionary<int, object>();

        while (reader.TryReadTag(out int field, out int wireType))
        {
            fields[field] = wireType == DoubaoWireType.Varint
                ? (int)reader.ReadVarint()
                : reader.ReadLengthDelimited().ToArray();
        }

        Assert.Equal("tok", System.Text.Encoding.UTF8.GetString((byte[])fields[2]));
        Assert.Equal("ASR", System.Text.Encoding.UTF8.GetString((byte[])fields[3]));
        Assert.Equal("TaskRequest", System.Text.Encoding.UTF8.GetString((byte[])fields[5]));
        Assert.Equal(audio, (byte[])fields[7]);
        Assert.Equal((int)DoubaoFrameState.Middle, (int)fields[9]);

        byte[] reencoded = DoubaoAsrRequest.Encode(
            System.Text.Encoding.UTF8.GetString((byte[])fields[2]),
            System.Text.Encoding.UTF8.GetString((byte[])fields[5]),
            System.Text.Encoding.UTF8.GetString((byte[])fields[8]),
            System.Text.Encoding.UTF8.GetString((byte[])fields[6]),
            (byte[])fields[7],
            (DoubaoFrameState)(int)fields[9]);

        Assert.Equal(encoded, reencoded);
    }

    [Fact]
    public void Empty_payload_audio_and_frame_state_are_omitted()
    {
        byte[] encoded = DoubaoAsrRequest.Encode("tok", DoubaoMethods.StartTask, "rid");

        var reader = new DoubaoProtoReader(encoded);
        var fields = new List<int>();

        while (reader.TryReadTag(out int field, out int wireType))
        {
            fields.Add(field);
            reader.Skip(wireType);
        }

        // token, service_name, method_name, request_id - nothing else.
        Assert.Equal(new[] { 2, 3, 5, 8 }, fields);
    }

    [Fact]
    public void Responses_decode_to_the_expected_fields()
    {
        using var document = Golden();
        int checked_ = 0;

        foreach (var vector in document.RootElement.GetProperty("responses").EnumerateArray())
        {
            byte[] bytes = Convert.FromHexString(vector.GetProperty("hex").GetString()!);
            var expected = vector.GetProperty("expected");

            var response = DoubaoAsrResponse.Parse(bytes);

            Assert.Equal(expected.GetProperty("requestId").GetString(), response.RequestId);
            Assert.Equal(expected.GetProperty("taskId").GetString(), response.TaskId);
            Assert.Equal(expected.GetProperty("serviceName").GetString(), response.ServiceName);
            Assert.Equal(expected.GetProperty("messageType").GetString(), response.MessageType);
            Assert.Equal(expected.GetProperty("statusCode").GetInt32(), response.StatusCode);
            Assert.Equal(expected.GetProperty("statusMessage").GetString(), response.StatusMessage);
            Assert.Equal(expected.GetProperty("resultJson").GetString(), response.ResultJson);
            Assert.Equal(expected.GetProperty("unknownField9").GetInt32(), response.UnknownField9);

            checked_++;
        }

        Assert.Equal(3, checked_);
    }

    [Fact]
    public void Unknown_fields_are_skipped_not_rejected()
    {
        using var document = Golden();
        var vector = document.RootElement.GetProperty("responses").EnumerateArray()
            .First(v => v.GetProperty("label").GetString() == "result_with_unknown_fields");

        // That vector carries field 42, which this decoder knows nothing about.
        var response = DoubaoAsrResponse.Parse(Convert.FromHexString(vector.GetProperty("hex").GetString()!));

        Assert.Equal("TaskResult", response.MessageType);
        Assert.Equal(20000000, response.StatusCode);
        Assert.Equal("""{"results":[{"text":"你好"}]}""", response.ResultJson);
    }

    [Fact]
    public void Response_flags_are_derived_from_the_message_type()
    {
        Assert.True(new DoubaoAsrResponse { StatusCode = 20000000 }.IsOk);
        Assert.False(new DoubaoAsrResponse { StatusCode = 45000001 }.IsOk);

        Assert.True(new DoubaoAsrResponse { MessageType = "SessionFailed" }.IsFailure);
        Assert.True(new DoubaoAsrResponse { MessageType = "TaskFailed" }.IsFailure);
        Assert.False(new DoubaoAsrResponse { MessageType = "SessionStarted" }.IsFailure);

        Assert.True(new DoubaoAsrResponse { MessageType = "SessionFinished" }.IsFinal);
        Assert.False(new DoubaoAsrResponse { MessageType = "TaskResult" }.IsFinal);
    }

    [Fact]
    public void Truncated_input_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => DoubaoAsrResponse.Parse(new byte[] { 0x0A, 0x05, 0x61 }));
    }
}
