using System.Net.WebSockets;
using System.Text;

namespace AzureWebPubSubBridge.Tunnels.PubSub;

public class WebSocketStream : Stream
{
    private readonly CancellationToken _cancellationToken;
    private readonly ClientWebSocket _connection;
    private StringBuilder? _captureBuilder;
    private long _length;
    private ValueWebSocketReceiveResult? _receive;

    public WebSocketStream(ClientWebSocket connection, CancellationToken cancellationToken, bool shouldCapture = false)
    {
        ShouldCapture = shouldCapture;
        _connection = connection;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead { get; } = true;

    public override bool CanSeek { get; } = false;

    public override bool CanWrite { get; } = false;

    public string? Capture => _captureBuilder?.ToString();

    public override long Length => _length;

    public override long Position { get; set; }

    public bool ShouldCapture { get; }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, _cancellationToken).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = new())
    {
        if (_receive?.EndOfMessage == true)
        {
            _receive = null;
            return 0;
        }

        if (_receive == null)
        {
            _captureBuilder = ShouldCapture ? new StringBuilder() : null;
            _length = 0;
            Position = 0;
        }

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
        _receive = await _connection.ReceiveAsync(buffer, linkedSource.Token);

        var read = _receive?.Count ?? -1;
        if (read == -1) return -1;

        if (_receive?.MessageType == WebSocketMessageType.Close) return -1;

        _captureBuilder?.Append(Encoding.UTF8.GetString(buffer.Span.Slice(0, read)));

        _length += read;
        Position = _length - 1;

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return 0;
    }

    public override void SetLength(long value)
    {
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
    }
}