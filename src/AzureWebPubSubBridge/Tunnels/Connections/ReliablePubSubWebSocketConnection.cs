using System.Net.WebSockets;
using Azure.Messaging.WebPubSub;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels.Connections;

public class ReliablePubSubWebSocketConnection : PubSubWebSocketConnection
{
    public ReliablePubSubWebSocketConnection(ILogger<ReliablePubSubWebSocketConnection> logger,
        WebPubSubServiceClient serviceClient) : base(logger, serviceClient)
    {
    }

    protected override async ValueTask ConnectAsync(ClientWebSocket connection, Uri sasUri, string? connectionId,
        string? reconnectionToken, CancellationToken cancellationToken)
    {
        var uriBuilder = new UriBuilder(sasUri);
        if (connectionId != null && reconnectionToken != null)
        {
            var query = $"awps_connection_id={connectionId}&awps_reconnection_token={reconnectionToken}";
            uriBuilder.Query = string.IsNullOrEmpty(uriBuilder.Query) ? query : $"{uriBuilder.Query}&{query}";
        }

        connection.Options.AddSubProtocol("json.reliable.webpubsub.azure.v1");
        await connection.ConnectAsync(uriBuilder.Uri, cancellationToken);
    }
}