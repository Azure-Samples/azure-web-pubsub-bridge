using System.Text;
using AzureWebPubSubBridge.Formats;
using AzureWebPubSubBridge.Messages;
using AzureWebPubSubBridge.Tunnels.PubSub;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels;

public class PubSubTunnelStream : Stream
{
    private static long _currentMessageId = 1;

    private readonly CancellationTokenSource _disconnectCancel;
    private readonly CancellationToken _disconnectCancelToken;
    private readonly Task _disconnectTask = Task.CompletedTask;
    private readonly ILogger _logger;
    private bool _disposing;
    private ReceivedMessage? _message;

    public PubSubTunnelStream(ILogger logger, PubSubTunnel tunnel, bool shouldHandleDisconnect, bool leaveOpen)
        : this(logger, tunnel, tunnel.ServerId!, shouldHandleDisconnect, leaveOpen)
    {
    }

    public PubSubTunnelStream(ILogger logger, PubSubTunnel tunnel, string address, bool shouldHandleDisconnect,
        bool leaveOpen)
    {
        _logger = logger;
        Address = PubSubDataFormat.FormatId(address);
        ServerId = tunnel.ServerId!;
        Tunnel = tunnel;
        ShouldHandleDisconnect = shouldHandleDisconnect;
        LeaveOpen = leaveOpen;
        _disconnectCancel = new CancellationTokenSource();
        _disconnectCancelToken = _disconnectCancel.Token;
        if (ShouldHandleDisconnect)
            _disconnectTask = MonitorForDisconnectAsync();
    }

    public string Address { get; }

    public override bool CanRead { get; } = true;

    public override bool CanSeek { get; } = false;

    public override bool CanWrite { get; } = true;

    public bool DataAvailable => (_message != null && _message.Segments.Count != 0) || Tunnel.DataAvailable;

    public bool LeaveOpen { get; set; }

    public override long Length { get; } = 0;

    public override long Position { get; set; } = 0;

    public string ServerId { get; }

    public bool ShouldHandleDisconnect { get; }

    public PubSubTunnel Tunnel { get; }

    public override async ValueTask DisposeAsync()
    {
        if (_disposing) return;
        _disposing = true;

        await SendDisconnectAsync();

        _disconnectCancel.Cancel();
        _disconnectCancel.Dispose();
        await _disconnectTask;
        if (!LeaveOpen)
            await Tunnel.DisposeAsync();

        _message?.Dispose();
        _message = null;

        await base.DisposeAsync();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = new())
    {
        if (_disposing)
            throw new ObjectDisposedException(nameof(PubSubTunnelStream));

        cancellationToken.ThrowIfCancellationRequested();
        _disconnectCancelToken.ThrowIfCancellationRequested();

        using var linkedCancel =
            CancellationTokenSource.CreateLinkedTokenSource(_disconnectCancelToken, cancellationToken);

        _message ??= await Tunnel.ReceiveAsync(Address, linkedCancel.Token);

        var remaining = buffer.Length;
        var written = 0;
        while (_message.Segments.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _disconnectCancelToken.ThrowIfCancellationRequested();

            var (key, segment) = _message.Segments.First();

            if (buffer.Length < segment.Length)
                throw new ArgumentException(
                    $"Buffer is too small to fill with data, Min Required Length: {segment.Length}, Current Buffer Length: {buffer.Length}",
                    nameof(buffer));
            var length = segment.Length;
            if (segment.Length > remaining) return written;

            segment.CopyTo(buffer.Slice(written).Span, 0, length);
            written += length;
            remaining -= length;
            _message.Segments.Remove(key);
            segment.Dispose();
        }

        _message = null;

        return written;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return 0;
    }

    public override void SetLength(long value)
    {
    }

    public async ValueTask<bool> WaitDataAvailableAsync(CancellationToken cancellationToken)
    {
        if (_disposing)
            throw new ObjectDisposedException(nameof(PubSubTunnelStream));

        cancellationToken.ThrowIfCancellationRequested();
        _disconnectCancelToken.ThrowIfCancellationRequested();
        if (DataAvailable)
            return true;

        using var linkedCancel =
            CancellationTokenSource.CreateLinkedTokenSource(_disconnectCancelToken, cancellationToken);
        return await Tunnel.WaitDataAvailableAsync(Address, linkedCancel.Token);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = new())
    {
        if (_disposing)
            throw new ObjectDisposedException(nameof(PubSubTunnelStream));

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("[[{Name}]] => Sending: {Buffer}", "Stream", Encoding.UTF8.GetString(buffer.Span));

        using var linkedCancel =
            CancellationTokenSource.CreateLinkedTokenSource(_disconnectCancelToken, cancellationToken);
        var linkedCancelToken = linkedCancel.Token;

        var sequenceNumber = 0;
        var totalSize = buffer.Length;
        var messageId = Interlocked.Increment(ref _currentMessageId);
        var written = 0;
        while (written < totalSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _disconnectCancelToken.ThrowIfCancellationRequested();
            var count = await Tunnel.SendAsync(Address, buffer.Slice(written), false, messageId,
                sequenceNumber, totalSize, linkedCancelToken);
            if (count <= 0)
                return;

            written += count;
            sequenceNumber += 1;
        }
    }

    private Task MonitorForDisconnectAsync()
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!_disposing && !_disconnectCancelToken.IsCancellationRequested)
                {
                    var connect = await Tunnel.ReceiveTunnelConnectAsync(_disconnectCancelToken);
                    _disconnectCancelToken.ThrowIfCancellationRequested();
                    if (!connect.Connect.Disconnect) continue;

                    _disconnectCancel.Cancel();
                    return;
                }
            }
            catch
            {
                // Ignored, could already be disconnected...
            }
        }, _disconnectCancelToken);
    }

    private async ValueTask SendDisconnectAsync()
    {
        try
        {
            using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(60));
            await Tunnel.TunnelDisconnectAsync(Address, cancel.Token);
        }
        catch
        {
            /* Ignore, could already be disconnected */
        }
    }
}