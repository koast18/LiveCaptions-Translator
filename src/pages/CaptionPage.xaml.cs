using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

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
            };
            Unloaded += (s, e) =>
            {
                Translator.Caption.RollingLines.CollectionChanged -= OnRollingLinesChanged;
                Translator.Caption.LinesRebuilt -= ScrollToLatest;
                (App.Current.MainWindow as MainWindow).CaptionLogButton.Visibility = Visibility.Collapsed;
            };

            CollapseTranslatedCaption(Translator.Setting.MainWindow.CaptionLogEnabled);
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
