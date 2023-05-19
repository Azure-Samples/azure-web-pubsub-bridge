using System.Net.Sockets;
using AzureWebPubSubBridge.Messages;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Protocols;

public class DirectProtocol : IBridgeProtocol
{
    private readonly TunnelConnect _connect;
    private readonly ILogger<DirectProtocol> _logger;
    private readonly string _serverId;

    public DirectProtocol(ILogger<DirectProtocol> logger, BridgeConfiguration configuration)
    {
        _logger = logger;
        if (configuration.Connect == null)
            throw new InvalidOperationException($"Connect information request for {nameof(DirectProtocol)}");
        _connect = new TunnelConnect
        {
            IpAddress = configuration.Connect.IpAddress,
            DomainName = configuration.Connect.DomainName,
            Port = configuration.Connect.Port
        };
        _serverId = configuration.Connect.ServerId;
        _logger.LogInformation("Utilizing a Direct Protocol connection, Server ID: {ServerId}, Connect: {@Connect}",
            _serverId, _connect);
    }

    public ValueTask<BridgeConnect?> ConnectAsync(NetworkStream stream,
        CancellationToken cancellationToken = new())
    {
        return ValueTask.FromResult(new BridgeConnect(_serverId, _connect))!;
    }
}