namespace LiveCaptionsTranslator.Core.Asr;

/// <summary>
/// A session that is technically open but no longer delivering anything. Long-lived recognition
/// sockets get recycled by the services themselves, and sometimes the peer simply goes quiet; both
/// look identical to the user - captions stop while the audio keeps playing - so the provider drops
/// the session and opens a new one.
/// </summary>
public sealed class AsrSessionStalledException : Exception
{
    public AsrSessionStalledException(string reason)
        : base(reason)
    {
    }
}
