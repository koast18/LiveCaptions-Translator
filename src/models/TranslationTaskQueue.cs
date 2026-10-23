namespace LiveCaptionsTranslator.models
{
    /// <summary>One finished translation, ready to be put on the line its source sentence is on.</summary>
    public sealed record TranslationResult(string SourceText, string TranslatedText, bool IsChoke);

    public class TranslationTaskQueue
    {
        private readonly object gate = new();
        private readonly List<TranslationTask> tasks = [];

        /// <summary>
        /// Results wait here until the display loop picks them up. A single output slot used to
        /// overwrite a result the display had not read yet, which silently dropped a whole sentence
        /// whenever two translations finished close together.
        /// </summary>
        private readonly Queue<TranslationResult> completed = new();

        /// <summary>Released whenever a result is queued, so the display loop need not poll.</summary>
        private readonly SemaphoreSlim available = new(0);

        /// <summary>Translations still running. Diagnostics and tests.</summary>
        public int PendingCount
        {
            get
            {
                lock (gate)
                    return tasks.Count;
            }
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText)
        {
            var translationTask = new TranslationTask(worker, originalText, new CancellationTokenSource());

            lock (gate)
                tasks.Add(translationTask);

            _ = CompleteAsync(translationTask);
        }

        /// <summary>Blocks until a new result is available. Replaces polling.</summary>
        public bool WaitForResult(int millisecondsTimeout) => available.Wait(millisecondsTimeout);

        /// <summary>Takes the oldest finished result, keeping the semaphore count in step with it.</summary>
        public bool TryTake(out TranslationResult result)
        {
            lock (gate)
            {
                if (completed.Count == 0)
                {
                    result = null!;
                    return false;
                }

                result = completed.Dequeue();
            }

            available.Wait(0);
            return true;
        }

        /// <summary>
        /// Publishes the result in completion order. Earlier translations are deliberately not
        /// cancelled: the display finds each line by its source text, so a slow sentence simply
        /// arrives later instead of being dropped.
        /// </summary>
        private async Task CompleteAsync(TranslationTask translationTask)
        {
            (string TranslatedText, bool IsChoke) result;

            try
            {
                result = await translationTask.Task;
            }
            catch (Exception)
            {
                lock (gate)
                    tasks.Remove(translationTask);

                // The sentence stays on screen as recognised text; there is nothing else to show.
                return;
            }

            lock (gate)
            {
                tasks.Remove(translationTask);
                completed.Enqueue(new TranslationResult(
                    translationTask.OriginalText, result.TranslatedText, result.IsChoke));
            }

            available.Release();

            try
            {
                bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
                if (!isOverwrite)
                    await Translator.AddContexts();
                await Translator.Log(translationTask.OriginalText, result.TranslatedText, isOverwrite);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // History is a side effect of showing captions; it must not stop them.
            }
        }
    }

    public class TranslationTask
    {
        public Task<(string, bool)> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
        }
    }
}
