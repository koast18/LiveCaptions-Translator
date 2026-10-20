namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>`FrameState` from asr.proto. The numbers are the wire values, not an ordinal.</summary>
    public enum DoubaoFrameState
    {
        Unspecified = 0,
        First = 1,
        Middle = 3,
        Last = 9,
    }

    /// <summary>Status codes the service uses.</summary>
    public static class DoubaoStatusCodes
    {
        public const int Ok = 20000000;
    }

    /// <summary>Method names of the ASR protocol.</summary>
    public static class DoubaoMethods
    {
        public const string StartTask = "StartTask";

        public const string StartSession = "StartSession";

        public const string TaskRequest = "TaskRequest";

        public const string FinishSession = "FinishSession";
    }

    /// <summary>
    /// Encodes `asr.AsrRequest`. Field order and the "omit empty" rules follow the reference client
    /// exactly, so the bytes match what the IME itself sends.
    /// </summary>
    public static class DoubaoAsrRequest
    {
        public const string ServiceName = "ASR";

        public static byte[] Encode(
            string token,
            string methodName,
            string requestId,
            string? payload = null,
            byte[]? audio = null,
            DoubaoFrameState frameState = DoubaoFrameState.Unspecified)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

            var buffer = new List<byte>(audio?.Length ?? 64);

            DoubaoProtoWriter.WriteString(buffer, 2, token);
            DoubaoProtoWriter.WriteString(buffer, 3, ServiceName);
            DoubaoProtoWriter.WriteString(buffer, 5, methodName);

            if (!string.IsNullOrEmpty(payload))
                DoubaoProtoWriter.WriteString(buffer, 6, payload);

            if (audio is { Length: > 0 })
                DoubaoProtoWriter.WriteBytes(buffer, 7, audio);

            DoubaoProtoWriter.WriteString(buffer, 8, requestId);

            if (frameState != DoubaoFrameState.Unspecified)
            {
                DoubaoProtoWriter.WriteTag(buffer, 9, DoubaoWireType.Varint);
                DoubaoProtoWriter.WriteVarint(buffer, (ulong)frameState);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>Decoded `asr.AsrResponse`. Unknown fields are skipped, not treated as errors.</summary>
    public sealed class DoubaoAsrResponse
    {
        public string RequestId { get; init; } = string.Empty;

        public string TaskId { get; init; } = string.Empty;

        public string ServiceName { get; init; } = string.Empty;

        /// <summary>TaskStarted / SessionStarted / TaskFailed / SessionFailed / TaskResult / SessionFinished.</summary>
        public string MessageType { get; init; } = string.Empty;

        public int StatusCode { get; init; }

        public string StatusMessage { get; init; } = string.Empty;

        public string ResultJson { get; init; } = string.Empty;

        public int UnknownField9 { get; init; }

        public bool IsOk => StatusCode == DoubaoStatusCodes.Ok;

        public bool IsFailure =>
            MessageType is "TaskFailed" or "SessionFailed";

        public bool IsFinal =>
            MessageType is "SessionFinished" or "TaskFinished";

        public static DoubaoAsrResponse Parse(ReadOnlySpan<byte> data) => Parse(data.ToArray());

        public static DoubaoAsrResponse Parse(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            var reader = new DoubaoProtoReader(data);

            string requestId = string.Empty;
            string taskId = string.Empty;
            string serviceName = string.Empty;
            string messageType = string.Empty;
            int statusCode = 0;
            string statusMessage = string.Empty;
            string resultJson = string.Empty;
            int unknownField9 = 0;

            while (reader.TryReadTag(out int field, out int wireType))
            {
                switch (field)
                {
                    case 1 when wireType == DoubaoWireType.LengthDelimited:
                        requestId = reader.ReadString();
                        break;

                    case 2 when wireType == DoubaoWireType.LengthDelimited:
                        taskId = reader.ReadString();
                        break;

                    case 3 when wireType == DoubaoWireType.LengthDelimited:
                        serviceName = reader.ReadString();
                        break;

                    case 4 when wireType == DoubaoWireType.LengthDelimited:
                        messageType = reader.ReadString();
                        break;

                    case 5 when wireType == DoubaoWireType.Varint:
                        statusCode = (int)reader.ReadVarint();
                        break;

                    case 6 when wireType == DoubaoWireType.LengthDelimited:
                        statusMessage = reader.ReadString();
                        break;

                    case 7 when wireType == DoubaoWireType.LengthDelimited:
                        resultJson = reader.ReadString();
                        break;

                    case 9 when wireType == DoubaoWireType.Varint:
                        unknownField9 = (int)reader.ReadVarint();
                        break;

                    default:
                        // Forward compatible: the service may add fields at any time.
                        reader.Skip(wireType);
                        break;
                }
            }

            return new DoubaoAsrResponse
            {
                RequestId = requestId,
                TaskId = taskId,
                ServiceName = serviceName,
                MessageType = messageType,
                StatusCode = statusCode,
                StatusMessage = statusMessage,
                ResultJson = resultJson,
                UnknownField9 = unknownField9,
            };
        }
    }
}
