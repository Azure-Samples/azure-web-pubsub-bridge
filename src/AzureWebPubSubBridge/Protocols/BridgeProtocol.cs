using System.Net.Sockets;
using AzureWebPubSubBridge.Messages;

namespace AzureWebPubSubBridge.Protocols;

public record BridgeConnect(string Address, TunnelConnect Connect);

public interface IBridgeProtocol
{
    ValueTask<BridgeConnect?> ConnectAsync(NetworkStream stream, CancellationToken cancellationToken = new());
}