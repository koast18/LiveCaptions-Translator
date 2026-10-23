namespace LiveCaptionsTranslator.Core.Text;

/// <summary>
/// One settled sentence, and what it does to the line already on screen.
/// <para>
/// <paramref name="Replaces"/> is set when the recogniser revised a sentence it had already handed
/// out: a segment can be finalised and then extended or corrected by the next pass, and the new
/// wording belongs where the old one is instead of becoming a second line.
/// </para>
/// </summary>
public sealed record TranscriptUpdate(string Text, string? Replaces = null);

/// <summary>
/// Turns the cumulative text a recogniser streams into finished sentences.
/// <para>
/// Two kinds of backend feed this class:
/// </para>
/// <list type="bullet">
/// <item>
/// A backend that knows which part of its transcript is settled (Doubao: everything before the
/// segment it is still revising) passes that <c>stableText</c>. Only sentences inside it are
/// committed, and a settled sentence that the final pass later extends replaces its own line.
/// </item>
/// <item>
/// A backend that only has a growing cumulative transcript (iFlytek, Windows Live Captions) passes
/// no stable text. A sentence is then committed at its full stop, and a replayed transcript is
/// recognised by the overlap with what has already been read.
/// </item>
/// </list>
/// <para>
/// Everything after the settled text is <see cref="Pending"/>: the line being spoken now.
/// </para>
/// </summary>
public sealed class LiveTranscript
{
    /// <summary>Longest a line may get before it is cut at a pause. Nothing is dropped.</summary>
    public const int MaxLineChars = 42;

    /// <summary>A settled sentence is only replaced by a revision close to the end of the transcript.</summary>
    public const int RevisionWindow = 4;

    /// <summary>
    /// A sentence can end at an ASCII full stop too - English recognition was one endless sentence
    /// without it - but a period inside a number ("3.5") or an abbreviation must not split the line.
    /// </summary>
    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '…', '!', '?', ';', '.'];

    private static readonly char[] Pauses =
        [' ', '　', '，', '、', '：', ',', ':', '。', '！', '？', '；', '.', '!', '?', ';'];

    private static readonly char[] TrailingPunctuation =
        [' ', '　', '。', '！', '？', '；', '…', '，', '、', '：', '.', '!', '?', ';', ',', ':', '~', '～'];

    /// <summary>Streaming mode: how much of the cumulative transcript has been consumed already.</summary>
    private string consumed = string.Empty;

    /// <summary>Stable mode: the transcript as it was last reported, so a rewrite can be spotted.</summary>
    private string stableConsumed = string.Empty;

    /// <summary>The text after the settled sentences; this is the live line.</summary>
    private string buffer = string.Empty;

    /// <summary>Sentences handed out so far, oldest first.</summary>
    private readonly List<string> sentences = [];

    private const int RecentCapacity = 64;

    /// <summary>True once a backend has reported settled text; its sentence list is then kept whole.</summary>
    private bool stableMode;

    /// <summary>The text still being spoken; this is the live line.</summary>
    public string Pending => buffer.Trim();

    public bool HasPending => buffer.Trim().Length > 0;

    /// <summary>Sentences committed so far, oldest first. Read-only view for diagnostics.</summary>
    public IReadOnlyList<string> Committed => sentences;

    /// <summary>
    /// Feeds one callback and returns what it finished.
    /// </summary>
    /// <param name="cumulative">The whole transcript the backend reports on this callback.</param>
    /// <param name="stableText">
    /// The part of <paramref name="cumulative"/> the backend will not revise any more, or
    /// <c>null</c> when the backend cannot tell. Doubao passes everything before the segment it is
    /// still revising.
    /// </param>
    /// <param name="isFinal">True when the backend will not revise anything it has reported.</param>
    public IReadOnlyList<TranscriptUpdate> Accept(
        string? cumulative, string? stableText = null, bool isFinal = false)
    {
        if (string.IsNullOrEmpty(cumulative))
            return Array.Empty<TranscriptUpdate>();

        return stableText is null
            ? AcceptStreaming(cumulative, isFinal)
            : AcceptStable(cumulative, isFinal ? cumulative : stableText);
    }

    /// <summary>
    /// A backend that reports which part of the transcript is settled. The settled text is the
    /// authority: when it changes, the line it describes is replaced rather than repeated.
    /// </summary>
    private IReadOnlyList<TranscriptUpdate> AcceptStable(string cumulative, string stableText)
    {
        var updates = new List<TranscriptUpdate>();
        stableMode = true;

        int stableLimit = CommonPrefixLength(cumulative, stableText);
        if (stableLimit > cumulative.Length)
            stableLimit = cumulative.Length;

        if (stableConsumed.Length > 0 && !cumulative.StartsWith(stableConsumed, StringComparison.Ordinal))
        {
            // The transcript was rewritten - a recycled session, or a final pass that re-decoded a
            // window reaching back past the last sentence. If it shares nothing at the start, it is
            // either a fresh session (the new sentences simply follow the ones already shown) or a
            // rewrite of the one sentence that was on screen (that line must be replaced).
            int matched = CommittedPrefixCount(cumulative);
            if (matched == 0 && Overlap(stableConsumed, cumulative) == 0)
            {
                string? first = FirstSentence(cumulative, stableLimit);
                bool revision = sentences.Count > 0 && first is not null &&
                                IsRevision(sentences[^1], first, nearTail: true);
                if (!revision)
                    sentences.Clear();
            }
        }

        stableConsumed = cumulative;

        var parsed = new List<string>();
        foreach (string text in SplitSentences(cumulative[..stableLimit]))
        {
            if (!string.IsNullOrWhiteSpace(text))
                parsed.Add(text);
        }

        int shared = 0;
        while (shared < parsed.Count && shared < sentences.Count && parsed[shared] == sentences[shared])
            shared++;

        for (int i = shared; i < parsed.Count; i++)
        {
            if (i < sentences.Count && IsRevision(sentences[i], parsed[i], i >= sentences.Count - RevisionWindow))
            {
                updates.Add(new TranscriptUpdate(parsed[i], Replaces: sentences[i]));
                sentences[i] = parsed[i];
                continue;
            }

            if (i < sentences.Count)
            {
                // Not the same sentence: the text belongs to a different transcript, so what was
                // committed here can no longer be compared with it.
                sentences.RemoveRange(i, sentences.Count - i);
            }

            updates.Add(new TranscriptUpdate(parsed[i]));
            sentences.Add(parsed[i]);
        }

        // A revision that dropped a sentence leaves its line on screen; forgetting it here means the
        // sentence is not re-committed later.
        if (sentences.Count > parsed.Count)
            sentences.RemoveRange(parsed.Count, sentences.Count - parsed.Count);

        buffer = cumulative[stableLimit..];
        return updates;
    }

    /// <summary>The first settled sentence of the transcript, if there is one.</summary>
    private static string? FirstSentence(string text, int limit)
    {
        foreach (string sentence in SplitSentences(text[..limit]))
        {
            if (!string.IsNullOrWhiteSpace(sentence))
                return sentence;
        }

        return null;
    }

    /// <summary>True when <paramref name="now"/> is the recogniser's revised form of <paramref name="was"/>.</summary>
    private static bool IsRevision(string was, string now, bool nearTail)
    {
        if (!nearTail)
            return false;

        if (string.Equals(was, now, StringComparison.Ordinal))
            return false;

        string a = was.TrimEnd(TrailingPunctuation);
        string b = now.TrimEnd(TrailingPunctuation);

        if (a.Length == 0 || b.Length == 0)
            return false;

        // A correction only ever reaches back a sentence or two - a re-decoded window. An unrelated
        // sentence far from the end means the backend restarted, not that history changed.
        if (b.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(b, StringComparison.Ordinal))
            return true;

        return TextUtil.Similarity(a, b) >= 0.5;
    }

    /// <summary>
    /// A backend that only has a growing transcript. A full stop ends a sentence, and a callback that
    /// does not continue what was read before is treated as a replay or a restarted transcript.
    /// </summary>
    private IReadOnlyList<TranscriptUpdate> AcceptStreaming(string cumulative, bool isFinal)
    {
        var committed = new List<TranscriptUpdate>();
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
    private void Add(List<TranscriptUpdate> committed, string sentence, bool replay)
    {
        if (replay && sentences.Contains(sentence))
            return;

        committed.Add(new TranscriptUpdate(sentence));
        sentences.Add(sentence);
        Remember();
    }

    private void Remember()
    {
        // The settled sentence list is compared with the whole stable text, so it has to stay
        // complete. Only the streaming mode needs a bound (it is a replay-dedupe window).
        if (stableMode)
            return;

        while (sentences.Count > RecentCapacity)
            sentences.RemoveAt(0);
    }

    /// <summary>How many already-committed sentences still start the transcript, in order.</summary>
    private int CommittedPrefixCount(string text)
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

        return count;
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
    private static IEnumerable<string> SplitSentences(string text)
    {
        int start = 0;

        while (start < text.Length)
        {
            int index = FindSentenceEnd(text, start, complete: true);
            int stop = index < 0 ? text.Length : index + 1;
            string candidate = text[start..stop].Trim();

            if (candidate.Length > 0 && candidate.Any(char.IsLetterOrDigit))
                yield return candidate;

            start = stop;
        }
    }

    /// <summary>
    /// First usable sentence end at or after <paramref name="start"/>. A period counts only when it is
    /// followed by whitespace or the end of the text, so "3.5" and "U.S." stay in one piece. In a
    /// streaming buffer a trailing period is not usable yet: the next chunk may be the fraction.
    /// </summary>
    private static int FindSentenceEnd(string text, int start, bool complete)
    {
        int from = start;

        while (from < text.Length)
        {
            int at = text.IndexOfAny(SentenceEnds, from);
            if (at < 0)
                return -1;

            if (text[at] != '.')
                return at;

            if (at + 1 >= text.Length)
                return complete ? at : -1;

            char next = text[at + 1];

            // English recognition often runs sentences together without a space ("loved.Except"),
            // while a decimal or an initial ("3.5", "U.S.A") is followed by a letter or digit.
            if (char.IsWhiteSpace(next) || char.IsUpper(next))
                return at;

            from = at + 1;
        }

        return -1;
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
        buffer = string.Empty;
        sentences.Clear();
        stableMode = false;
    }

    /// <summary>Commits the leading finished sentence of the streaming buffer, if there is one.</summary>
    private bool TryTakeSentence(out string sentence)
    {
        sentence = string.Empty;

        // A full stop ends a sentence. Stray punctuation - a lone "，" at the start, say - is skipped
        // instead of being returned as an empty sentence.
        while (true)
        {
            int end = FindSentenceEnd(buffer, 0, complete: false);
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
