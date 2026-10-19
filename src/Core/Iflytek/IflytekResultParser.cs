using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>One <c>result</c> object from the service.</summary>
    /// <param name="Sn">Sentence slot this revision belongs to.</param>
    /// <param name="Pgs">Revision kind: <c>apd</c> (append/overwrite) or <c>rpl</c> (replace a range).</param>
    /// <param name="Rg">Inclusive <c>[start, end]</c> slot range removed by an <c>rpl</c> revision.</param>
    /// <param name="Ls">Whether the service considers the utterance finished.</param>
    /// <param name="Text">Text carried by this revision.</param>
    public sealed record IflytekWordResult(int Sn, string? Pgs, int[]? Rg, bool Ls, string Text);

    /// <summary>A decoded response frame.</summary>
    public sealed record IflytekMessage(int Code, string? Message, int Status, IflytekWordResult? Result)
    {
        public bool IsError => Code != 0;

        /// <summary>The service signals the end of the stream with <c>status == 2</c>.</summary>
        public bool IsFinal => Status == 2;
    }

    /// <summary>
    /// Decodes the JSON the service returns. Both <c>ws</c> shapes are accepted:
    /// <c>[{"cw":[{"w":"…"}]}]</c> (what the IME endpoint actually returns) and
    /// <c>[[{"cw":[{"w":"…"}]}]]</c> (the shape in the public documentation).
    /// </summary>
    public static class IflytekResultParser
    {
        public static IflytekMessage ParseMessage(string json)
        {
            using var document = JsonDocument.Parse(json);
            return ParseMessage(document.RootElement);
        }

        public static IflytekMessage ParseMessage(JsonElement root)
        {
            int code = 0;
            if (root.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.Number)
            {
                code = codeElement.GetInt32();
            }

            string? message = null;
            if (root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
            }

            int status = 0;
            IflytekWordResult? result = null;

            if (root.TryGetProperty("data", out var dataElement) &&
                dataElement.ValueKind == JsonValueKind.Object)
            {
                if (dataElement.TryGetProperty("status", out var statusElement) &&
                    statusElement.ValueKind == JsonValueKind.Number)
                {
                    status = statusElement.GetInt32();
                }

                if (dataElement.TryGetProperty("result", out var resultElement) &&
                    resultElement.ValueKind == JsonValueKind.Object)
                {
                    result = ParseResult(resultElement);
                }
            }

            return new IflytekMessage(code, message, status, result);
        }

        public static IflytekWordResult ParseResult(JsonElement result)
        {
            int sn = 0;
            if (result.TryGetProperty("sn", out var snElement) &&
                snElement.ValueKind == JsonValueKind.Number)
            {
                sn = snElement.GetInt32();
            }

            string? pgs = null;
            if (result.TryGetProperty("pgs", out var pgsElement) &&
                pgsElement.ValueKind == JsonValueKind.String)
            {
                pgs = pgsElement.GetString();
            }

            int[]? rg = null;
            if (result.TryGetProperty("rg", out var rgElement) &&
                rgElement.ValueKind == JsonValueKind.Array &&
                rgElement.GetArrayLength() >= 2)
            {
                rg = new[] { rgElement[0].GetInt32(), rgElement[1].GetInt32() };
            }

            bool ls = result.TryGetProperty("ls", out var lsElement) &&
                      lsElement.ValueKind == JsonValueKind.True;

            return new IflytekWordResult(sn, pgs, rg, ls, ExtractText(result));
        }

        /// <summary>Concatenates every <c>cw[].w</c> in the result, accepting both nesting shapes.</summary>
        public static string ExtractText(JsonElement result)
        {
            if (!result.TryGetProperty("ws", out var ws) || ws.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var builder = new StringBuilder();

            foreach (var entry in ws.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in entry.EnumerateArray())
                        AppendItem(item, builder);
                }
                else
                {
                    AppendItem(entry, builder);
                }
            }

            return builder.ToString();
        }

        private static void AppendItem(JsonElement item, StringBuilder builder)
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("cw", out var words) ||
                words.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var word in words.EnumerateArray())
            {
                if (word.ValueKind == JsonValueKind.String)
                {
                    builder.Append(word.GetString());
                }
                else if (word.ValueKind == JsonValueKind.Object &&
                         word.TryGetProperty("w", out var text) &&
                         text.ValueKind == JsonValueKind.String)
                {
                    builder.Append(text.GetString());
                }
            }
        }
    }
}
