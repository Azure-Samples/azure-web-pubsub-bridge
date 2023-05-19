using System.Buffers;
using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using AzureWebPubSubBridge.Exceptions;
using AzureWebPubSubBridge.Messages;
using AzureWebPubSubBridge.Protocols;
using AzureWebPubSubBridge.Tunnels.Connections;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels.PubSub;

public class PubSubTunnel : IAsyncDisposable
{
    public const int DefaultMaxConnectionAttempts = 5;
    public const int DefaultMaxDelayMilliSeconds = 2000;
    public const int DefaultRetryConnectionDelayMilliSeconds = 100;
    public const int DefaultRetrySendDelayMilliSeconds = 10;

    public static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly BridgeConfiguration _configuration;

    private readonly PubSubWebSocketConnection _connection;

    private readonly ILogger<PubSubTunnel> _logger;
    private readonly PubSubTunnelMessageReceiver _messageReceiver;
    private readonly PubSubTunnelMessageSender _messageSender;

    private int _attempts;

    private CancellationTokenSource _cancellationTokenSource = new();

    private Task _listenerTask = Task.CompletedTask;
    private PubSubTunnelMessageHandler? _messageHandler;

    public PubSubTunnel(ILogger<PubSubTunnel> logger, PubSubWebSocketConnection connection,
        PubSubTunnelMessageReceiver messageReceiver, PubSubTunnelMessageSender messageSender,
        BridgeConfiguration configuration)
    {
        _logger = logger;
        _connection = connection;
        _messageReceiver = messageReceiver;
        _messageSender = messageSender;
        _configuration = configuration;
    }

    public string? ClientId { get; private set; }

    public bool DataAvailable => _messageReceiver!.DataAvailable;

    public bool IsDisposed { get; private set; }

    public string? ServerId { get; private set; }

    public string? SocketId { get; private set; }

    public bool Started { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        if (!_cancellationTokenSource.IsCancellationRequested)
            _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        await _connection.DisposeAsync();
        _messageReceiver.Dispose();
    }

    public async Task<ReceivedMessage> ReceiveAsync(string from, CancellationToken cancellationToken)
    {
        using var cancel =
            CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken);
        while (!cancel.IsCancellationRequested)
        {
            await WaitWebSocketConnectedAsync(cancellationToken);

            return await _messageReceiver.ReceiveMessagesAsync(from, cancel.Token);
        }

        throw new OperationCanceledException(cancel.Token);
    }

    public async ValueTask<BridgeConnect> ReceiveTunnelConnectAsync(CancellationToken cancellationToken)
    {
        using var cancel =
            CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken);
        while (!cancel.IsCancellationRequested)
        {
            await WaitWebSocketConnectedAsync(cancellationToken);

            return await _messageReceiver.ReceiveConnectAsync(cancel.Token);
        }

        throw new OperationCanceledException(cancel.Token);
    }

    public async Task<int> SendAsync(string address, ReadOnlyMemory<byte> message, bool connect, long messageId,
        int sequenceNumber, int totalSize, CancellationToken cancellationToken)
    {
        MessageSentStatus? status = null;
        try
        {
            using var cancel =
                CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken);
            for (var i = 0; i < 2 && !cancel.IsCancellationRequested; i++)
            {
                await WaitWebSocketConnectedAsync(cancel.Token);

                status = await _messageSender!.SendMessageAsync(_connection.Client!, message, connect,
                    address, messageId, sequenceNumber, totalSize, status, cancel.Token);

                if (status.Succeeded)
                    return status.AmountConsumed;

                _logger.LogWarning(status.Error, "Failed sending message, trying again");
                await Task.Delay(DefaultRetrySendDelayMilliSeconds, cancel.Token);
            }

            throw new OperationCanceledException(cancel.Token);
        }
        catch
        {
            _messageSender!.CleanupMessage(status?.AckId);
            throw;
        }
    }

    public async ValueTask SendTunnelConnectAsync(string to, TunnelConnect tunnelConnect,
        CancellationToken cancellationToken)
    {
        using var buffer = MemoryPool<byte>.Shared.Rent(_configuration.PacketSize);
        var written = await _messageSender!.SerializeAsync(buffer.Memory, tunnelConnect, cancellationToken);

        await SendAsync(to, buffer.Memory.Slice(0, written), true, 1, 1, written, cancellationToken);
    }

    public async ValueTask SendTunnelDisconnectAsync(string to, CancellationToken cancellationToken)
    {
        TunnelConnect tunnelConnect = new()
        {
            Disconnect = true,
            ClientId = ClientId,
            ServerId = ServerId
        };

        using var buffer = MemoryPool<byte>.Shared.Rent(_configuration.PacketSize);
        var written = await _messageSender!.SerializeAsync(buffer.Memory, tunnelConnect, cancellationToken);

        await SendAsync(to, buffer.Memory.Slice(0, written), true, 1, 1, written, cancellationToken);
    }

    public async Task StartAsync(string serverId, string? socketId, string? clientId,
        CancellationToken cancellationToken)
    {
        ServerId = serverId;
        SocketId = socketId ?? Guid.NewGuid().ToString("N");
        ClientId = clientId ?? Guid.NewGuid().ToString("N");
        _messageReceiver.Initialize(ServerId, ClientId);
        _messageSender.Initialize(ServerId, ClientId);
        _cancellationTokenSource = new CancellationTokenSource();
        var cancelToken = _cancellationTokenSource.Token;
        _listenerTask = Task.Run(async () =>
        {
            try
            {
                await StartListenerAsync(cancelToken);
            }
            finally
            {
                Started = false;
                if (!_cancellationTokenSource.IsCancellationRequested)
                    _cancellationTokenSource.Cancel();
            }
        }, cancellationToken);

        var available = await _connection.WaitConnectedAsync(cancelToken);
        if (!available) await _listenerTask;
        Started = available;
    }

    public async ValueTask<bool> TunnelConnectAsync(BridgeConnect bridgeConnect, bool waitForResponse,
        CancellationToken cancellationToken)
    {
        bridgeConnect.Connect.ClientId = ClientId;
        bridgeConnect.Connect.ServerId = ServerId;

        await SendTunnelConnectAsync(bridgeConnect.Address, bridgeConnect.Connect, cancellationToken);

        if (!waitForResponse) return true;

        var response = await _messageReceiver!.ReceiveConnectAsync(cancellationToken);
        return response.Connect.Connected;
    }

    public async ValueTask TunnelDisconnectAsync(string address, CancellationToken cancellationToken)
    {
        await SendTunnelDisconnectAsync(address, cancellationToken);
    }

    public ValueTask<bool> WaitConnectAvailableAsync(CancellationToken cancellationToken)
    {
        return _messageReceiver!.WaitConnectAsync(cancellationToken);
    }

    public ValueTask<bool> WaitDataAvailableAsync(string from, CancellationToken cancellationToken)
    {
        return _messageReceiver!.WaitAsync(from, cancellationToken);
    }

    private async Task<bool> BackoffAsync(CancellationToken cancellationToken)
    {
        if (_configuration.StopOnError)
            return false;

        if (_attempts >= DefaultMaxConnectionAttempts)
            return false;

        await Task.Delay(ExponentialBackoff(), cancellationToken);
        _attempts += 1;
        return true;
    }

    private TimeSpan ExponentialBackoff()
    {
        var random = new Random();
        var amount = Math.Pow(2, _attempts) * DefaultRetryConnectionDelayMilliSeconds
                     + random.NextDouble() * DefaultRetryConnectionDelayMilliSeconds;
        return TimeSpan.FromMilliseconds(Math.Min(amount, DefaultMaxDelayMilliSeconds));
    }

    private async Task<PubSubMessage?> ReceiveAsync(ClientWebSocket connection, CancellationToken cancellationToken)
    {
        var stream = new WebSocketStream(connection, cancellationToken, _logger.IsEnabled(LogLevel.Trace));
        var message = await JsonSerializer.DeserializeAsync<PubSubMessage>(stream, DefaultJsonSerializerOptions,
            cancellationToken);
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("[[{Name}]] <- Receiving: {message}", "WebSocket", stream.Capture);
        return message;
    }

    private async Task Reconnect(CancellationToken cancellationToken)
    {
        await _connection.Reconnect(SocketId!, ServerId!, _messageSender!, _messageReceiver!, cancellationToken);
        _messageHandler = new PubSubTunnelMessageHandler(_logger, _connection.Client!,
            _connection.UpdateConnection, _connection.OnConnected, _messageSender!, _messageReceiver!);
    }

    private async Task StartListenerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
            try
            {
                // Attempt a reconnect if the connection is not open.
                if (_connection.Client is not { State: WebSocketState.Open }) await Reconnect(cancellationToken);

                // Wait for a message.
                var message = await ReceiveAsync(_connection.Client!, cancellationToken);
                if (message != null) await _messageHandler!.HandleMessage(message, cancellationToken);
                UpdateBackoff();
            }
            catch (Exception e)
            {
                // Handle cancellation from possible aggregate exception
                cancellationToken.ThrowIfCancellationRequested();

                var exceptions = new ReadOnlyCollection<Exception>(new[] { e });
                if (e is AggregateException aggregateException)
                    exceptions = aggregateException.Flatten().InnerExceptions;

                foreach (var ex in exceptions)
                    switch (ex)
                    {
                        case OperationCanceledException:
                            throw ex;
                        case RequestFailedException:
                            _logger.LogError(ex, "Failed connecting to Azure service, message: {Message}", ex.Message);
                            throw;
                        case HandleServerJoinMessageException:
                            _logger.LogError(ex, "Failed joining server group, Message: {Message}", ex.Message);
                            throw;
                        case HandleDisconnectedMessageException:
                            _logger.LogWarning(ex, "Disconnected from server group, reconnecting, Message: {Message}",
                                ex.Message);
                            await _connection.CloseAsync(ex.Message, cancellationToken);
                            break;
                        case HandleMessageException hme:
                            _logger.LogError(hme, "Failed handling message, Type: {MessageType}, Message: {Message}",
                                hme.HandledMessage.Type, ex.Message);
                            break;
                        case WebSocketException:
                            _logger.LogError(ex, "Failed connecting to websocket, Message: {Message}", ex.Message);
                            throw;
                        default:
                            _logger.LogError(ex, "Tunnel failed listening for messages, Message: {Message}",
                                ex.Message);
                            break;
                    }

                if (await BackoffAsync(cancellationToken))
                    continue;
                throw;
            }
    }

    private void UpdateBackoff()
    {
        _attempts = 0;
    }

    private async ValueTask WaitWebSocketConnectedAsync(CancellationToken cancellationToken)
    {
        if (!await _connection.WaitConnectedAsync(cancellationToken))
        {
            await _listenerTask;
            throw new PubSubWebSocketStoppedException();
        }
    }
}