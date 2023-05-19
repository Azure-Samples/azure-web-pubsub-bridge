using AzureWebPubSubBridge.Protocols;
using AzureWebPubSubBridge.Tunnels.PubSub;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels;

public class PubSubTunnelClient
{
    private readonly ILogger<PubSubTunnelClient> _logger;
    private readonly IPubSubTunnelFactory _tunnelFactory;

    public PubSubTunnelClient(ILogger<PubSubTunnelClient> logger, IPubSubTunnelFactory tunnelFactory)
    {
        _logger = logger;
        _tunnelFactory = tunnelFactory;
    }

    public async ValueTask<PubSubTunnelStream> ConnectAsync(BridgeConnect bridge,
        string? socketId = null, string? clientId = null, CancellationToken cancellationToken = new())
    {
        var tunnel = _tunnelFactory.Create();
        await tunnel.StartAsync(bridge.Address, socketId, clientId, cancellationToken);
        if (!tunnel.Started)
            throw new ArgumentException("Unable to connect to server with connection request", nameof(bridge));

        await tunnel.TunnelConnectAsync(bridge, true, cancellationToken);

        return new PubSubTunnelStream(_logger, tunnel, true, false);
    }
}