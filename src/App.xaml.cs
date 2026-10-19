using System.Windows;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Translator.Setting?.Save();

            Task.Run(Translator.StartAsync);
            Task.Run(() => Translator.TranslateLoop());
            Task.Run(() => Translator.DisplayLoop());
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Translator.Shutdown();
        }
    }
}
