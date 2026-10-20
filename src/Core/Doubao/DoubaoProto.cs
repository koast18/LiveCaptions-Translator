namespace LiveCaptionsTranslator.Core.Doubao
{
    /// <summary>protobuf wire types, only the ones these messages use.</summary>
    internal static class DoubaoWireType
    {
        public const int Varint = 0;
        public const int Fixed64 = 1;
        public const int LengthDelimited = 2;
        public const int Fixed32 = 5;
    }

    /// <summary>
    /// Minimal protobuf writer. Hand written on purpose: the schema is two messages, and pulling in
    /// Google.Protobuf plus a code generator would add a dependency for no benefit.
    /// </summary>
    internal static class DoubaoProtoWriter
    {
        public static void WriteTag(List<byte> buffer, int fieldNumber, int wireType) =>
            WriteVarint(buffer, (ulong)((fieldNumber << 3) | wireType));

        public static void WriteVarint(List<byte> buffer, ulong value)
        {
            while (true)
            {
                byte bits = (byte)(value & 0x7F);
                value >>= 7;

                if (value == 0)
                {
                    buffer.Add(bits);
                    return;
                }

                buffer.Add((byte)(bits | 0x80));
            }
        }

        public static void WriteString(List<byte> buffer, int fieldNumber, string value)
        {
            ArgumentNullException.ThrowIfNull(value);

            byte[] raw = System.Text.Encoding.UTF8.GetBytes(value);
            WriteTag(buffer, fieldNumber, DoubaoWireType.LengthDelimited);
            WriteVarint(buffer, (ulong)raw.Length);
            buffer.AddRange(raw);
        }

        public static void WriteBytes(List<byte> buffer, int fieldNumber, ReadOnlySpan<byte> value)
        {
            WriteTag(buffer, fieldNumber, DoubaoWireType.LengthDelimited);
            WriteVarint(buffer, (ulong)value.Length);

            foreach (byte item in value)
                buffer.Add(item);
        }
    }

    /// <summary>
    /// Minimal protobuf reader. Unknown fields are skipped by their wire type instead of throwing,
    /// so the service can add fields without breaking the app.
    /// </summary>
    internal sealed class DoubaoProtoReader(ReadOnlyMemory<byte> data)
    {
        private int position;

        public bool IsAtEnd => position >= data.Length;

        public bool TryReadTag(out int fieldNumber, out int wireType)
        {
            fieldNumber = 0;
            wireType = 0;

            if (IsAtEnd)
                return false;

            ulong tag = ReadVarint();
            fieldNumber = (int)(tag >> 3);
            wireType = (int)(tag & 0x07);
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            int shift = 0;

            while (true)
            {
                if (position >= data.Length)
                    throw new InvalidDataException("protobuf 数据在 varint 中途结束");

                byte current = data.Span[position++];
                result |= (ulong)(current & 0x7F) << shift;

                if ((current & 0x80) == 0)
                    return result;

                shift += 7;

                if (shift > 63)
                    throw new InvalidDataException("protobuf varint 过长");
            }
        }

        public ReadOnlyMemory<byte> ReadLengthDelimited()
        {
            int length = checked((int)ReadVarint());

            if (length < 0 || position + length > data.Length)
                throw new InvalidDataException("protobuf 长度超出缓冲区");

            var slice = data.Slice(position, length);
            position += length;
            return slice;
        }

        public string ReadString() => System.Text.Encoding.UTF8.GetString(ReadLengthDelimited().Span);

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case DoubaoWireType.Varint:
                    ReadVarint();
                    break;

                case DoubaoWireType.LengthDelimited:
                    ReadLengthDelimited();
                    break;

                case DoubaoWireType.Fixed64:
                    Advance(8);
                    break;

                case DoubaoWireType.Fixed32:
                    Advance(4);
                    break;

                default:
                    throw new InvalidDataException($"不支持的 protobuf wire type {wireType}");
            }
        }

        private void Advance(int count)
        {
            if (position + count > data.Length)
                throw new InvalidDataException("protobuf 定长字段超出缓冲区");

            position += count;
        }
    }
}
