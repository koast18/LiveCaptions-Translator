using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.Localization;

namespace LiveCaptionsTranslator.models
{
    /// <summary>One line in the rolling caption view, lyrics style.</summary>
    public sealed record CaptionLine(string Text, bool IsLatest);

    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private bool translationRedundant;

        /// <summary>The rolling view: previous lines dimmed, the newest one highlighted.</summary>
        public ObservableCollection<CaptionLine> RollingLines { get; } = [];

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
                if (interimText == value)
                    return;

                interimText = value;
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

        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
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

        private List<CaptionLine> BuildRollingLines(int previousCount)
        {
            // The interim line wins: it is the only text that exists while the recogniser is still
            // deciding what the sentence is.
            string current = InterimText
                             ?? (translationRedundant ? DisplayOriginalCaption : DisplayTranslatedCaption);

            var lines = new List<CaptionLine>(previousCount + 1);
            var previous = Contexts.Reverse().Take(Math.Max(0, previousCount)).Reverse().ToList();

            foreach (var entry in previous)
            {
                string text = LineFor(entry.SourceText, entry.TranslatedText);
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(new CaptionLine(text, false));
            }

            if (!string.IsNullOrWhiteSpace(current))
                lines.Add(new CaptionLine(current, true));

            return lines;
        }

        private void ApplyRollingLines(List<CaptionLine> lines)
        {
            if (RollingLines.Count == lines.Count && RollingLines.SequenceEqual(lines))
                return;

            RollingLines.Clear();
            foreach (var line in lines)
                RollingLines.Add(line);
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