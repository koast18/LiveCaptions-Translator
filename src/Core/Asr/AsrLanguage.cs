namespace LiveCaptionsTranslator.Core.Asr
{
    /// <summary>
    /// A speech-recognition language offered by an <see cref="IAsrProvider"/>.
    /// </summary>
    /// <param name="Id">Provider-neutral language id, e.g. <c>zh_cn</c>, <c>en_us</c>.</param>
    /// <param name="DisplayName">Name shown in the settings UI.</param>
    /// <param name="Accent">Provider specific accent/dialect selector, when applicable.</param>
    /// <param name="Verified">
    /// <c>true</c> only when this language has been confirmed against a real speech sample.
    /// Languages whose parameters are merely accepted by the service must stay <c>false</c>
    /// so the UI and the docs can tell the two apart.
    /// </param>
    public sealed record AsrLanguage(
        string Id,
        string DisplayName,
        string? Accent = null,
        bool Verified = false);
}
