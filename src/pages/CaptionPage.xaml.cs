using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using LiveCaptionsTranslator.Utils;

namespace LiveCaptionsTranslator
{
    public partial class CaptionPage : Page
    {
        public const int CARD_HEIGHT = 110;

        private static CaptionPage instance;
        public static CaptionPage Instance => instance;

        public CaptionPage()
        {
            InitializeComponent();
            DataContext = Translator.Caption;
            instance = this;

            // Lyrics style: always keep the newest line in view.
            Translator.Caption.RollingLines.CollectionChanged += OnRollingLinesChanged;

            RollingScroll.SizeChanged += (s, e) => ScrollToLatest();
            RollingScroll.ScrollChanged += RollingScroll_ScrollChanged;
            Translator.Caption.LinesRebuilt += ScrollToLatest;

            Loaded += (s, e) =>
            {
                AutoHeight();
                (App.Current.MainWindow as MainWindow).CaptionLogButton.Visibility = Visibility.Visible;
                ScrollToLatest();
                RunCaptionProbeIfRequested();
            };
            Unloaded += (s, e) =>
            {
                Translator.Caption.RollingLines.CollectionChanged -= OnRollingLinesChanged;
                Translator.Caption.LinesRebuilt -= ScrollToLatest;
                (App.Current.MainWindow as MainWindow).CaptionLogButton.Visibility = Visibility.Collapsed;
            };

            CollapseTranslatedCaption(Translator.Setting.MainWindow.CaptionLogEnabled);
        }


        /// <summary>
        /// Opt-in diagnostics used by CI: feed a Doubao-shaped stream and record what the page really
        /// rendered. The probe reads the visual tree, not the model, so a broken binding or a list that
        /// never updates fails it.
        /// </summary>
        private static bool captionProbeRan;

        private async void RunCaptionProbeIfRequested()
        {
            string? path = Environment.GetEnvironmentVariable("LCT_CAPTION_PROBE");
            if (string.IsNullOrWhiteSpace(path) || captionProbeRan)
                return;

            captionProbeRan = true;

            try
            {
                // Chinese in, Chinese out: the translation policy answers locally, so the rendered
                // lines are the recognised text and no network is involved.
                Translator.Setting!.TargetLanguage = "zh-CN";
                Translator.Setting.AsrLanguage = "zh_cn";

                string[] sentences =
                [
                    "今天天气不错。",
                    "我们一起去公园散步吧。",
                    "这个项目的字幕不能重复。",
                    "颜值要尽量小。",
                    "他说他明天。",
                    "要去北京出差。",
                    "大概 3 天。",
                    "以后回来。",
                ];

                string transcript = string.Empty;

                foreach (string sentence in sentences)
                {
                    // What the backend does: a partial that is only the beginning, then the final that
                    // settles (and can correct) the whole sentence.
                    string partial = transcript + sentence[..Math.Max(1, sentence.Length / 2)];
                    Translator.HandleCaptionText(partial, isFinal: false, stableText: transcript);

                    transcript += sentence;
                    Translator.HandleCaptionText(transcript, isFinal: true, stableText: transcript);
                }

                // Leave one sentence still being spoken, so the live slot is rendered too.
                string live = "我们";
                Translator.HandleCaptionText(transcript + live, isFinal: false, stableText: transcript);

                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                UpdateLayout();

                WriteCaptionProbe(path);
            }
            catch (Exception exception)
            {
                try
                {
                    File.WriteAllText(path, $"error\t{exception.Message}");
                }
                catch (Exception)
                {
                    // Diagnostics must never break the app.
                }
            }
        }

        private void WriteCaptionProbe(string path)
        {
            var lines = new List<string>();

            foreach (TextBlock block in Descendants<TextBlock>(RollingItems))
            {
                // Only the caption lines themselves, not whatever a template may add later.
                if (string.IsNullOrEmpty(block.Text))
                    continue;

                lines.Add($"line\t{block.Text}\t{block.FontSize:0.#}\t{block.FontWeight}\t{block.Visibility}");
            }

            lines.Add($"live\t{LiveLine.Text}");
            lines.Add($"notice\t{Translator.Caption.Notice}");

            File.WriteAllLines(path, lines);
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                if (child is T match)
                    yield return match;

                foreach (T nested in Descendants<T>(child))
                    yield return nested;
            }
        }

        private void OnRollingLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
            => ScrollToLatest();

        /// <summary>
        /// Stays pinned to the bottom unless the reader scrolled up. Scrolling after a collection change
        /// alone was not reliable: the extent is only known once the new line has been laid out, which is
        /// what this event reports.
        /// </summary>
        private void RollingScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0)
                return;

            bool wasAtBottom = e.ExtentHeight - e.ExtentHeightChange - (e.VerticalOffset + e.ViewportHeight) < 2;
            if (wasAtBottom)
                RollingScroll.ScrollToEnd();
        }

        private void ScrollToLatest()
        {
            if (!IsLoaded)
                return;

            // At Background priority this ran before the new line had been laid out, so it scrolled to
            // the previous extent and the newest line ended up below the fold. Scroll at Render, then
            // once more after a layout pass, and clamp to the real maximum.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(() =>
                {
                    RollingScroll.ScrollToVerticalOffset(double.MaxValue);
                    RollingScroll.UpdateLayout();
                    RollingScroll.ScrollToVerticalOffset(double.MaxValue);
                }));
        }

        private async void TextBlock_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                try
                {
                    Clipboard.SetText(textBlock.Text);
                    SnackbarHost.Show(LocalizationManager.T("Caption_Copied"), textBlock.Text, SnackbarType.Info, 100);
                }
                catch
                {
                    SnackbarHost.Show(LocalizationManager.T("Caption_CopyFailed"), string.Empty, SnackbarType.Error, 100);
                }

                await Task.Delay(500);
            }
        }

        /// <summary>
        /// Kept for the "CaptionLog" toggle in the main window. The rolling view always shows the
        /// context lines; the toggle only decides how tall the window grows for them.
        /// </summary>
        public void CollapseTranslatedCaption(bool isCollapsed) => AutoHeight();

        public void AutoHeight()
        {
            if (Translator.Setting.MainWindow.CaptionLogEnabled)
                (App.Current.MainWindow as MainWindow).AutoHeightAdjust(
                    minHeight: CARD_HEIGHT * (Translator.Setting.DisplaySentences + 1),
                    maxHeight: CARD_HEIGHT * (Translator.Setting.DisplaySentences + 1));
            else
                (App.Current.MainWindow as MainWindow).AutoHeightAdjust(
                    minHeight: (int)App.Current.MainWindow.MinHeight,
                    maxHeight: (int)App.Current.MainWindow.MinHeight);
        }
    }
}
