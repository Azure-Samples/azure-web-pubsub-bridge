using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge;

public abstract class Forwarder : BackgroundService
{
    private readonly HashSet<Task> _bridges = new();
    private readonly ILogger<Forwarder> _logger;

    protected Forwarder(ILogger<Forwarder> logger, BridgeConfiguration configuration)
    {
        Configuration = configuration;
        _logger = logger;
    }

    public TaskCompletionSource<bool> Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected BridgeConfiguration Configuration { get; }

    protected abstract Task<Task> AcceptAndBridgeTunnelAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SetupAsync(cancellationToken);

            try
            {
                await AcceptTunnelConnectionsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                /* Ignored, shutting down... */
            }
            catch (Exception e)
            {
                _logger.LogError(e, "{Forwarder} Stopped: {Message}", GetType().Name, e.Message);
            }
            finally
            {
                await TeardownAsync(cancellationToken);
            }
        }
        catch (Exception e)
        {
            Started.TrySetException(e);
        }
        finally
        {
            Started.TrySetResult(false);
        }
    }

    protected virtual ValueTask SetupAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    protected virtual ValueTask TeardownAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    protected async Task TransferAsync(TcpClient client, Stream from, Stream to, CancellationToken cancellationToken)
    {
        using var buffer = MemoryPool<byte>.Shared.Rent(Configuration.PacketSize);
        while (client.Connected && !cancellationToken.IsCancellationRequested)
            try
            {
                var read = await from.ReadAsync(buffer.Memory, cancellationToken);

                if (read <= 0)
                    // Remote has shutdown its connection.
                    return;

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("[[{Name}]] => {From}->{To} Sending: {Buffer}", from.GetType().Name,
                        to.GetType().Name, "Forwarder",
                        Encoding.UTF8.GetString(buffer.Memory.Slice(0, read).Span));

                await to.WriteAsync(buffer.Memory.Slice(0, read), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or SocketException)
            {
                _logger.LogWarning(e,
                    "IO Exception on transferring from TCP stream, could be stream closing, Message: {Message}",
                    e.Message);
                return;
            }
    }

    private async Task AcceptNewConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Accepting new connection...");
            _bridges.Add(await AcceptAndBridgeTunnelAsync(cancellationToken));
            _logger.LogInformation("New connection started...");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("New connection canceled...");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "New connection errored: {Message} ...", e.Message);
            if (Configuration.StopOnError)
                throw;
        }
    }

    private async Task AcceptTunnelConnectionsAsync(CancellationToken cancellationToken)
    {
        await Task.Run(async () =>
        {
            Started.TrySetResult(true);
            using var bridgeCancel =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                while (bridgeCancel.IsCancellationRequested == false)
                {
                    await ProcessRunningBridgeConnectionsAsync();

                    await AcceptNewConnectionAsync(bridgeCancel.Token);
                }
            }
            catch (OperationCanceledException)
            {
                /* Ignored, shutting down... */
            }
            catch (Exception e)
            {
                _logger.LogError("Stopped on exception, Message: {Message}", e.Message);
            }
            finally
            {
                bridgeCancel.Cancel();
                try
                {
                    await Task.WhenAll(_bridges);
                }
                catch
                {
                    /* Ignored, shutting down... */
                }
            }
        }, cancellationToken);
    }

    private async Task ProcessRunningBridgeConnectionsAsync()
    {
        _logger.LogInformation("Processing {BridgeCount} bridges...", _bridges.Count);
        foreach (var bridge in _bridges.Where(x => x.IsCompleted || x.IsCanceled).ToList())
        {
            _bridges.Remove(bridge);

            if (Configuration.StopOnError && bridge.IsFaulted) await bridge;
        }

        _logger.LogInformation("Finished processing bridges, remaining {BridgeCount} bridges...", _bridges.Count);
    }
}