namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>
    /// Reassembles the streaming result into a single growing transcript.
    /// <para>
    /// The service numbers sentences with <c>sn</c>. An <c>apd</c> revision overwrites slot
    /// <c>sn</c>; an <c>rpl</c> revision first deletes every slot inside <c>rg</c> and then
    /// writes <c>sn</c>. The final text is the slots joined in ascending order.
    /// </para>
    /// </summary>
    public sealed class IflytekTranscript
    {
        private readonly SortedDictionary<int, string> parts = [];

        public int LastSn { get; private set; }

        /// <summary>Set once the service flags a result with <c>ls</c>.</summary>
        public bool IsLast { get; private set; }

        /// <summary>The transcript so far.</summary>
        public string Text => string.Concat(parts.Values);

        public int PartCount => parts.Count;

        /// <summary>Applies one result and returns the updated transcript.</summary>
        public string Feed(IflytekWordResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (result.Pgs == "rpl" && result.Rg is { Length: >= 2 })
            {
                var removed = parts.Keys
                    .Where(slot => slot >= result.Rg[0] && slot <= result.Rg[1])
                    .ToList();

                foreach (int slot in removed)
                    parts.Remove(slot);
            }

            if (!string.IsNullOrEmpty(result.Text))
                parts[result.Sn] = result.Text;

            LastSn = Math.Max(LastSn, result.Sn);

            if (result.Ls)
                IsLast = true;

            return Text;
        }

        public void Reset()
        {
            parts.Clear();
            LastSn = 0;
            IsLast = false;
        }
    }
}
