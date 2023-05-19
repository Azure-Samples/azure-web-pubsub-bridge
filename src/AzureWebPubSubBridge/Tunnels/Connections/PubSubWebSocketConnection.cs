using System.Net.WebSockets;
using Azure.Messaging.WebPubSub;
using AzureWebPubSubBridge.Messages;
using AzureWebPubSubBridge.Tunnels.PubSub;
using AzureWebPubSubBridge.Utilities;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels.Connections;

public class PubSubWebSocketConnection : IAsyncDisposable
{
    public static readonly TimeSpan DefaultSasUriTimeout = TimeSpan.FromHours(1);

    private readonly AsyncManualResetEvent<bool> _connected = new();
    private readonly ILogger<PubSubWebSocketConnection> _logger;
    private readonly WebPubSubServiceClient _serviceClient;
    private string? _connectionId;
    private string? _reconnectionToken;

    private Uri? _sasUri;
    private DateTimeOffset _sasUriUpdatedTimeOffset = DateTimeOffset.MinValue;

    public PubSubWebSocketConnection(ILogger<PubSubWebSocketConnection> logger, WebPubSubServiceClient serviceClient)
    {
        _logger = logger;
        _serviceClient = serviceClient;
    }

    public ClientWebSocket? Client { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("Disposing connection", CancellationToken.None);

        if (Client != null)
        {
            Client.Dispose();
            Client = null;
        }
    }

    public async ValueTask CloseAsync(string statusMessage, CancellationToken cancellationToken)
    {
        try
        {
            if (Client is not { State: WebSocketState.Open or WebSocketState.Connecting })
                return;

            await Client.CloseAsync(WebSocketCloseStatus.NormalClosure, statusMessage, cancellationToken);
        }
        catch
        {
            /* ignored */
        }

        _connectionId = null;
        _reconnectionToken = null;

        try
        {
            _connected.Reset();
            _connected.Set(false);
        }
        catch
        {
            /* ignored */
        }
    }

    public void OnConnected(bool connected)
    {
        if (connected)
        {
            _connected.Set(true);
            return;
        }

        _connected.Reset();
    }

    public async ValueTask<PubSubTunnelMessageHandler> Reconnect(string socketId, string serverId,
        PubSubTunnelMessageSender messageSender, PubSubTunnelMessageReceiver messageReceiver,
        CancellationToken cancellationToken)
    {
        await RefreshSasUriAsync(socketId, serverId, cancellationToken);

        Client = new ClientWebSocket();
        await ConnectAsync(Client, _sasUri!, _connectionId, _reconnectionToken, cancellationToken);

        return new PubSubTunnelMessageHandler(_logger, Client,
            UpdateConnection, OnConnected, messageSender, messageReceiver);
    }

    public void UpdateConnection(IConnectedSystemPubSubMessage connected)
    {
        if (connected.ConnectionId != null)
            _connectionId = connected.ConnectionId;
        if (connected.ReconnectionToken != null)
            _reconnectionToken = connected.ReconnectionToken;
    }

    public Task<bool> WaitConnectedAsync(CancellationToken cancellationToken)
    {
        return _connected.WaitAsync(cancellationToken);
    }

    protected virtual async ValueTask ConnectAsync(ClientWebSocket connection, Uri sasUri, string? connectionId,
        string? reconnectionToken, CancellationToken cancellationToken)
    {
        var uriBuilder = new UriBuilder(sasUri);
        if (connectionId != null && reconnectionToken != null)
        {
            var query = $"awps_connection_id={connectionId}";
            uriBuilder.Query = string.IsNullOrEmpty(uriBuilder.Query) ? query : $"{uriBuilder.Query}&{query}";
        }

        connection.Options.AddSubProtocol("json.webpubsub.azure.v1");
        await connection.ConnectAsync(uriBuilder.Uri, cancellationToken);
    }

    private async Task RefreshSasUriAsync(string socketId, string serverId, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow.Ticks < (_sasUriUpdatedTimeOffset.Ticks + DefaultSasUriTimeout.Ticks) / 2)
            return;

        _sasUri = await _serviceClient.GetClientAccessUriAsync(DefaultSasUriTimeout * 2, socketId,
            new[] { $"webpubsub.joinLeaveGroup.{serverId}", $"webpubsub.sendToGroup.{serverId}" },
            cancellationToken);
        _sasUriUpdatedTimeOffset = DateTimeOffset.UtcNow;
    }
}