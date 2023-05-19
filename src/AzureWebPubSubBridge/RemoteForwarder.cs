using System.Net;
using System.Net.Sockets;
using AzureWebPubSubBridge.Messages;
using AzureWebPubSubBridge.Tunnels;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge;

public class RemoteForwarder : Forwarder
{
    private readonly PubSubTunnelListener _listener;
    private readonly ILogger<RemoteForwarder> _logger;

    public RemoteForwarder(ILogger<RemoteForwarder> logger, BridgeConfiguration configuration,
        PubSubTunnelListener listener)
        : base(logger, configuration)
    {
        _listener = listener;
        _logger = logger;
    }

    protected override async Task<Task> AcceptAndBridgeTunnelAsync(CancellationToken cancellationToken)
    {
        var (tunnelStream, connect) = await _listener.AcceptTunnelConnectionAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Connection accepted, Connect: {@Connect}", connect);

        // NOTE: Missing timeout on connection established.

        return Task.Run(async () =>
        {
            try
            {
                var remoteClient = await ConnectRemoteTcpClientAsync(connect, cancellationToken);
                try
                {
                    var remoteStream = remoteClient.GetStream();
                    _logger.LogInformation("Connection started, Connect: {@Connect}", connect);
                    await Task.WhenAny(
                        Task.Run(() => TransferAsync(remoteClient, tunnelStream, remoteStream, cancellationToken),
                            cancellationToken),
                        Task.Run(() => TransferAsync(remoteClient, remoteStream, tunnelStream, cancellationToken),
                            cancellationToken));
                }
                finally
                {
                    remoteClient.Close();
                }
            }
            catch (OperationCanceledException e)
            {
                _logger.LogInformation(e, "Connection canceled, Connect: {@Connect}", connect);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed, Connect: {@Connect}, Message: {Message}", connect, e.Message);
            }
            finally
            {
                await tunnelStream.DisposeAsync();
                _logger.LogInformation("Connection closed, Connect: {@Connect}", connect);
            }
        }, cancellationToken);
    }

    protected override async ValueTask SetupAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Remote Forwarder...");
        _logger.LogInformation(
            "Listening, PubSub Endpoint: {PubSubEndpoint}, Hub: {Hub}, Server ID: {ServerId}",
            Configuration.PubSubEndpoint, Configuration.Hub, Configuration.Connect.ServerId);

        await _listener.StartAsync(Configuration.Connect.ServerId, cancellationToken);
    }

    protected override async ValueTask TeardownAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Remote Forwarder..");
        await _listener.StopAsync(CancellationToken.None);
    }

    private async ValueTask<TcpClient> ConnectRemoteTcpClientAsync(TunnelConnect tunnelConnect,
        CancellationToken cancellationToken)
    {
        TcpClient remoteClient = new();

        if (tunnelConnect.DomainName != null)
        {
            var hostAddress = await Dns.GetHostAddressesAsync(tunnelConnect.DomainName, cancellationToken);
            if (hostAddress.Length == 0)
                throw new ArgumentException(
                    $"Domain name specified but does not exist, Domain: {tunnelConnect.DomainName}",
                    nameof(tunnelConnect));

            _logger.LogInformation("Connecting, Host Addresses: {@HostAddresses}, Port: {Port}", hostAddress,
                tunnelConnect.Port);
            await remoteClient.ConnectAsync(hostAddress, tunnelConnect.Port, cancellationToken);
            return remoteClient;
        }

        if (tunnelConnect.IpAddress != null)
        {
            _logger.LogInformation("Connecting, IP Address: {@IPAddress}, Port: {Port}", tunnelConnect.IpAddress,
                tunnelConnect.Port);
            await remoteClient.ConnectAsync(tunnelConnect.IpAddress, tunnelConnect.Port, cancellationToken);
            return remoteClient;
        }

        throw new ArgumentException("Connect message was not in a valid format, missing IP or Domain",
            nameof(tunnelConnect));
    }
}