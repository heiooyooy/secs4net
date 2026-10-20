using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Secs4Net;

/// <summary>
/// HSMS-SS connection (SEMI E37.1) in Passive or Active connect mode.
/// <para>
/// One TCP/IP connection is served at a time. Every message of a connection is handled in the order it
/// was received: a control message is processed before the next message is read, so a data message that
/// follows a successful Select always finds the connection SELECTED. The Reject procedure is not
/// supported (E37.1 §5.1, §7.5); every situation that would call for it, and every violation of an
/// HSMS-SS restriction (§7.7), is a communications failure that closes the TCP/IP connection.
/// </para>
/// <para>
/// <see cref="ConnectionChanged"/> handlers run while the connection's state is held, so they observe the
/// states in order. They must only record or queue the change and must not block.
/// </para>
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed class HsmsConnection : ISecsConnection, IAsyncDisposable
{
    private const byte SelectStatusEstablished = 0;
    private const ushort ControlSessionId = 0xFFFF;

    public event EventHandler<ConnectionState>? ConnectionChanged;
    public int T5 { get; }
    public int T6 { get; }
    public int T7 { get; }
    public int T8 { get; }
    public int LinkTestInterval { get; }
    public bool LinkTestEnabled { get; set; }

    public ConnectionState State { get; private set; }
    public bool IsActive { get; }
    public IPAddress IpAddress { get; }
    public int Port { get; }

    public string DeviceIpAddress
        => IsActive
        ? IpAddress.ToString()
        : FindRemoteAddress(Volatile.Read(ref _link)) ?? "NA";

    private const int DisposalNotStarted = 0;
    private const int DisposalComplete = 1;
    private int _disposeStage;

    public bool IsDisposed
        => Interlocked.CompareExchange(ref _disposeStage, DisposalComplete, DisposalComplete) == DisposalComplete;

    private readonly ISecsGemLogger _logger;
    private readonly int _socketReceiveBufferSize;
    private readonly Socket? _listener;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _sendLock = new(initialCount: 1);
    private readonly CancellationTokenSource _lifetime = new();

    private readonly Channel<DecodedMessage> _dataMessageChannel = Channel
        .CreateUnbounded<DecodedMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

    private HsmsLink? _link;
    private Task _connectionLoop = Task.CompletedTask;
    private Task _linkTestLoop = Task.CompletedTask;
    private int _started;

    public HsmsConnection(IOptions<SecsGemOptions> secsGemOptions, ISecsGemLogger logger)
    {
        _logger = logger;
        var options = secsGemOptions.Value;
        T5 = options.T5;
        T6 = options.T6;
        T7 = options.T7;
        T8 = options.T8;
        LinkTestInterval = options.LinkTestInterval;
        IpAddress = IPAddress.Parse(options.IpAddress);
        Port = options.Port;
        IsActive = options.IsActive;
        _socketReceiveBufferSize = options.SocketReceiveBufferSize;

        if (!IsActive)
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IpAddress, Port));
            listener.Listen(0);
            _listener = listener;
        }
    }

    private static string? FindRemoteAddress(HsmsLink? link)
    {
        try
        {
            return (link?.Socket.RemoteEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts connection establishment. <paramref name="cancellation"/> stops further connect, accept and
    /// Linktest attempts; it does not close a connection that is already open (<see cref="DisposeAsync"/> does).
    /// </summary>
    public void Start(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token).Token;
        _connectionLoop = Task.Run(() => RunConnectionLoopAsync(stopping));
        _linkTestLoop = Task.Run(() => RunLinkTestLoopAsync(stopping));
    }

    /// <summary>
    /// Closes the current TCP/IP connection, if any, as a communications failure. Connection establishment
    /// then starts again according to the connect mode.
    /// </summary>
    public void Reconnect()
    {
        var link = Volatile.Read(ref _link);
        if (link is not null)
        {
            CloseLink(link, "Reconnect requested after a transport error.");
        }
    }

    #region Connection establishment (E37 §6.3, E37.1 §5.5 and §5.6)

    private async Task RunConnectionLoopAsync(CancellationToken stopping)
    {
        var firstAttempt = true;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (IsActive && !firstAttempt)
                {
                    // E37 §9.2.1.1 and E37.1 Table 2 #4: after an active connect procedure ends, or a
                    // connection opened by it is closed, wait T5 before the next connect.
                    _logger.Info($"Start T5 Timer: {T5 / 1000.0} sec.");
                    await Task.Delay(T5, stopping).ConfigureAwait(false);
                }

                firstAttempt = false;
                SetStateWithoutLink(ConnectionState.Connecting);
                var socket = IsActive
                    ? await ConnectAsync(stopping).ConfigureAwait(false)
                    : await AcceptAsync(stopping).ConfigureAwait(false);
                if (socket is null)
                {
                    continue;
                }

                var link = new HsmsLink(socket, PipeReader.Create(new NetworkStream(socket, ownsSocket: false)));
                await RunLinkAsync(link).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error("Unexpected failure in the HSMS connection loop", ex);
            }
        }
    }

    private async Task<Socket?> ConnectAsync(CancellationToken stopping)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = _socketReceiveBufferSize,
        };

        try
        {
            await socket.ConnectAsync(IpAddress, Port, stopping).ConfigureAwait(false);
            return socket;
        }
        catch (Exception ex) when (!stopping.IsCancellationRequested)
        {
            _logger.Error($"Active connect to {IpAddress}:{Port} failed: {ex.Message}");
            socket.Dispose();
            return null;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Accepts the next connection on the published port. The listener is not accepted from while a
    /// connection is open, so a second connection request is not serviced until the current connection
    /// ends (E37 §9.2.4.1.1, third option).
    /// </summary>
    private async Task<Socket?> AcceptAsync(CancellationToken stopping)
    {
        try
        {
            var socket = await _listener!.AcceptAsync(stopping).ConfigureAwait(false);
            socket.ReceiveBufferSize = _socketReceiveBufferSize;
            return socket;
        }
        catch (Exception ex) when (!stopping.IsCancellationRequested)
        {
            _logger.Error($"Passive accept on port {Port} failed: {ex.Message}");
            await Task.Delay(1000, stopping).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Runs one TCP/IP connection from CONNECTED / NOT SELECTED until it is closed.
    /// </summary>
    private async Task RunLinkAsync(HsmsLink link)
    {
        lock (_stateGate)
        {
            Volatile.Write(ref _link, link);
            ChangeState(ConnectionState.Connected);
        }

        // A connection accepted or connected while DisposeAsync runs is closed as soon as it exists.
        using var disposal = _lifetime.Token.Register(() => CloseLink(link, "The HSMS connection is being disposed."));
        var receiving = Task.Run(() => ReceiveAsync(link));
        var t7 = Task.Run(() => EnforceT7Async(link));
        if (IsActive)
        {
            // E37.1 §7.1 and Table 2 #2: only the active entity initiates Select, on the new connection.
            await SendControlRequestAsync(link, MessageType.SelectRequest).ConfigureAwait(false);
        }

        await link.Closed.ConfigureAwait(false);
        await Task.WhenAll(receiving, t7).ConfigureAwait(false);
        await link.DisposeAsync().ConfigureAwait(false);
        lock (_stateGate)
        {
            Interlocked.CompareExchange(ref _link, null, link);
        }
    }

    /// <summary>
    /// E37 §9.2.2 and E37.1 Table 1 #4: a connection that has not become SELECTED within T7 is closed.
    /// </summary>
    private async Task EnforceT7Async(HsmsLink link)
    {
        _logger.Info($"Start T7 Timer: {T7 / 1000.0} sec.");
        try
        {
            await Task.Delay(T7, link.Cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!link.IsSelected)
        {
            CloseLink(link, $"T7 Timeout: {T7 / 1000.0} sec in NOT SELECTED.");
        }
    }

    #endregion

    #region Receiving (E37.1 Tables 1 and 2, §7)

    private async Task ReceiveAsync(HsmsLink link)
    {
        var decoder = new PipeDecoder(link.Reader, T8);
        try
        {
            while (true)
            {
                var message = await decoder.ReadMessageAsync(link.Cancellation).ConfigureAwait(false);
                if (message is null)
                {
                    CloseLink(link, "The remote entity closed the TCP/IP connection.");
                    return;
                }

                await HandleMessageAsync(link, message.Value).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (link.Cancellation.IsCancellationRequested)
        {
        }
        catch (TimeoutException ex)
        {
            CloseLink(link, ex.Message);
        }
        catch (HsmsProtocolException ex)
        {
            CloseLink(link, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected failure receiving HSMS messages", ex);
            CloseLink(link, "TCP/IP receive failed.");
        }
    }

    private async ValueTask HandleMessageAsync(HsmsLink link, DecodedMessage message)
    {
        var header = message.Header;
        if (header.MessageType == MessageType.DataMessage)
        {
            // E37 §7.5 and E37.1 Tables 1/2 #4: a data message is valid only in SELECTED; without Reject,
            // one received in NOT SELECTED is a communications failure.
            if (!link.IsSelected)
            {
                CloseLink(link, $"Data message S{header.S}F{header.F} received in NOT SELECTED.");
                return;
            }

            await _dataMessageChannel.Writer.WriteAsync(message, link.Cancellation).ConfigureAwait(false);
            return;
        }

        _logger.Info("Receive Control message: " + header.MessageType);
        switch (header.MessageType)
        {
            case MessageType.SelectRequest:
                await HandleSelectRequestAsync(link, header).ConfigureAwait(false);
                break;
            case MessageType.SelectResponse:
            case MessageType.LinkTestResponse:
                HandleControlResponse(link, header);
                break;
            case MessageType.LinkTestRequest:
                await HandleLinktestRequestAsync(link, header).ConfigureAwait(false);
                break;
            case MessageType.SeparateRequest:
                // E37.1 §7.6: after receiving Separate.req the entity closes the TCP/IP connection.
                CloseLink(link, "Separate.req received.");
                break;
            case MessageType.Deselect_req:
            case MessageType.Deselect_rsp:
                // E37.1 §7.3: Deselect is not used in HSMS-SS; §7.7 makes its use a communications failure.
                CloseLink(link, $"{header.MessageType} received; HSMS-SS does not use Deselect.");
                break;
            case MessageType.Reject_req:
                // The remote entity found a message from this entity inappropriate (E37 §7.10.1, action
                // local entity-specific). This entity does not support Reject and ends the connection.
                CloseLink(link, $"Reject.req received for SType/PType {header[2]} with reason code {header.F}.");
                break;
        }
    }

    private async ValueTask HandleSelectRequestAsync(HsmsLink link, MessageHeader header)
    {
        if (IsActive)
        {
            // E37.1 §7.1: the Select procedure is initiated only by the active entity.
            CloseLink(link, "Select.req received by the active entity; only the active entity initiates Select.");
            return;
        }

        if (link.IsSelected)
        {
            // E37.1 §7.1.1: Select is permitted only in NOT SELECTED; §7.7 makes this a communications failure.
            CloseLink(link, "Select.req received in SELECTED.");
            return;
        }

        // E37.1 Table 1 #3: send Select.rsp with zero SelectStatus, cancel T7, enter SELECTED.
        if (await TrySendControlMessageAsync(link, MessageType.SelectResponse, header.Id, SelectStatusEstablished).ConfigureAwait(false))
        {
            TryEnterSelected(link);
        }
    }

    private async ValueTask HandleLinktestRequestAsync(HsmsLink link, MessageHeader header)
    {
        if (!link.IsSelected)
        {
            // E37.1 §7.4 and Table 1 #4: Linktest is limited to SELECTED.
            CloseLink(link, "Linktest.req received in NOT SELECTED.");
            return;
        }

        await TrySendControlMessageAsync(link, MessageType.LinkTestResponse, header.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes the open control transaction a response belongs to. A response with no open transaction
    /// of its kind would be rejected with "Transaction Not Open" (E37 Table 9); without Reject it is a
    /// communications failure (E37.1 §7.5).
    /// </summary>
    private void HandleControlResponse(HsmsLink link, MessageHeader header)
    {
        if (!link.TryTakeTransaction(header.Id, header.MessageType, out var transaction))
        {
            CloseLink(link, $"{header.MessageType} 0x{header.Id:X8} received with no open transaction for it.");
            return;
        }

        if (header.MessageType == MessageType.SelectResponse)
        {
            CompleteSelect(link, selectStatus: header.F);
        }

        transaction.TrySetResult();
    }

    /// <summary>
    /// E37.1 Table 2 #3 and #4: a zero SelectStatus enters SELECTED; any other status closes the TCP/IP
    /// connection, and the next connect waits T5 (§7.1.1). Entering SELECTED here, while the response is
    /// being processed, keeps it ahead of any data message the passive entity sends right after it.
    /// </summary>
    private void CompleteSelect(HsmsLink link, byte selectStatus)
    {
        if (selectStatus == SelectStatusEstablished)
        {
            TryEnterSelected(link);
            return;
        }

        CloseLink(link, $"Select.rsp with SelectStatus {selectStatus} ({DescribeSelectStatus(selectStatus)}).");
    }

    private static string DescribeSelectStatus(byte selectStatus)
    {
        // SEMI E37 Table 7.
        return selectStatus switch
        {
            1 => "Communication Already Active",
            2 => "Connection Not Ready",
            3 => "Connect Exhaust",
            < 128 => "reserved for Subordinate Standards",
            _ => "local entity-specific",
        };
    }

    #endregion

    #region Control transactions (E37 §9.3.1)

    /// <summary>
    /// Sends a control request and waits T6 for its response. Expiry of T6 is a communications failure
    /// (E37 §9.3.1.1, E37.1 Table 2 #4 and Table 1/2 #5).
    /// </summary>
    private async Task SendControlRequestAsync(HsmsLink link, MessageType request)
    {
        var id = MessageIdGenerator.NewId();
        var transaction = link.OpenTransaction(id, ResponseTo(request));
        if (!await TrySendControlMessageAsync(link, request, id).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await transaction.Task.WaitAsync(TimeSpan.FromMilliseconds(T6), link.Cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CloseLink(link, $"T6 Timeout[id=0x{id:X8}]: no {ResponseTo(request)} within {T6 / 1000.0} sec.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            link.TryTakeTransaction(id, ResponseTo(request), out _);
        }
    }

    private static MessageType ResponseTo(MessageType request)
    {
        return request switch
        {
            MessageType.SelectRequest => MessageType.SelectResponse,
            MessageType.LinkTestRequest => MessageType.LinkTestResponse,
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, "Not a control request that HSMS-SS sends."),
        };
    }

    private async ValueTask<bool> TrySendControlMessageAsync(HsmsLink link, MessageType messageType, int id, byte headerByte3 = 0)
    {
        try
        {
            await SendFrameAsync(link, EncodeControlMessage(messageType, id, headerByte3), link.Cancellation).ConfigureAwait(false);
            _logger.Info("Sent Control Message: " + messageType);
            return true;
        }
        catch (OperationCanceledException) when (link.Cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"Sending {messageType} failed", ex);
            CloseLink(link, $"TCP/IP send of {messageType} failed.");
            return false;
        }
    }

    private static readonly ReadOnlyMemory<byte> ControlMessageLengthBytes = new byte[] { 0, 0, 0, 10 };

    private static ReadOnlyMemory<byte> EncodeControlMessage(MessageType messageType, int id, byte headerByte3)
    {
        // E37 Table 6 and E37.1 §8.1.1: Session ID 0xFFFF, header byte 2 = 0, PType = 0.
        var header = new MessageHeader { DeviceId = ControlSessionId, MessageType = messageType, F = headerByte3, Id = id };
        var buffer = new MemoryBufferWriter<byte>(new byte[14]);
        buffer.Write(ControlMessageLengthBytes.Span);
        buffer.Write(header);
        return buffer.WrittenMemory;
    }

    private async Task RunLinkTestLoopAsync(CancellationToken stopping)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(LinkTestInterval));
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                // E37.1 §7.4: Linktest only in SELECTED.
                var link = Volatile.Read(ref _link);
                if (!LinkTestEnabled || link is null || !link.IsSelected)
                {
                    continue;
                }

                await SendControlRequestAsync(link, MessageType.LinkTestRequest).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    #endregion

    #region State

    private void TryEnterSelected(HsmsLink link)
    {
        lock (_stateGate)
        {
            if (link.IsClosing || link.IsSelected)
            {
                return;
            }

            link.MarkSelected();
            ChangeState(ConnectionState.Selected);
        }

        _logger.Info("Stop T7 Timer");
    }

    /// <summary>
    /// Ends a connection: E37 §9.1.1 and E37.1 Tables 1/2 — a communications failure closes the TCP/IP
    /// connection. The first call for a connection wins; later triggers for the same connection are ignored.
    /// </summary>
    private void CloseLink(HsmsLink link, string reason)
    {
        lock (_stateGate)
        {
            if (!link.TryBeginClose())
            {
                return;
            }

            _logger.Error($"Closing HSMS connection: {reason}");
            if (ReferenceEquals(Volatile.Read(ref _link), link))
            {
                ChangeState(ConnectionState.Retry);
            }
        }

        link.Close();
    }

    private void SetStateWithoutLink(ConnectionState state)
    {
        lock (_stateGate)
        {
            ChangeState(state);
        }
    }

    /// <summary>Called with <see cref="_stateGate"/> held.</summary>
    private void ChangeState(ConnectionState state)
    {
        State = state;
        ConnectionChanged?.Invoke(this, state);
    }

    #endregion

    #region Sending

    Task ISecsConnection.SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellation)
    {
        var link = Volatile.Read(ref _link);
        if (link is null || link.IsClosing)
        {
            throw new SocketException((int)SocketError.NotConnected);
        }

        return SendFrameAsync(link, buffer, cancellation);
    }

    /// <summary>
    /// Writes one whole frame to the connection's socket. A failed or cancelled write may have left part of
    /// the frame on the stream, so it closes the connection rather than let the next frame follow it.
    /// </summary>
    private async Task SendFrameAsync(HsmsLink link, ReadOnlyMemory<byte> buffer, CancellationToken cancellation)
    {
        await _sendLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            while (!buffer.IsEmpty)
            {
                var length = await link.Socket.SendAsync(buffer, SocketFlags.None, cancellation).ConfigureAwait(false);
                buffer = buffer[length..];
            }
        }
        catch
        {
            if (!buffer.IsEmpty)
            {
                CloseLink(link, "A frame was only partly written to the TCP/IP connection.");
            }

            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    IAsyncEnumerable<DecodedMessage> ISecsConnection.GetDataMessages(CancellationToken cancellation)
        => _dataMessageChannel.Reader.ReadAllAsync(cancellation);

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStage, DisposalComplete) != DisposalNotStarted)
        {
            return;
        }

        ConnectionChanged = null;
        var link = Volatile.Read(ref _link);
        if (link is { IsSelected: true, IsClosing: false })
        {
            // E37.1 §7.3 and §7.6: communication is ended with Separate, then the TCP/IP connection is closed.
            await TrySendControlMessageAsync(link, MessageType.SeparateRequest, MessageIdGenerator.NewId()).ConfigureAwait(false);
        }

        if (link is not null)
        {
            CloseLink(link, "The HSMS connection is being disposed.");
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        await Task.WhenAll(_connectionLoop, _linkTestLoop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _dataMessageChannel.Writer.TryComplete();
        _lifetime.Dispose();
        _sendLock.Dispose();
    }

    /// <summary>
    /// One TCP/IP connection: its socket, its reader, its open control transactions and whether it has
    /// reached SELECTED. Closing it cancels its receive loop, its timers and its open transactions.
    /// </summary>
    private sealed class HsmsLink(Socket socket, PipeReader reader) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<int, OpenControlTransaction> _openTransactions = new();
        private volatile bool _isSelected;
        private int _closing;

        public Socket Socket { get; } = socket;
        public PipeReader Reader { get; } = reader;
        public CancellationToken Cancellation => _cancellation.Token;
        public Task Closed => _closed.Task;
        public bool IsSelected => _isSelected;
        public bool IsClosing => Volatile.Read(ref _closing) != 0;

        public void MarkSelected() => _isSelected = true;

        public bool TryBeginClose() => Interlocked.Exchange(ref _closing, 1) == 0;

        public TaskCompletionSource OpenTransaction(int id, MessageType expectedResponse)
        {
            var transaction = new OpenControlTransaction(expectedResponse);
            _openTransactions[id] = transaction;
            return transaction.Completion;
        }

        public bool TryTakeTransaction(int id, MessageType response, out TaskCompletionSource completion)
        {
            completion = null!;
            if (!_openTransactions.TryGetValue(id, out var transaction) || transaction.ExpectedResponse != response)
            {
                return false;
            }

            if (!_openTransactions.TryRemove(id, out _))
            {
                return false;
            }

            completion = transaction.Completion;
            return true;
        }

        public void Close()
        {
            _isSelected = false;
            _cancellation.Cancel();
            foreach (var transaction in _openTransactions.Values)
            {
                transaction.Completion.TrySetCanceled();
            }

            try
            {
                Socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
                // The socket may already be broken; closing it below is what matters.
            }

            Socket.Close();
            _closed.TrySetResult();
        }

        /// <summary>
        /// Releases the reader and the socket once the connection's tasks have ended. The cancellation
        /// source is left undisposed: it holds no timer, and a Linktest started just before the close may
        /// still read its token.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await Reader.CompleteAsync().ConfigureAwait(false);
            Socket.Dispose();
        }
    }

    private sealed record OpenControlTransaction(MessageType ExpectedResponse)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
