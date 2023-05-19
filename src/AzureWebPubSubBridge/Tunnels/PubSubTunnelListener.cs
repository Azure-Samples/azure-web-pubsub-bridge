using System.Collections.Concurrent;
using AzureWebPubSubBridge.Messages;
using AzureWebPubSubBridge.Protocols;
using AzureWebPubSubBridge.Tunnels.PubSub;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels;

public record PubSubTunnelConnection(PubSubTunnelStream Stream, TunnelConnect Connect);

public class PubSubTunnelListener
{
    private readonly ConcurrentDictionary<string, PubSubTunnelConnection>
        _connections = new();

    private readonly ILogger<PubSubTunnelListener> _logger;
    private readonly IPubSubTunnelFactory _tunnelFactory;
    private string? _serverId;
    private PubSubTunnel? _tunnel;

    public PubSubTunnelListener(ILogger<PubSubTunnelListener> logger, IPubSubTunnelFactory tunnelFactory)
    {
        _logger = logger;
        _tunnelFactory = tunnelFactory;
    }

    public async ValueTask<PubSubTunnelConnection> AcceptTunnelConnectionAsync(
        CancellationToken cancellationToken = new())
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var connection = await _tunnel!.ReceiveTunnelConnectAsync(cancellationToken);

            if (connection.Connect.Disconnect)
            {
                await DisconnectStream(connection);
                continue;
            }

            // For now immediately indicate connected. May want to attempt a TCP connection first but
            // may timeout on SSL handshake.
            connection.Connect.Connected = true;
            await _tunnel.TunnelConnectAsync(connection, false, cancellationToken);
            PubSubTunnelStream stream = new(_logger, _tunnel, connection.Address, false, true);
            PubSubTunnelConnection result = new(stream, connection.Connect);
            if (!_connections.TryAdd(connection.Address, result))
                continue;

            return result;
        }

        throw new OperationCanceledException(cancellationToken);
    }

    public async ValueTask StartAsync(string serverId, CancellationToken cancellationToken = new())
    {
        // Already started.
        if (_serverId == serverId)
            return;

        // Can only start one listener per serverId.
        if (_serverId != null && _serverId != serverId)
            throw new InvalidOperationException(
                "Attempting to start the listener more than once for different Server ID's");

        _serverId = serverId;
        _tunnel = _tunnelFactory.Create();
        await _tunnel.StartAsync(serverId, null, serverId, cancellationToken);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = new())
    {
        if (_tunnel == null)
            return;

        foreach (var connection in _connections.ToList()) await connection.Value.Stream.DisposeAsync();

        _connections.Clear();
        await _tunnel.DisposeAsync();

        _serverId = null;
        _tunnel = null;
    }

    private async ValueTask DisconnectStream(BridgeConnect connect)
    {
        if (_connections.TryRemove(connect.Address, out var connectedStream))
            await connectedStream.Stream.DisposeAsync();
    }
}