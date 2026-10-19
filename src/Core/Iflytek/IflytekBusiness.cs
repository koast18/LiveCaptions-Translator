namespace LiveCaptionsTranslator.Core.Iflytek
{
    /// <summary>
    /// The <c>business</c> object carried by the first frame.
    /// <para>
    /// Property order matters: the wire format serialises the keys as
    /// <c>language, domain, accent, vad_eos, dwa, nunum, ptt</c>. A <c>null</c> value omits
    /// the key entirely.
    /// </para>
    /// </summary>
    public sealed record IflytekBusiness
    {
        public string? Language { get; init; } = "zh_cn";

        public string? Domain { get; init; } = "iat";

        public string? Accent { get; init; } = "mandarin";

        /// <summary>Silence in milliseconds after which the service ends the current sentence.</summary>
        public int? VadEos { get; init; } = 3000;

        /// <summary><c>wpgs</c> enables streaming results with <c>apd</c>/<c>rpl</c> revisions.</summary>
        public string? Dwa { get; init; } = "wpgs";

        /// <summary>Emit numbers as Arabic digits.</summary>
        public int? Nunum { get; init; } = 1;

        /// <summary>Push-to-talk mode, used by the keyboard client this protocol was captured from.</summary>
        public int? Ptt { get; init; } = 1;

        public static IflytekBusiness Default { get; } = new();
    }
}
