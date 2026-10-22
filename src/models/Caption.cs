using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.Localization;

namespace LiveCaptionsTranslator.models
{
    /// <summary>One line in the rolling caption view, lyrics style.</summary>
    /// <param name="Source">The recognised sentence the line was produced from, so a correction can find it.</param>
    public sealed record CaptionLine(string Text, bool IsLatest, string? Source = null);

    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayTranslatedCaption = string.Empty;
        private bool translationRedundant;

        /// <summary>The rolling view: previous lines dimmed, the newest one highlighted.</summary>
        public ObservableCollection<CaptionLine> RollingLines { get; } = [];

        /// <summary>Raised after the list was replaced, so the page can keep the newest line in view.</summary>
        public event Action? LinesRebuilt;

        /// <summary>
        /// Sentences that have finished, newest last. More are kept than any window can show so the
        /// visible count follows the window height instead of a setting.
        /// </summary>
        private readonly List<CaptionLine> recent = [];

        /// <summary>Touched by the recognition and display threads alike.</summary>
        private readonly object recentLock = new();

        private const int MaxRecentLines = 200;

        public void AddRecentLine(string? text, string? source = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string line = text.Trim();

            lock (recentLock)
                AddRecentLineCore(line, source ?? line);
        }

        private void AddRecentLineCore(string line, string source)
        {
            recent.Add(new CaptionLine(line, false, source));
            while (recent.Count > MaxRecentLines)
                recent.RemoveAt(0);
        }

        /// <summary>Drops a line a revision removed from the transcript. Returns false when it was not shown.</summary>
        public bool RemoveRecentLineBySource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return false;

            lock (recentLock)
            {
                for (int i = recent.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(recent[i].Source ?? recent[i].Text, source, StringComparison.Ordinal))
                        continue;

                    recent.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        public void ClearRecentLines()
        {
            lock (recentLock)
                recent.Clear();
        }

        /// <summary>
        /// Replaces the line a corrected sentence was shown on, wherever it is, and re-tags it with the
        /// corrected wording so the translation that follows lands on the same line.
        /// </summary>
        public bool ReviseRecentLine(string? was, string? now)
        {
            if (string.IsNullOrWhiteSpace(was) || string.IsNullOrWhiteSpace(now))
                return false;

            lock (recentLock)
            {
                for (int i = recent.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(recent[i].Source ?? recent[i].Text, was, StringComparison.Ordinal))
                        continue;

                    recent[i] = recent[i] with { Text = now.Trim(), Source = now };
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when a finished sentence is still in the history, i.e. its translation still belongs there.</summary>
        public bool HasRecentLineSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return false;

            lock (recentLock)
                return recent.Any(line => string.Equals(line.Source ?? line.Text, source, StringComparison.Ordinal));
        }

        /// <summary>Puts the translation on the line of the sentence it came from.</summary>
        public bool SetRecentLineText(string? source, string? text)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(text))
                return false;

            lock (recentLock)
            {
                for (int i = recent.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(recent[i].Source ?? recent[i].Text, source, StringComparison.Ordinal))
                        continue;

                    recent[i] = recent[i] with { Text = text.Trim() };
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// What the recogniser believes it heard right now, before the segment is final. Shown as the
        /// newest line straight away so the page reacts to speech instead of waiting for both the
        /// final result and its translation.
        /// </summary>
        public string? InterimText
        {
            get => interimText;
            set
            {
                // The live slot shows at most two lines: beyond that only the tail is shown, so it can
                // never push the finished sentences out of view. It is a display concern only - nothing
                // is committed here.
                string? shown = value;
                if (shown is not null && shown.Length > DisplayLineChars * 2)
                    shown = "…" + shown[^(DisplayLineChars * 2)..];

                if (interimText == shown)
                    return;

                interimText = shown;
                OnPropertyChanged(nameof(InterimText));
            }
        }

        private string? interimText;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayNoticePrefix = " ";

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;

        public Queue<TranslationHistoryEntry> Contexts { get; } = new(MAX_CONTEXTS);

        public IEnumerable<TranslationHistoryEntry> AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);

        public IEnumerable<TranslationHistoryEntry> DisplayLogCards =>
            GetPreviousContexts(Translator.Setting.DisplaySentences).Reverse();

        /// <summary>
        /// Text the app itself has to show - a connection error, a fallback warning, "paused". It has
        /// its own slot so it can never be mistaken for a caption, and never ends up in the rolling
        /// history the way a status written into the caption field used to.
        /// </summary>
        public string Notice
        {
            get => notice;
            set
            {
                value ??= string.Empty;
                if (notice == value)
                    return;

                notice = value;
                OnPropertyChanged(nameof(Notice));
                OnPropertyChanged(nameof(HasNotice));
            }
        }

        public bool HasNotice => notice.Length > 0;

        private string notice = string.Empty;

        /// <summary>
        /// True when the translation adds nothing - the source was already Simplified Chinese, so the
        /// policy skipped the API and the two lines would read identically. The UI hides the second
        /// line instead of showing it twice.
        /// </summary>
        public bool TranslationRedundant
        {
            get => translationRedundant;
            set
            {
                if (translationRedundant == value)
                    return;

                translationRedundant = value;
                OnPropertyChanged("TranslationRedundant");
            }
        }

        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                overlayOriginalCaption = value;
                OnPropertyChanged("OverlayOriginalCaption");
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                overlayNoticePrefix = value;
                OnPropertyChanged("OverlayNoticePrefix");
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                overlayCurrentTranslation = value;
                OnPropertyChanged("OverlayCurrentTranslation");
            }
        }

        public string OverlayPreviousTranslation =>
            GetPreviousText(Translator.Setting.DisplaySentences, TextType.Translation);

        private Caption()
        {
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public string GetPreviousText(int count, TextType textType)
        {
            if (count <= 0 || Contexts.Count == 0)
                return string.Empty;

            var prev = Contexts
                .Reverse().Take(count).Reverse()
                .Select(entry => entry == null || string.CompareOrdinal(entry.TranslatedText, "N/A") == 0 ||
                                 StatusText.IsStatus(entry.TranslatedText) ?
                    "" : (textType == TextType.Caption ? entry.SourceText : entry.TranslatedText))
                .Aggregate((accu, cur) =>
                {
                    if (!string.IsNullOrEmpty(accu))
                    {
                        if (Array.IndexOf(TextUtil.PUNC_EOS, accu[^1]) == -1)
                            accu += TextUtil.isCJChar(accu[^1]) ? "。" : ". ";
                        else
                            accu += TextUtil.isCJChar(accu[^1]) ? "" : " ";
                    }
                    cur = RegexPatterns.NoticePrefix().Replace(cur, "");
                    return accu + cur;
                });

            if (textType == TextType.Translation)
                prev = RegexPatterns.NoticePrefix().Replace(prev, "");
            if (!string.IsNullOrEmpty(prev) && Array.IndexOf(TextUtil.PUNC_EOS, prev[^1]) == -1)
                prev += TextUtil.isCJChar(prev[^1]) ? "。" : ".";
            if (!string.IsNullOrEmpty(prev) && Encoding.UTF8.GetByteCount(prev[^1].ToString()) < 2)
                prev += " ";
            return prev;
        }

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count)
        {
            if (count <= 0 || Contexts.Count == 0)
                return [];

            return Contexts
                .Reverse().Take(count).Reverse()
                .Where(entry => entry != null && string.CompareOrdinal(entry.TranslatedText, "N/A") != 0 &&
                                !StatusText.IsStatus(entry.TranslatedText));
        }

        /// <summary>
        /// Rebuilds the rolling view from the recent history plus the line being spoken now, marking
        /// the newest one so the template can highlight it.
        /// </summary>
        public void RebuildRollingLines(int previousCount)
        {
            var lines = BuildRollingLines(previousCount);

            // RollingLines is bound to the caption page, and WPF refuses a collection change from any
            // thread but the one that owns it. The display loop runs on the thread pool, so an
            // unguarded Add here threw and took the whole loop down - which is why the page stayed
            // empty while the history, written elsewhere, kept filling.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => ApplyRollingLines(lines)));
                return;
            }

            ApplyRollingLines(lines);
        }

        /// <summary>
        /// Longest a single displayed line may get before it is broken at a natural pause. A
        /// recogniser that never punctuates would otherwise pile several sentences into one very long
        /// highlighted line that keeps changing length.
        /// </summary>
        public const int DisplayLineChars = 42;

        /// <summary>Characters a line may be broken after; breaking anywhere else would cut a word.</summary>
        private static readonly char[] BreakChars = [' ', '　', '，', '。', '、', '；', '：', '！', '？', ',', '.', ';', ':', '!', '?'];

        private List<CaptionLine> BuildRollingLines(int previousCount)
        {
            // The finished line only. The sentence being spoken lives in its own slot (InterimText) so
            // it can never appear here as well - that duplication was visible whenever the translation
            // was skipped because the source was already in the target language.
            string current = DisplayTranslatedCaption;

            // Everything that has finished goes in; the ScrollViewer decides how much of it is visible.
            List<CaptionLine> snapshot;
            lock (recentLock)
                snapshot = [.. recent];

            var lines = new List<CaptionLine>(snapshot.Count + 1);

            foreach (var entry in snapshot)
            {
                foreach (string part in SplitForDisplay(entry.Text, DisplayLineChars))
                    lines.Add(new CaptionLine(part, false));
            }

            if (!string.IsNullOrWhiteSpace(current) &&
                (lines.Count == 0 || lines[^1].Text != current.Trim()))
            {
                var parts = SplitForDisplay(current, DisplayLineChars);
                for (int i = 0; i < parts.Count; i++)
                    lines.Add(new CaptionLine(parts[i], i == parts.Count - 1));
            }
            else if (lines.Count > 0)
            {
                // The sentence on screen is already the last finished line: highlight it instead of
                // listing it a second time.
                lines[^1] = lines[^1] with { IsLatest = true };
            }

            return lines;
        }


        /// <summary>
        /// Breaks text into readable lines at the last pause within <paramref name="limit"/>. Nothing is
        /// dropped: when no pause is close enough the remainder is moved forward until one is found, so
        /// the text is split rather than truncated.
        /// </summary>
        public static IReadOnlyList<string> SplitForDisplay(string? text, int limit)
        {
            var parts = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return parts;

            string rest = text.Trim();
            if (limit < 8)
                limit = 8;

            while (rest.Length > limit)
            {
                int cut = rest.LastIndexOfAny(BreakChars, limit - 1, limit);
                if (cut < limit / 3)
                {
                    // No pause near the limit: look ahead for the next one instead of cutting a word.
                    cut = rest.IndexOfAny(BreakChars, limit);
                    if (cut == -1)
                        break;
                }

                parts.Add(rest[..(cut + 1)].Trim());
                rest = rest[(cut + 1)..].TrimStart();
            }

            if (rest.Length > 0)
                parts.Add(rest);

            return parts;
        }

        private void ApplyRollingLines(List<CaptionLine> lines)
        {
            if (RollingLines.Count == lines.Count && RollingLines.SequenceEqual(lines))
                return;

            RollingLines.Clear();
            foreach (var line in lines)
                RollingLines.Add(line);

            LinesRebuilt?.Invoke();
        }

        /// <summary>One text per entry, so a skipped translation is never shown twice.</summary>
        private static string LineFor(string? source, string? translated)
        {
            if (string.IsNullOrWhiteSpace(translated) || translated == "N/A")
                return source?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(source))
                return translated.Trim();

            return string.Equals(source.Trim(), translated.Trim(), StringComparison.Ordinal)
                ? source.Trim()
                : translated.Trim();
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}