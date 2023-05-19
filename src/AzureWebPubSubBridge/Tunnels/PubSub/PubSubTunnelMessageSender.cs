using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using AzureWebPubSubBridge.Formats;
using AzureWebPubSubBridge.Messages;
using AzureWebPubSubBridge.Utilities;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels.PubSub;

public class PubSubTunnelMessageSender : IDisposable
{
    private static long _currentAckId = 1;
    private readonly int _dataSize;
    private readonly ILogger<PubSubTunnelMessageSender> _logger;
    private readonly ConcurrentDictionary<long, (IMemoryOwner<byte> buffer, int length)> _messages = new();
    private readonly int _packetSize;
    private string? _clientId;
    private string? _serverId;
    private long _serverJoinAckId;

    public PubSubTunnelMessageSender(ILogger<PubSubTunnelMessageSender> logger, BridgeConfiguration configuration)
    {
        _logger = logger;
        _packetSize = configuration.PacketSize;
        _dataSize = _packetSize - 256;
    }

    public long ServerJoinAckId => _serverJoinAckId;

    public void Dispose()
    {
        foreach (var (_, packet) in _messages.ToArray()) packet.buffer.Dispose();
        _messages.Clear();
    }

    public void CleanupMessage(long? ackId)
    {
        if (ackId == null) return;

        if (_messages.TryRemove(ackId.Value, out var packet)) packet.buffer.Dispose();
    }

    public void Initialize(string serverId, string clientId)
    {
        _serverId = serverId;
        _clientId = clientId;
    }

    public async ValueTask<MessageSentStatus> ResendAllMessagesAsync(ClientWebSocket connection,
        CancellationToken cancellationToken)
    {
        foreach (var ackId in _messages.Keys.ToArray())
        {
            var status = await ResendMessageAsync(connection, ackId, cancellationToken);
            if (!status.Succeeded) return status;
        }

        return new MessageSentStatus(true);
    }

    public ValueTask<MessageSentStatus> ResendMessageAsync(ClientWebSocket connection, long ackId,
        CancellationToken cancellationToken)
    {
        if (_messages.TryGetValue(ackId, out var packet))
        {
            _logger.LogTrace("[[{Name}]] -> Resending packet, Ack ID: {AckId}, Packet Length: {Length}", "WebSocket",
                ackId, packet.length);
            return SendAsync(connection, ackId, packet.buffer.Memory.Slice(0, packet.length), cancellationToken);
        }

        return ValueTask.FromResult(new MessageSentStatus(true) { AckId = ackId });
    }

    public async ValueTask<MessageSentStatus> SendJoinServerGroupMessageAsync(ClientWebSocket connection,
        CancellationToken cancellationToken)
    {
        PubSubMessage message = new()
        {
            Type = "joinGroup",
            Group = _serverId,
            AckId = Interlocked.Increment(ref _currentAckId)
        };
        var currServerAckId = _serverJoinAckId;
        if (Interlocked.CompareExchange(ref _serverJoinAckId, message.AckId!.Value, currServerAckId) != currServerAckId)
        {
            _logger.LogInformation("Already joining server group, Server Join Ack ID: {ServerJoinAckId}",
                _serverJoinAckId);
            return new MessageSentStatus(true) { AckId = _serverJoinAckId, Duplicated = true };
        }

        var status = await SendAsync(connection, message, cancellationToken);
        if (status.Succeeded)
            return status;

        if (_messages.TryRemove(status.AckId, out var packet)) packet.buffer.Dispose();

        return status;
    }

    public async ValueTask<MessageSentStatus> SendMessageAsync(ClientWebSocket connection, ReadOnlyMemory<byte> message,
        bool connect, string destination, long messageId, int sequenceNumber, int totalSize,
        MessageSentStatus? previousStatus, CancellationToken cancellationToken)
    {
        if (previousStatus != null)
            return await ResendMessageAsync(connection, previousStatus.AckId, cancellationToken) with
            {
                AmountConsumed = previousStatus.AmountConsumed
            };

        if (string.IsNullOrEmpty(_clientId))
            throw new InvalidOperationException("Sending a message before connecting");

        using var data = MemoryPool<byte>.Shared.Rent(_dataSize);

        PubSubDataFormat.EncodeToFormat(data.Memory.Span.Slice(0, _dataSize), message.Span, destination, _clientId,
            messageId,
            sequenceNumber, totalSize, out var consumed, out var written);
        PubSubDataFormat.SetIsConnect(data.Memory.Span.Slice(0, _dataSize), connect);

        var envelope = new PubSubMessage
        {
            Type = "sendToGroup",
            Group = _serverId,
            Data = data,
            NoEcho = true
        };

        return await SendAsync(connection, envelope, cancellationToken) with { AmountConsumed = consumed };
    }

    public async ValueTask<MessageSentStatus> SendSequenceAckMessageAsync(ClientWebSocket connection, long sequenceId,
        CancellationToken cancellationToken)
    {
        PubSubMessage message = new()
        {
            Type = "sequenceAck",
            SequenceId = sequenceId
        };

        var packet = MemoryPool<byte>.Shared.Rent(_packetSize);
        int committed;
        try
        {
            committed = await SerializeAsync(packet.Memory, message, cancellationToken);
        }
        catch (Exception e)
        {
            packet.Dispose();
            return new MessageSentStatus(false) { Error = e };
        }

        return await SendAsync(connection, -1, packet.Memory.Slice(0, committed), cancellationToken);
    }

    public async ValueTask<int> SerializeAsync<TMessage>(Memory<byte> packet, TMessage message,
        CancellationToken cancellationToken)
    {
        await using var jsonWriter = new Utf8JsonWriter(new MemoryPoolBufferWriter(packet));
        JsonSerializer.Serialize(jsonWriter, message, PubSubTunnel.DefaultJsonSerializerOptions);
        await jsonWriter.FlushAsync(cancellationToken);

        return (int)jsonWriter.BytesCommitted;
    }

    private void CaptureMessage<TMessage>(TMessage message)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("[[{Name}]] -> Sending: {Message}", "WebSocket",
                JsonSerializer.Serialize(message, PubSubTunnel.DefaultJsonSerializerOptions));
    }

    private async ValueTask<MessageSentStatus> SendAsync(ClientWebSocket connection, PubSubMessage envelope,
        CancellationToken cancellationToken)
    {
        var ackId = envelope.AckId ?? Interlocked.Increment(ref _currentAckId);
        var packet = MemoryPool<byte>.Shared.Rent(_packetSize);
        int committed;

        try
        {
            envelope.AckId = ackId;

            CaptureMessage(envelope);

            committed = await SerializeAsync(packet.Memory, envelope, cancellationToken);
            _messages.TryAdd(ackId, (packet, committed));
        }
        catch
        {
            packet.Dispose();
            throw;
        }

        return await SendAsync(connection, ackId, packet.Memory.Slice(0, committed), cancellationToken);
    }

    private async ValueTask<MessageSentStatus> SendAsync(ClientWebSocket connection, long ackId,
        ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(packet, WebSocketMessageType.Text, true, cancellationToken);

            return new MessageSentStatus(true) { AckId = ackId };
        }
        catch (Exception e)
        {
            return new MessageSentStatus(false) { AckId = ackId, Error = e };
        }
    }
}