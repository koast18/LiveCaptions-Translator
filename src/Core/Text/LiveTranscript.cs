namespace LiveCaptionsTranslator.Core.Text;

/// <summary>
/// Turns the cumulative text a recogniser streams into finished sentences.
/// <para>
/// Two kinds of backend feed this class:
/// </para>
/// <list type="bullet">
/// <item>
/// A backend that knows which part of its transcript is final (Doubao: everything before the
/// segment it is still revising) passes that <c>stableText</c>. Only sentences inside it are
/// committed, so a sentence is shown once, in the wording the recogniser finally settled on -
/// its second/third pass corrects mis-heard characters before the sentence reaches the screen.
/// </item>
/// <item>
/// A backend that only has a growing cumulative transcript (iFlytek, Windows Live Captions) passes
/// no stable text. A sentence is then committed at its full stop, and a replayed transcript is
/// recognised by the overlap with what has already been read.
/// </item>
/// </list>
/// <para>
/// Everything after the committed text is <see cref="Pending"/>: the line being spoken now.
/// </para>
/// </summary>
public sealed class LiveTranscript
{
    /// <summary>Longest a line may get before it is cut at a pause. Nothing is dropped.</summary>
    public const int MaxLineChars = 42;

    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '…', '!', '?', ';'];

    private static readonly char[] Pauses =
        [' ', '　', '，', '、', '：', ',', ':', '。', '！', '？', '；', '.', '!', '?', ';'];

    /// <summary>Streaming mode: how much of the cumulative transcript has been consumed already.</summary>
    private string consumed = string.Empty;

    /// <summary>Stable mode: the transcript as it was last reported, so a rewrite can be spotted.</summary>
    private string stableConsumed = string.Empty;

    /// <summary>Stable mode: offset in the transcript just after the last committed sentence.</summary>
    private int committedEnd;

    /// <summary>The text after the last committed sentence; this is the live line.</summary>
    private string buffer = string.Empty;

    /// <summary>Sentences already handed out, newest last, so a replayed transcript can be recognised.</summary>
    private readonly List<string> sentences = [];

    private const int RecentCapacity = 64;

    /// <summary>The text still being spoken; this is the live line.</summary>
    public string Pending => buffer.Trim();

    public bool HasPending => buffer.Trim().Length > 0;

    /// <summary>Sentences committed so far, newest last. Read-only view for diagnostics.</summary>
    public IReadOnlyList<string> Committed => sentences;

    /// <summary>
    /// Feeds one callback and returns the sentences that finished because of it.
    /// </summary>
    /// <param name="cumulative">The whole transcript the backend reports on this callback.</param>
    /// <param name="stableText">
    /// The part of <paramref name="cumulative"/> the backend will not revise any more, or
    /// <c>null</c> when the backend cannot tell. Doubao passes everything before the segment it is
    /// still revising.
    /// </param>
    /// <param name="isFinal">True when the backend will not revise anything it has reported.</param>
    public IReadOnlyList<string> Accept(string? cumulative, string? stableText = null, bool isFinal = false)
    {
        if (string.IsNullOrEmpty(cumulative))
            return Array.Empty<string>();

        return stableText is null
            ? AcceptStreaming(cumulative, isFinal)
            : AcceptStable(cumulative, isFinal ? cumulative : stableText);
    }

    /// <summary>
    /// A backend that reports which part of the transcript is settled. Nothing is committed while it
    /// can still be revised, so a correction can never become a second line.
    /// </summary>
    private IReadOnlyList<string> AcceptStable(string cumulative, string stableText)
    {
        var committed = new List<string>();

        int stableLimit = CommonPrefixLength(cumulative, stableText);
        if (stableLimit > cumulative.Length)
            stableLimit = cumulative.Length;

        if (stableConsumed.Length > 0 && !cumulative.StartsWith(stableConsumed, StringComparison.Ordinal))
        {
            // The transcript was rewritten - a recycled session, or a correction that reached back
            // past the segment boundary. Reposition to whatever has already been committed.
            committedEnd = CommittedPrefixEnd(cumulative);
        }

        stableConsumed = cumulative;

        if (committedEnd > stableLimit)
            committedEnd = stableLimit;

        if (stableLimit > committedEnd)
        {
            foreach (var (text, _) in SplitSentences(cumulative[committedEnd..stableLimit]))
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                committed.Add(text);
                sentences.Add(text);
                Remember();
            }

            committedEnd = stableLimit;
        }

        buffer = cumulative[stableLimit..];
        return committed;
    }

    /// <summary>
    /// A backend that only has a growing transcript. A full stop ends a sentence, and a callback that
    /// does not continue what was read before is treated as a replay or a restarted transcript.
    /// </summary>
    private IReadOnlyList<string> AcceptStreaming(string cumulative, bool isFinal)
    {
        var committed = new List<string>();
        string delta;
        bool replay = false;

        if (consumed.Length > 0 && cumulative.StartsWith(consumed, StringComparison.Ordinal))
        {
            delta = cumulative[consumed.Length..];
        }
        else
        {
            // The backend restarted its transcript - a recycled session, or a corrected segment. It may
            // still begin with what has already been read, so the overlap is skipped instead of being
            // committed a second time. Without this a whole block of sentences is repeated.
            int overlap = Overlap(consumed, cumulative);

            consumed = string.Empty;
            buffer = string.Empty;
            delta = cumulative[overlap..];
            replay = true;
        }

        buffer += delta;
        consumed = cumulative;

        while (TryTakeSentence(out string sentence))
            Add(committed, sentence, replay);

        if (isFinal && HasPending)
        {
            Add(committed, Pending, replay);
            buffer = string.Empty;
        }

        return committed;
    }

    /// <summary>
    /// Queues a sentence. In a replay, a sentence that has already been shown is refused: the same
    /// words can legitimately be said twice, but a transcript that starts over must not be recorded
    /// again.
    /// </summary>
    private void Add(List<string> committed, string sentence, bool replay)
    {
        if (replay && sentences.Contains(sentence))
            return;

        committed.Add(sentence);
        sentences.Add(sentence);
        Remember();
    }

    private void Remember()
    {
        while (sentences.Count > RecentCapacity)
            sentences.RemoveAt(0);
    }

    /// <summary>
    /// How far into <paramref name="text"/> the sentences committed so far reach. Sentences that are
    /// no longer part of the transcript from this point on are forgotten, so the rewritten tail can be
    /// committed again.
    /// </summary>
    private int CommittedPrefixEnd(string text)
    {
        int cursor = 0;
        int count = 0;

        foreach (string sentence in sentences)
        {
            int at = text.IndexOf(sentence, cursor, StringComparison.Ordinal);

            // The transcript has to *start* with what was committed; a gap is a different session.
            if (at < 0 || at > cursor + 2)
                break;

            cursor = at + sentence.Length;
            count++;
        }

        if (count < sentences.Count)
            sentences.RemoveRange(count, sentences.Count - count);

        return cursor;
    }

    /// <summary>Longest prefix the two texts share.</summary>
    private static int CommonPrefixLength(string text, string prefix)
    {
        int length = Math.Min(text.Length, prefix.Length);
        int i = 0;

        while (i < length && text[i] == prefix[i])
            i++;

        return i;
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

    /// <summary>
    /// Splits settled text into sentences. The text is settled, so a trailing run without a full stop
    /// is a finished sentence too - the recogniser simply did not punctuate it.
    /// </summary>
    private static IEnumerable<(string Text, int End)> SplitSentences(string text)
    {
        int start = 0;

        while (start < text.Length)
        {
            int index = text.IndexOfAny(SentenceEnds, start);
            int stop = index < 0 ? text.Length : index + 1;
            string candidate = text[start..stop].Trim();

            if (candidate.Length > 0 && candidate.Any(char.IsLetterOrDigit))
                yield return (candidate, stop);

            start = stop;
        }
    }

    /// <summary>
    /// Takes the leading part of the sentence still being spoken once it has filled more than a line, so
    /// the live slot never grows past what a line can show. Returns null while it still fits.
    /// </summary>
    public string? TakePendingLine()
    {
        if (buffer.Length <= MaxLineChars)
            return null;

        int cut = buffer.LastIndexOfAny(Pauses, MaxLineChars - 1, MaxLineChars);
        if (cut < MaxLineChars / 3)
        {
            cut = buffer.IndexOfAny(Pauses, MaxLineChars);
            if (cut < 0)
                return null;
        }

        string part = buffer[..(cut + 1)].Trim();
        buffer = buffer[(cut + 1)..].TrimStart();

        return part.Length > 0 ? part : null;
    }

    public void Reset()
    {
        consumed = string.Empty;
        stableConsumed = string.Empty;
        committedEnd = 0;
        buffer = string.Empty;
        sentences.Clear();
    }

    /// <summary>Commits the leading finished sentence of the streaming buffer, if there is one.</summary>
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
