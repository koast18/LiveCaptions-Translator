using System.Globalization;
using System.Text;

using LiveCaptionsTranslator.Core.Audio;

namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>
    /// Frame layout for the streaming IAT protocol: a first frame carrying
    /// <c>common</c>+<c>business</c>, middle frames, and an empty final frame.
    /// </summary>
    public static class IflytekProtocol
    {
        /// <summary>Silence prepended to every utterance, matching the reference client.</summary>
        public const int LeadingSilenceMs = 200;

        /// <summary>Builds the JSON text of one frame. <paramref name="status"/> is 0, 1 or 2.</summary>
        public static string BuildFrame(
            int status,
            ReadOnlySpan<byte> audio,
            IflytekCredentials credentials,
            IflytekBusiness? business = null,
            int sampleRate = AudioFormat.SampleRate)
        {
            ArgumentNullException.ThrowIfNull(credentials);

            var builder = new StringBuilder(audio.Length * 2 + 320);

            builder.Append("{\"data\":{\"status\":").Append(status)
                   .Append(",\"format\":\"audio/L16;rate=").Append(sampleRate)
                   .Append("\",\"encoding\":\"raw\",\"audio\":\"")
                   .Append(Convert.ToBase64String(audio))
                   .Append("\"}");   // close the `data` object

            if (status == 0)
            {
                builder.Append(",\"common\":{\"app_id\":\"").Append(credentials.AppId).Append("\"}");
                builder.Append(",\"business\":");
                AppendBusiness(builder, business ?? IflytekBusiness.Default);
            }

            builder.Append('}');   // close the frame
            return builder.ToString();
        }

        /// <summary>Splits PCM into fixed-size pieces, zero-padding the last one.</summary>
        public static IReadOnlyList<byte[]> ChunkPcm(
            ReadOnlySpan<byte> pcm,
            int chunkBytes = AudioFormat.ChunkBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkBytes);

            if (pcm.IsEmpty)
                return Array.Empty<byte[]>();

            var chunks = new List<byte[]>((pcm.Length / chunkBytes) + 1);
            for (int offset = 0; offset < pcm.Length; offset += chunkBytes)
            {
                var piece = new byte[chunkBytes];
                int count = Math.Min(chunkBytes, pcm.Length - offset);
                pcm.Slice(offset, count).CopyTo(piece);
                chunks.Add(piece);
            }

            return chunks;
        }

        /// <summary>Silence in the ASR PCM format, used as a lead-in before real audio.</summary>
        public static byte[] LeadingSilence(int milliseconds = LeadingSilenceMs) =>
            new byte[AudioFormat.SampleRate * milliseconds / 1000 * (AudioFormat.BitsPerSample / 8)];

        private static void AppendBusiness(StringBuilder builder, IflytekBusiness business)
        {
            builder.Append('{');

            bool first = true;

            void AppendValue(string name, string jsonValue)
            {
                if (!first)
                    builder.Append(',');

                first = false;
                builder.Append('"').Append(name).Append("\":").Append(jsonValue);
            }

            if (business.Language is not null)
                AppendValue("language", Quote(business.Language));

            if (business.Domain is not null)
                AppendValue("domain", Quote(business.Domain));

            if (business.Accent is not null)
                AppendValue("accent", Quote(business.Accent));

            if (business.VadEos is not null)
                AppendValue("vad_eos", business.VadEos.Value.ToString(CultureInfo.InvariantCulture));

            if (business.Dwa is not null)
                AppendValue("dwa", Quote(business.Dwa));

            if (business.Nunum is not null)
                AppendValue("nunum", business.Nunum.Value.ToString(CultureInfo.InvariantCulture));

            if (business.Ptt is not null)
                AppendValue("ptt", business.Ptt.Value.ToString(CultureInfo.InvariantCulture));

            builder.Append('}');
        }

        /// <summary>
        /// Minimal JSON string literal. Matches the reference client, which serialises with
        /// <c>ensure_ascii=False</c>: non-ASCII characters are written literally.
        /// </summary>
        private static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');

            foreach (char character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < 0x20)
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(character);

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
