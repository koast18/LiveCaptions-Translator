namespace LiveCaptionsTranslator.Core.Text;

/// <summary>
/// Turns the cumulative text a recogniser streams into finished sentences.
/// <para>
/// Every callback carries the whole segment so far - recorded from the real endpoint, see
/// tests/fixtures/transcript-partials.txt - so the only sound model is to remember what has already
/// been consumed and process the difference. Splitting the full text again on every callback is what
/// produced duplicated sentences and one ever-growing line.
/// </para>
/// </summary>
public sealed class LiveTranscript
{
    /// <summary>Longest a line may get before it is cut at a pause. Nothing is dropped.</summary>
    public const int MaxLineChars = 42;

    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '…', '!', '?', ';'];

    private static readonly char[] Pauses =
        [' ', '　', '，', '、', '：', ',', ':', '。', '！', '？', '；', '.', '!', '?', ';'];

    private string consumed = string.Empty;
    private string buffer = string.Empty;

    /// <summary>Sentences already handed out, so a replayed transcript cannot commit them twice.</summary>
    private readonly Queue<string> committedRecent = new();

    private const int RecentCapacity = 64;

    /// <summary>The text still being spoken; this is the live line.</summary>
    public string Pending => buffer.Trim();

    public bool HasPending => buffer.Trim().Length > 0;

    /// <summary>
    /// Feeds one callback and returns the sentences that finished because of it. Everything absorbed
    /// so far is remembered, so a repeated callback can never commit the same sentence twice.
    /// </summary>
    public IReadOnlyList<string> Accept(string? cumulative, bool isFinal)
    {
        var committed = new List<string>();
        if (string.IsNullOrEmpty(cumulative))
            return committed;

        string delta;
        if (consumed.Length > 0 && cumulative.StartsWith(consumed, StringComparison.Ordinal))
        {
            delta = cumulative[consumed.Length..];
        }
        else
        {
            // The backend restarted its transcript - a recycled session, or a corrected segment. It may
            // still begin with what we have already read, so the overlap is skipped instead of being
            // committed a second time. Without this a whole block of sentences is repeated.
            int overlap = Overlap(consumed, cumulative);

            consumed = string.Empty;
            buffer = string.Empty;
            delta = cumulative[overlap..];
        }

        buffer += delta;
        consumed = cumulative;

        while (TryTakeSentence(out string sentence))
            Add(committed, sentence);

        if (isFinal && HasPending)
        {
            Add(committed, Pending);
            buffer = string.Empty;
        }

        return committed;
    }

    /// <summary>Longest suffix of <paramref name="seen"/> that is also a prefix of <paramref name="next"/>.</summary>
    private static int Overlap(string seen, string next)
    {
        int max = Math.Min(seen.Length, next.Length);
        for (int length = max; length > 0; length--)
        {
            if (seen.AsSpan(seen.Length - length).SequenceEqual(next.AsSpan(0, length)))
                return length;
        }

        return 0;
    }

    /// <summary>Queues a sentence unless it was already committed recently.</summary>
    private void Add(List<string> committed, string sentence)
    {
        if (!committedRecent.Contains(sentence))
        {
            committed.Add(sentence);

            committedRecent.Enqueue(sentence);
            while (committedRecent.Count > RecentCapacity)
                committedRecent.Dequeue();
        }
    }

    public void Reset()
    {
        consumed = string.Empty;
        buffer = string.Empty;
        committedRecent.Clear();
    }

    private bool TryTakeSentence(out string sentence)
    {
        sentence = string.Empty;

        // A full stop ends a sentence. Stray punctuation - a lone "，" at the start, say - is skipped
        // instead of being returned as an empty sentence.
        while (true)
        {
            int end = buffer.IndexOfAny(SentenceEnds);
            if (end < 0)
                break;

            string candidate = buffer[..(end + 1)].Trim();
            buffer = buffer[(end + 1)..];

            if (candidate.Any(char.IsLetterOrDigit))
            {
                sentence = candidate;
                return true;
            }
        }

        if (buffer.Length <= MaxLineChars)
            return false;

        // Too long without a full stop: break at the last pause inside the window, or at the next one
        // after it. Cutting anywhere else would split a word, so the text runs on rather than being
        // butchered.
        int cut = buffer.LastIndexOfAny(Pauses, MaxLineChars - 1, MaxLineChars);
        if (cut < MaxLineChars / 3)
            cut = buffer.IndexOfAny(Pauses, MaxLineChars);

        if (cut < 0)
            return false;

        string part = buffer[..(cut + 1)].Trim();
        buffer = buffer[(cut + 1)..];

        if (part.Length == 0)
            return false;

        sentence = part;
        return true;
    }
}
