using System.Text;

namespace LiveCaptionsTranslator.Core.Text
{
    /// <summary>
    /// Script level helpers for Chinese: telling Han text apart from other scripts, guessing
    /// whether it is already Simplified, and normalising Traditional text to Simplified.
    /// </summary>
    public static class ChineseText
    {
        private static readonly Dictionary<int, string> TraditionalToSimplified = BuildTable();

        /// <summary>Size of the embedded Traditional to Simplified table.</summary>
        public static int TableSize => TraditionalToSimplified.Count;

        /// <summary>True when the text contains at least one Han ideograph.</summary>
        public static bool HasHan(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (var rune in text.EnumerateRunes())
            {
                if (IsHan(rune.Value))
                    return true;
            }

            return false;
        }

        /// <summary>Number of Han ideographs in the text.</summary>
        public static int HanCount(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int count = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (IsHan(rune.Value))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// True when at least <paramref name="threshold"/> of the letters (Han and otherwise,
        /// ignoring whitespace, punctuation and symbols) are Han ideographs.
        /// </summary>
        public static bool IsMostlyHan(string? text, double threshold = 0.5)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            int han = 0;
            int letters = 0;

            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune) || Rune.IsSymbol(rune) || Rune.IsNumber(rune))
                    continue;

                letters++;

                if (IsHan(rune.Value))
                    han++;
            }

            return letters > 0 && (double)han / letters >= threshold;
        }

        /// <summary>
        /// Number of characters that only exist as Traditional forms. Some characters are valid
        /// in both scripts (乾 appears in Simplified 乾坤), so this is a heuristic, not a parser.
        /// </summary>
        public static int TraditionalCount(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int count = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (TraditionalToSimplified.ContainsKey(rune.Value))
                    count++;
            }

            return count;
        }

        /// <summary>Heuristic: Han text that contains no Traditional-only character.</summary>
        public static bool IsSimplified(string? text) =>
            HasHan(text) && TraditionalCount(text) == 0;

        /// <summary>Heuristic: Han text that contains at least one Traditional-only character.</summary>
        public static bool IsTraditional(string? text) =>
            TraditionalCount(text) > 0;

        /// <summary>
        /// Rewrites Traditional characters as their Simplified forms. Character level only:
        /// vocabulary is left alone (電腦 becomes 电脑, not 计算机).
        /// </summary>
        public static string ToSimplified(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            var builder = new StringBuilder(text.Length);
            bool changed = false;

            foreach (var rune in text.EnumerateRunes())
            {
                if (TraditionalToSimplified.TryGetValue(rune.Value, out string? simplified))
                {
                    builder.Append(simplified);
                    changed = true;
                }
                else if (rune.IsBmp)
                {
                    builder.Append((char)rune.Value);
                }
                else
                {
                    builder.Append(rune.ToString());
                }
            }

            return changed ? builder.ToString() : text;
        }

        /// <summary>CJK ideograph blocks, including the supplementary planes.</summary>
        private static bool IsHan(int codePoint) => codePoint switch
        {
            >= 0x3400 and <= 0x4DBF => true,       // Extension A
            >= 0x4E00 and <= 0x9FFF => true,       // Unified Ideographs
            >= 0xF900 and <= 0xFAFF => true,       // Compatibility Ideographs
            >= 0x20000 and <= 0x2A6DF => true,     // Extension B
            >= 0x2A700 and <= 0x2EBEF => true,     // Extensions C-F
            >= 0x2F800 and <= 0x2FA1F => true,     // Compatibility Ideographs Supplement
            >= 0x30000 and <= 0x323AF => true,     // Extensions G-H
            _ => false,
        };

        private static Dictionary<int, string> BuildTable()
        {
            var runes = new List<Rune>(TraditionalToSimplifiedTable.PairCount * 2);
            foreach (var rune in TraditionalToSimplifiedTable.Packed.EnumerateRunes())
                runes.Add(rune);

            var table = new Dictionary<int, string>(runes.Count / 2);
            for (int i = 0; i + 1 < runes.Count; i += 2)
                table[runes[i].Value] = runes[i + 1].ToString();

            return table;
        }
    }
}
