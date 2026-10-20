using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Secs4Net;

/// <summary>
/// One HSMS message as received: its header and, for a data message with text, the decoded root item.
/// </summary>
internal readonly record struct DecodedMessage(MessageHeader Header, Item? RootItem);

/// <summary>
/// Raised when the received byte stream breaks a rule of SEMI E37 / E37.1 that does not depend on the
/// connection state. HSMS-SS without the Reject procedure treats every such case as a communications
/// failure and closes the TCP/IP connection (E37.1 §7.5, Tables 1 and 2).
/// </summary>
internal sealed class HsmsProtocolException(string message) : Exception(message);

/// <summary>
/// Frames HSMS messages from one TCP/IP connection's byte stream (SEMI E37 §8.2). Each message is
/// framed by its Message Length; the text is decoded only once all of it has arrived, so a text that
/// does not fill its length exactly can never shift the framing of the next message.
/// </summary>
internal sealed class PipeDecoder
{
    private const int LengthFieldSize = 4;
    private const int HeaderSize = 10;
    private const ushort ControlSessionId = 0xFFFF;

    private readonly PipeReader _reader;
    private readonly TimeSpan _t8;

    public PipeDecoder(PipeReader reader, int t8Milliseconds)
    {
        _reader = reader;
        _t8 = TimeSpan.FromMilliseconds(t8Milliseconds);
    }

    /// <summary>
    /// Reads the next message. Returns null when the remote entity closed the connection between messages.
    /// </summary>
    /// <exception cref="HsmsProtocolException">The stream violates SEMI E37 / E37.1.</exception>
    /// <exception cref="TimeoutException">T8 expired while a message was being received (E37 §9.2.3.1).</exception>
    public async ValueTask<DecodedMessage?> ReadMessageAsync(CancellationToken cancellation)
    {
        // Waiting for the first byte of a message is idle time; T8 starts only once a message has begun.
        var result = await _reader.ReadAsync(cancellation).ConfigureAwait(false);
        if (result.Buffer.IsEmpty && result.IsCompleted)
        {
            return null;
        }

        var buffer = await ReadAtLeastAsync(result, LengthFieldSize, cancellation).ConfigureAwait(false);
        var messageLength = ReadMessageLength(buffer);
        if (messageLength < HeaderSize)
        {
            throw new HsmsProtocolException(
                $"Message Length {messageLength} is less than the 10-byte header (SEMI E37 §8.2.4, E37.1 Tables 1 and 2).");
        }

        var frameLength = LengthFieldSize + (long)messageLength;
        buffer = await ReadAtLeastAsync(_reader, buffer, frameLength, cancellation).ConfigureAwait(false);
        var frame = buffer.Slice(0, frameLength);
        try
        {
            return DecodeFrame(frame.Slice(LengthFieldSize), messageLength);
        }
        finally
        {
            _reader.AdvanceTo(frame.End);
        }
    }

    private static uint ReadMessageLength(ReadOnlySequence<byte> buffer)
    {
        Span<byte> lengthBytes = stackalloc byte[LengthFieldSize];
        buffer.Slice(0, LengthFieldSize).CopyTo(lengthBytes);
        return BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
    }

    private static DecodedMessage DecodeFrame(ReadOnlySequence<byte> message, uint messageLength)
    {
        MessageHeader header = default;
        message.Slice(0, HeaderSize).CopyTo(header);
        ThrowIfHeaderInvalid(header, messageLength);

        if (messageLength == HeaderSize)
        {
            return new DecodedMessage(header, RootItem: null);
        }

        var text = message.Slice(HeaderSize);
        Item rootItem;
        try
        {
            rootItem = Item.DecodeFromFullBuffer(ref text);
        }
        catch (Exception ex) when (ex is not HsmsProtocolException)
        {
            throw new HsmsProtocolException($"The message text is not a valid SECS-II item ({ex.Message}).");
        }

        if (!text.IsEmpty)
        {
            throw new HsmsProtocolException(
                $"The message text holds {text.Length} byte(s) after its root SECS-II item; Message Length does not match the text.");
        }

        return new DecodedMessage(header, rootItem);
    }

    /// <summary>
    /// The header rules of SEMI E37 §8 and E37.1 §8 that hold in every connection state. An HSMS-SS entity
    /// without Reject treats a violation as a communications failure (E37.1 §7.5 and §7.7).
    /// </summary>
    private static void ThrowIfHeaderInvalid(ReadOnlySpan<byte> header, uint messageLength)
    {
        // MessageHeader holds the ten header bytes as received (SEMI E37 Table 3).
        var sessionId = BinaryPrimitives.ReadUInt16BigEndian(header);
        var byte2 = header[2];
        var byte3 = header[3];
        var pType = header[4];
        var sType = (MessageType)header[5];

        if (pType != 0)
        {
            throw new HsmsProtocolException($"PType {pType} is not supported; HSMS-SS messages are PType 0 (SEMI E37.1 §8.2).");
        }

        if (!Enum.IsDefined(sType))
        {
            throw new HsmsProtocolException(
                $"SType {(byte)sType} is not defined by HSMS; HSMS-SS permits only HSMS-defined STypes (SEMI E37.1 §8.3).");
        }

        if (sType == MessageType.DataMessage)
        {
            if ((sessionId & 0x8000) != 0)
            {
                throw new HsmsProtocolException(
                    $"Data message Session ID 0x{sessionId:X4} has its high-order bit set (SEMI E37.1 §8.1).");
            }

            return;
        }

        if (messageLength != HeaderSize)
        {
            throw new HsmsProtocolException(
                $"Control message {sType} has Message Length {messageLength}; control messages are header only (SEMI E37 §8.3, §9.3.3.1).");
        }

        if (sessionId != ControlSessionId)
        {
            throw new HsmsProtocolException(
                $"Control message {sType} has Session ID 0x{sessionId:X4}; HSMS-SS control messages use 0xFFFF (SEMI E37.1 §8.1.1).");
        }

        if (byte2 != 0 && sType != MessageType.Reject_req)
        {
            throw new HsmsProtocolException($"Control message {sType} has header byte 2 = {byte2}; it must be 0 (SEMI E37 §8.3).");
        }

        if (byte3 != 0 && !CarriesStatusInByte3(sType))
        {
            throw new HsmsProtocolException($"Control message {sType} has header byte 3 = {byte3}; it must be 0 (SEMI E37 §8.3).");
        }
    }

    private static bool CarriesStatusInByte3(MessageType sType)
    {
        return sType is MessageType.SelectResponse or MessageType.Deselect_rsp or MessageType.Reject_req;
    }

    private ValueTask<ReadOnlySequence<byte>> ReadAtLeastAsync(ReadResult first, long required, CancellationToken cancellation)
    {
        if (first.Buffer.Length >= required)
        {
            return new(first.Buffer);
        }

        ThrowIfCompletedMidMessage(first);
        return ReadAtLeastAsync(_reader, first.Buffer, required, cancellation);
    }

    /// <summary>
    /// Reads until <paramref name="required"/> bytes of the current message are buffered. Every read here
    /// is inside a message, so each one is bounded by T8, the maximum time between successive bytes of a
    /// single HSMS message (SEMI E37 §9.2.3).
    /// </summary>
    private async ValueTask<ReadOnlySequence<byte>> ReadAtLeastAsync(
        PipeReader reader,
        ReadOnlySequence<byte> buffer,
        long required,
        CancellationToken cancellation)
    {
        while (buffer.Length < required)
        {
            reader.AdvanceTo(consumed: buffer.Start, examined: buffer.End);
            var result = await ReadWithinT8Async(reader, cancellation).ConfigureAwait(false);
            buffer = result.Buffer;
            if (buffer.Length < required)
            {
                ThrowIfCompletedMidMessage(result);
            }
        }

        return buffer;
    }

    private async ValueTask<ReadResult> ReadWithinT8Async(PipeReader reader, CancellationToken cancellation)
    {
        using var t8 = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        t8.CancelAfter(_t8);
        try
        {
            return await reader.ReadAsync(t8.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"T8 Timeout: no byte of the current HSMS message arrived within {_t8.TotalSeconds} sec.");
        }
    }

    private static void ThrowIfCompletedMidMessage(ReadResult result)
    {
        if (result.IsCompleted)
        {
            throw new HsmsProtocolException("The TCP/IP connection closed in the middle of an HSMS message.");
        }
    }
}
