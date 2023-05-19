using System.Net;
using System.Net.Sockets;
using AzureWebPubSubBridge.Protocols;
using AzureWebPubSubBridge.Tunnels;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge;

public class LocalForwarder : Forwarder
{
    private readonly PubSubTunnelClient _client;
    private readonly ILogger<LocalForwarder> _logger;
    private readonly IBridgeProtocol _protocol;
    private readonly TcpListener _server;

    public LocalForwarder(ILogger<LocalForwarder> logger, BridgeConfiguration configuration, IBridgeProtocol protocol,
        PubSubTunnelClient client)
        : base(logger, configuration)
    {
        _protocol = protocol;
        _client = client;
        _logger = logger;
        _server = new TcpListener(IPAddress.Any, configuration.Port);
    }

    protected override async Task<Task> AcceptAndBridgeTunnelAsync(CancellationToken cancellationToken)
    {
        var client = await _server.AcceptTcpClientAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Connection attempted, From: {From}, To: {To}",
            client.Client.RemoteEndPoint?.ToString(), client.Client.LocalEndPoint?.ToString());

        // NOTE: Missing timeout on connection established.

        var stream = client.GetStream();

        var bridge = await _protocol.ConnectAsync(stream, cancellationToken);
        if (bridge == null)
        {
            _logger.LogWarning("Failed establishing connection, From: {RemoteEndPoint}, To: {LocalEndPoint}",
                client.Client.RemoteEndPoint?.ToString(), client.Client.LocalEndPoint?.ToString());
            stream.Close();
            return Task.CompletedTask;
        }

        _logger.LogInformation("Connection accepted, Bridge: {@Bridge}", bridge);

        var tunnelStream = await _client.ConnectAsync(bridge, cancellationToken: cancellationToken);

        return Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation(
                    "Connection to tunnel started, Bridge: {@Bridge}", bridge);
                await Task.WhenAny(
                    Task.Run(() => TransferAsync(client, stream, tunnelStream, cancellationToken), cancellationToken),
                    Task.Run(() => TransferAsync(client, tunnelStream, stream, cancellationToken), cancellationToken));
            }
            catch (OperationCanceledException e)
            {
                _logger.LogInformation(e, "Connection canceled, Bridge: {@Bridge}", bridge);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed, Bridge: {@Bridge}, Message: {Message}", bridge, e.Message);
            }
            finally
            {
                await tunnelStream.DisposeAsync();
                client.Close();
                _logger.LogInformation("Connection closed, Bridge: {@Bridge}", bridge);
            }
        }, cancellationToken);
    }

    protected override ValueTask SetupAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Local Forwarder...");
        _logger.LogInformation("Listening, Port: {Port}", Configuration.Port);

        _server.Start();
        return ValueTask.CompletedTask;
    }

    protected override ValueTask TeardownAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Local Forwarder..");
        _server.Stop();
        return ValueTask.CompletedTask;
    }
}