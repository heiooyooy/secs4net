namespace Secs4Net;

/// <summary>
/// T3 expired before the reply to a primary message arrived (SEMI E37 §9.2.1, E5 §8.3). <see cref="Header"/> is
/// the header the primary message was sent with, so an equipment can report the transaction it abandoned with
/// S9F9, whose text is that header (SEMI E5 §10.13, E30 §7.10.3.2).
/// </summary>
public sealed class SecsTransactionTimeoutException(SecsMessage secsMessage, MessageHeader header, string message)
    : TimeoutException(message)
{
    /// <summary>The primary message that was not replied to.</summary>
    public SecsMessage SecsMessage { get; } = secsMessage;

    /// <summary>The ten header bytes the primary message was sent with.</summary>
    public MessageHeader Header { get; } = header;
}
