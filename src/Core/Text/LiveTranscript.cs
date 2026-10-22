namespace LiveCaptionsTranslator.Core.Text;

/// <summary>
/// One sentence the recogniser finished, and what it does to the line already on screen.
/// <para>
/// <paramref name="Replaces"/> is set when the backend revised a sentence it had already handed
/// out: the service's final pass is what fixes mis-heard characters, and the corrected sentence
/// belongs where the old one was, not below it. <paramref name="Retract"/> is set when the
/// revision removed a sentence altogether.
/// </para>
/// </summary>
public sealed record TranscriptUpdate(string Text, string? Replaces = null, bool Retract = false);

/// <summary>
/// Turns the cumulative text a recogniser streams into finished sentences.
/// <para>
/// Every callback carries the whole segment so far - recorded from the real endpoint, see
/// tests/fixtures/doubao-partials.txt - so the only sound model is to remember what has already
/// been consumed and process the difference. Splitting the full text again on every callback is what
/// produced duplicated sentences and one ever-growing line.
/// </para>
/// <para>
/// The backend's second/third pass revises text it already sent. When that happens the new
/// transcript no longer starts with what was consumed, but it still shares the beginning of it;
/// the revised tail is handed back as updates that replace the lines already shown, so a correction
/// never becomes a second line.
/// </para>
/// </summary>
public sealed class LiveTranscript
{
    /// <summary>Longest a line may get before it is cut at a pause. Nothing is dropped.</summary>
    public const int MaxLineChars = 42;

    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '…', '!', '?', ';'];

    private static readonly char[] Pauses =
        [' ', '　', '，', '、', '：', ',', ':', '。', '！', '？', '；', '.', '!', '?', ';'];

    private static readonly char[] TrailingPunctuation =
        [' ', '　', '。', '！', '？', '；', '…', '，', '、', '：', '.', '!', '?', ';', ',', ':', '~', '～'];

    private string consumed = string.Empty;
    private string buffer = string.Empty;

    /// <summary>Sentences already handed out, newest last, so a replay or a revision can be recognised.</summary>
    private readonly List<string> sentences = [];

    private const int RecentCapacity = 64;

    /// <summary>How far back a revision is compared; a long session must not make this quadratic.</summary>
    private const int SimilarityTailChars = 400;

    /// <summary>A revision shares its wording with what it replaces; a new session does not.</summary>
    private const double RevisionSimilarity = 0.5;

    /// <summary>The text still being spoken; this is the live line.</summary>
    public string Pending => buffer.Trim();

    public bool HasPending => buffer.Trim().Length > 0;

    /// <summary>Sentences committed so far, newest last. Read-only view for diagnostics.</summary>
    public IReadOnlyList<string> Committed => sentences;

    /// <summary>
    /// Feeds one callback and returns what it finished. Everything absorbed so far is remembered,
    /// so a repeated callback can never commit the same sentence twice, and a revised sentence
    /// comes back marked with the text it replaces.
    /// </summary>
    public IReadOnlyList<TranscriptUpdate> Accept(string? cumulative, bool isFinal)
    {
        var updates = new List<TranscriptUpdate>();
        if (string.IsNullOrEmpty(cumulative))
            return updates;

        string delta;
        bool replay = false;
        List<string>? revised = null;

        if (consumed.Length > 0 && cumulative.StartsWith(consumed, StringComparison.Ordinal))
        {
            delta = cumulative[consumed.Length..];
        }
        else
        {
            // The backend restarted or revised its transcript. It may still begin with what has
            // already been read, so the overlap is skipped instead of being committed twice.
            int overlap = Overlap(consumed, cumulative);
            string tail = cumulative[overlap..];

            // Sentences fully inside the part that still matches are stable. The rest of the old
            // tail may have been revised: it is handed back for replacement, never appended again.
            revised = RevisionCandidates(consumed, cumulative, tail, overlap);

            consumed = string.Empty;
            buffer = string.Empty;
            delta = tail;
            replay = true;
        }

        buffer += delta;
        consumed = cumulative;

        int cursor = 0;
        List<string> dropped = [];

        while (TryTakeSentence(out string sentence))
            Add(updates, sentence, replay, revised, ref cursor, dropped);

        if (isFinal && HasPending)
        {
            string pending = Pending;
            buffer = string.Empty;
            Add(updates, pending, replay, revised, ref cursor, dropped);
        }

        // A revision that removed a sentence: the display has to drop it, or a sentence that is no
        // longer part of the transcript would stay on screen forever.
        if (revised is not null)
        {
            for (int i = cursor; i < revised.Count; i++)
                dropped.Add(revised[i]);

            foreach (string removed in dropped)
                updates.Add(new TranscriptUpdate(removed, Retract: true));
        }

        return updates;
    }

    /// <summary>
    /// Decides which already-committed sentences the backend just revised. Returns those that were
    /// taken out of the committed history, so unpaired ones can be retracted and paired ones
    /// replaced. A transcript that simply starts over keeps everything it already showed.
    /// </summary>
    private List<string>? RevisionCandidates(string seen, string next, string tail, int overlap)
    {
        int stable = StableSentenceCount(next, overlap);
        if (stable >= sentences.Count)
            return null;

        var candidates = sentences.GetRange(stable, sentences.Count - stable);

        // A fresh session starts with different words; a revision keeps the wording of what it
        // revises, either by being similar or by extending the sentence that was cut off.
        bool revision = LooksLikeRevision(seen, next) || PrefixExtends(candidates[^1], tail);
        if (!revision)
            return null;

        sentences.RemoveRange(stable, candidates.Count);
        return candidates;
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
    /// True when <paramref name="next"/> revises <paramref name="seen"/> rather than being a new
    /// transcript from a fresh session. A revision keeps the wording of the transcript it revises;
    /// a session that restarts after a reconnect starts with different words.
    /// </summary>
    private static bool LooksLikeRevision(string seen, string next)
    {
        if (seen.Length == 0)
            return false;

        string a = seen.Length > SimilarityTailChars ? seen[^SimilarityTailChars..] : seen;
        string b = next.Length > SimilarityTailChars ? next[^SimilarityTailChars..] : next;

        return TextUtil.Similarity(a, b) >= RevisionSimilarity;
    }

    /// <summary>
    /// True when the new text still begins with the words of the sentence that was cut off. This is
    /// the shape of a live revision: "嗯。" grows into "嗯，我们开始吧。".
    /// </summary>
    private static bool PrefixExtends(string sentence, string tail)
    {
        string trimmed = sentence.TrimEnd(TrailingPunctuation);
        return trimmed.Length > 0 && tail.TrimStart().StartsWith(trimmed, StringComparison.Ordinal);
    }

    /// <summary>How many already-committed sentences sit entirely inside the still-matching prefix.</summary>
    private int StableSentenceCount(string next, int overlap)
    {
        int stable = 0;
        int cursor = 0;

        foreach (string sentence in sentences)
        {
            int at = next.IndexOf(sentence, cursor, StringComparison.Ordinal);
            if (at < 0 || at + sentence.Length > overlap)
                break;

            cursor = at + sentence.Length;
            stable++;
        }

        return stable;
    }

    /// <summary>
    /// Queues a sentence. When a revision is being replayed, the new sentence replaces the revised
    /// one; a revision that dropped sentences leaves them for the retraction pass at the end.
    /// </summary>
    private void Add(
        List<TranscriptUpdate> updates,
        string sentence,
        bool replay,
        List<string>? revised,
        ref int cursor,
        List<string> dropped)
    {
        string? replaces = null;

        if (revised is not null)
        {
            // A revision can delete sentences, so keep looking past the ones it removed.
            while (cursor < revised.Count && !Revises(revised[cursor], sentence))
                dropped.Add(revised[cursor++]);

            if (cursor < revised.Count)
                replaces = revised[cursor++];
        }

        if (replaces is null && replay && sentences.Contains(sentence))
            return;

        // The same wording came back: nothing on screen changes, but the sentence is still committed.
        if (replaces is not null && string.Equals(replaces, sentence, StringComparison.Ordinal))
        {
            sentences.Add(sentence);
            Remember();
            return;
        }

        updates.Add(new TranscriptUpdate(sentence, replaces));
        sentences.Add(sentence);
        Remember();
    }

    /// <summary>True when <paramref name="now"/> is the recogniser's corrected form of <paramref name="was"/>.</summary>
    private static bool Revises(string was, string now)
    {
        if (string.Equals(was, now, StringComparison.Ordinal))
            return true;

        string a = was.TrimEnd(TrailingPunctuation);
        string b = now.TrimEnd(TrailingPunctuation);

        if (a.Length == 0 || b.Length == 0)
            return false;

        if (b.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(b, StringComparison.Ordinal))
            return true;

        return TextUtil.Similarity(a, b) >= RevisionSimilarity;
    }

    private void Remember()
    {
        while (sentences.Count > RecentCapacity)
            sentences.RemoveAt(0);
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
        buffer = string.Empty;
        sentences.Clear();
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
