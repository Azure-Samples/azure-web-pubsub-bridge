using System.Net.WebSockets;
using AzureWebPubSubBridge.Exceptions;
using AzureWebPubSubBridge.Messages;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels.PubSub;

public class PubSubTunnelMessageHandler
{
    private readonly ClientWebSocket _connection;
    private readonly ILogger _logger;
    private readonly PubSubTunnelMessageReceiver _messageReceiver;

    private readonly Action<bool> _onConnected;
    private readonly PubSubTunnelMessageSender _pubSubTunnelMessageSender;
    private readonly Action<IConnectedSystemPubSubMessage> _updateConnection;
    private IConnectedSystemPubSubMessage? _connectedMessage;

    public PubSubTunnelMessageHandler(ILogger logger, ClientWebSocket connection,
        Action<IConnectedSystemPubSubMessage> updateConnection,
        Action<bool> onConnected,
        PubSubTunnelMessageSender pubSubTunnelMessageSender,
        PubSubTunnelMessageReceiver messageReceiver)
    {
        _logger = logger;
        _connection = connection;
        _onConnected = onConnected;
        _updateConnection = updateConnection;
        _pubSubTunnelMessageSender = pubSubTunnelMessageSender;
        _messageReceiver = messageReceiver;
    }

    public Task<bool> HandleMessage(PubSubMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return message switch
            {
                { Type: "system", Event: "connected" } and IConnectedSystemPubSubMessage m => HandleConnectedMessage(m,
                    cancellationToken),
                { Type: "system", Event: "disconnected" } and IDisconnectedSystemPubSubMessage m =>
                    HandleDisconnectedMessage(m, cancellationToken),
                { Type: "ack" } and IAckPubSubMessage m => HandleAckMessage(m, cancellationToken),
                { Type: "message" } and IReceivePubSubMessage m => HandleReceiveMessage(m, cancellationToken),
                _ => UnknownMessage(message)
            };
        }
        catch (HandleMessageException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new HandleMessageException($"Failed handling message, Message: {e.Message}", message, e);
            throw;
        }
    }

    private async Task<bool> HandleAckMessage(IAckPubSubMessage message, CancellationToken cancellationToken)
    {
        if (message.AckId == null)
            throw new HandleMessageException("Ack ID is missing from Ack Message", message);

        var ackId = message.AckId!.Value;

        if (message.Success == true || message.Error?["name"] == "Duplicate")
        {
            _pubSubTunnelMessageSender.CleanupMessage(ackId);
            if (ackId == _pubSubTunnelMessageSender.ServerJoinAckId) await SignalConnected(cancellationToken);
            return true;
        }

        if (message.Error?["name"] == "Forbidden")
        {
            if (ackId == _pubSubTunnelMessageSender.ServerJoinAckId)
                throw new HandleServerJoinMessageException(
                    $"Forbidden to join server group, Message: {message.Error?["message"]}", message);
            throw new HandleMessageException($"Forbidden to complete action, Message: {message.Error?["message"]}",
                message);
        }

        var status = await _pubSubTunnelMessageSender.ResendMessageAsync(_connection, ackId, cancellationToken);
        if (!status.Succeeded) throw status.Error!;

        return true;
    }

    private async Task<bool> HandleConnectedMessage(IConnectedSystemPubSubMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _connectedMessage, message);
        _updateConnection(message);

        var status =
            await _pubSubTunnelMessageSender.SendJoinServerGroupMessageAsync(_connection, cancellationToken);
        if (!status.Succeeded) throw status.Error!;
        return true;
    }

    private Task<bool> HandleDisconnectedMessage(IDisconnectedSystemPubSubMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new HandleDisconnectedMessageException(message.Message ?? "Disconnected", message);
    }

    private async Task<bool> HandleReceiveMessage(IReceivePubSubMessage pubSubMessage,
        CancellationToken cancellationToken)
    {
        var receivedMessage = pubSubMessage.Data;
        if (receivedMessage != null)
            _messageReceiver.ReceiveMessage(receivedMessage);

        if (pubSubMessage.SequenceId != null)
        {
            var status = await _pubSubTunnelMessageSender.SendSequenceAckMessageAsync(_connection,
                pubSubMessage.SequenceId!.Value,
                cancellationToken);
            if (!status.Succeeded) throw status.Error!;
        }

        return true;
    }

    private async Task SignalConnected(CancellationToken cancellationToken)
    {
        var status = await _pubSubTunnelMessageSender.ResendAllMessagesAsync(_connection, cancellationToken);
        if (!status.Succeeded) throw status.Error!;
        _onConnected(true);
    }

    private Task<bool> UnknownMessage(PubSubMessage message)
    {
        throw new HandleMessageException($"Unknown message: {message.Type}", message);
    }
}