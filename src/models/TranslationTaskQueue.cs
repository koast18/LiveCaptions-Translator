namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<TranslationTask> tasks;

        private (string translatedText, bool isChoke, bool interim) output;
        public (string translatedText, bool isChoke, bool interim) Output => output;

        /// <summary>Released whenever <see cref="Output"/> changes, so the display loop need not poll.</summary>
        private readonly SemaphoreSlim available = new(0);

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            output = (string.Empty, false, false);
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText, bool interim = false)
        {
            var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource(), interim);
            lock (_lock)
            {
                tasks.Add(newTranslationTask);
            }
            // Run `OnTaskCompleted` in a new thread.
            newTranslationTask.Task.ContinueWith(
                task => OnTaskCompleted(newTranslationTask),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
        }

        /// <summary>Blocks until a new result is available. Replaces polling.</summary>
        public bool WaitForResult(int millisecondsTimeout) => available.Wait(millisecondsTimeout);

        private async Task OnTaskCompleted(TranslationTask translationTask)
        {
            lock (_lock)
            {
                var index = tasks.IndexOf(translationTask);
                for (int i = 0; i < index; i++)
                    tasks[i].CTS.Cancel();
                for (int i = index; i >= 0; i--)
                    tasks.RemoveAt(i);
            }

            var result = translationTask.Task.Result;
            output = (result.Item1, result.Item2, translationTask.Interim);
            available.Release();

            // An interim translation is a preview: it is replaced by the final one, so it must not be
            // logged or fed into the translation context.
            if (translationTask.Interim)
                return;

            bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
            if (!isOverwrite)
                await Translator.AddContexts();
            await Translator.Log(translationTask.OriginalText, result.Item1, isOverwrite);
        }
    }

    public class TranslationTask
    {
        public Task<(string, bool)> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        /// <summary>True for a preview translation of a sentence that is still being spoken.</summary>
        public bool Interim { get; }

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts, bool interim = false)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
            Interim = interim;
        }
    }
}