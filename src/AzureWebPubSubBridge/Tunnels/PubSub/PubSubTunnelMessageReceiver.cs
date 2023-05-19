using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AzureWebPubSubBridge.Formats;
using AzureWebPubSubBridge.Messages;
using AzureWebPubSubBridge.Protocols;
using AzureWebPubSubBridge.Utilities;
using Microsoft.Extensions.Logging;

namespace AzureWebPubSubBridge.Tunnels.PubSub;

public class PubSubTunnelMessageReceiver : IDisposable
{
    private readonly ConcurrentDictionary<string, AsyncManualResetEvent<bool>> _available = new();
    private readonly ConcurrentQueue<BridgeConnect> _connects = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ReceivedMessage>> _inbox = new();
    private readonly ILogger<PubSubTunnelMessageReceiver> _logger;
    private readonly ConcurrentDictionary<ReceivedMessageId, ReceivedMessageBuilder> _segments = new();
    private bool _clearing;
    private string? _clientId;
    private AsyncManualResetEvent<bool> _connectAvailable = new();
    private bool _disposing;
    private byte[] _encodedClientId = Array.Empty<byte>();
    private string? _formattedClientId;
    private string? _serverId;

    public PubSubTunnelMessageReceiver(ILogger<PubSubTunnelMessageReceiver> logger)
    {
        _logger = logger;
    }

    public bool DataAvailable => !_inbox.IsEmpty;

    public void Dispose()
    {
        _disposing = true;

        Clear();
    }

    public void Initialize(string serverId, string clientId)
    {
        _serverId = serverId;
        _clientId = clientId;
        if (!string.IsNullOrEmpty(clientId))
        {
            _formattedClientId = PubSubDataFormat.FormatId(clientId);
            _encodedClientId = Encoding.UTF8.GetBytes(_formattedClientId);
        }
        else
        {
            _formattedClientId = null;
            _encodedClientId = Array.Empty<byte>();
        }

        Clear();
    }

    public async ValueTask<BridgeConnect> ReceiveConnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_connects.TryDequeue(out var connect))
                return connect;

            if (!await WaitConnectAsync(cancellationToken))
                throw new OperationCanceledException(cancellationToken);

            if (_connects.TryDequeue(out connect))
                return connect;

            ResetConnectAvailable();
        }

        throw new OperationCanceledException(cancellationToken);
    }

    public void ReceiveMessage(IMemoryOwner<byte> message)
    {
        var from = ShouldReceiveMessage(message);
        if (from == null)
        {
            message.Dispose();
            return;
        }

        if (PubSubDataFormat.IsConnect(message.Memory.Span))
            ReceiveConnectMessage(from, message);
        else
            ReceiveDataMessage(from, message);
    }

    public async Task<ReceivedMessage> ReceiveMessagesAsync(string from, CancellationToken cancellationToken = new())
    {
        from = PubSubDataFormat.FormatId(from);
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = ReceivedMessage(from);
            if (message != null)
                return message;

            if (!await WaitAsync(from, cancellationToken))
                throw new OperationCanceledException(cancellationToken);

            message = ReceivedMessage(from);
            if (message != null)
                return message;

            ResetAvailable(from);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    public async ValueTask<bool> WaitAsync(string from, CancellationToken cancellationToken)
    {
        if (_disposing)
            return false;

        return await _available.GetOrAdd(from, _ => new AsyncManualResetEvent<bool>()).WaitAsync(cancellationToken);
    }

    public async ValueTask<bool> WaitConnectAsync(CancellationToken cancellationToken)
    {
        if (_disposing)
            return false;

        return await _connectAvailable.WaitAsync(cancellationToken);
    }

    private void CaptureMessage(ReadOnlySpan<byte> memorySpan, int written)
    {
        var data = Encoding.UTF8.GetString(PubSubDataFormat.GetData(memorySpan, written));
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("[[{Name}]] <- {data}", "Data",
                Encoding.UTF8.GetString(PubSubDataFormat.GetData(memorySpan, written)));
    }

    private void Clear(string? from = null)
    {
        _clearing = true;

        try
        {
            foreach (var (key, available) in _available.Where(x => from == null || x.Key == from).ToArray())
            {
                available.Cancel();
                _available.TryRemove(key, out _);
            }

            foreach (var (key, queue) in _inbox.Where(x => from == null || x.Key == from).ToArray())
            {
                while (queue.TryDequeue(out var segment))
                    segment.Dispose();
                _inbox.TryRemove(key, out _);
            }

            foreach (var (key, segment) in _segments.ToArray())
            {
                segment.Dispose();
                _segments.TryRemove(key, out _);
            }

            _connectAvailable.Cancel();
            _connectAvailable = new AsyncManualResetEvent<bool>();
        }
        finally
        {
            _clearing = false;
        }
    }

    private void ReceiveConnectMessage(string from, IMemoryOwner<byte> message)
    {
        try
        {
            PubSubDataFormat.DecodeData(message.Memory.Span, out var length);

            CaptureMessage(message.Memory.Span, length);

            var data = PubSubDataFormat.GetData(message.Memory.Span, length);
            var jsonReader = new Utf8JsonReader(data);
            var connect =
                JsonSerializer.Deserialize<TunnelConnect>(ref jsonReader,
                    PubSubTunnel.DefaultJsonSerializerOptions);
            if (connect == null)
                throw new ArgumentException("Message was not in correct ConnectMessage format", nameof(message));

            _connects.Enqueue(new BridgeConnect(from, connect));
            _connectAvailable.Set(true);
        }
        finally
        {
            message.Dispose();
        }
    }

    private void ReceiveDataMessage(string from, IMemoryOwner<byte> message)
    {
        PubSubDataFormat.DecodeData(message.Memory.Span, out var length);

        CaptureMessage(message.Memory.Span, length);

        var messageId = new ReceivedMessageId(from, PubSubDataFormat.GetMessageId(message.Memory.Span));
        var messageSegments = _segments.GetOrAdd(messageId,
            _ => new ReceivedMessageBuilder(PubSubDataFormat.GetTotalDecodedSize(message.Memory.Span)));

        var sequenceNumber = PubSubDataFormat.GetSequenceNumber(message.Memory.Span);
        var receivedMessage = new ReceivedMessageSegment(message, length, sequenceNumber);
        messageSegments.AddSegment(receivedMessage);

        if (messageSegments.IsComplete && _segments.TryRemove(messageId, out var segments))
        {
            var queue = _inbox.GetOrAdd(messageId.From, _ => new ConcurrentQueue<ReceivedMessage>());
            queue.Enqueue(new ReceivedMessage(messageId, segments.GetMessages(), segments.TotalMessageSize, from));
            _available.GetOrAdd(messageId.From, _ => new AsyncManualResetEvent<bool>()).Set(true);
        }
    }

    private ReceivedMessage? ReceivedMessage(string from)
    {
        if (!_inbox.TryGetValue(from, out var queue)) return null;

        if (queue.TryDequeue(out var receivedMessage)) return receivedMessage;

        return null;
    }

    private void ResetAvailable(string from)
    {
        if (_disposing)
            return;

        _available.GetOrAdd(from, _ => new AsyncManualResetEvent<bool>()).Reset();
    }

    private void ResetConnectAvailable()
    {
        if (_disposing)
            return;

        _connectAvailable.Reset();
    }

    private string? ShouldReceiveMessage(IMemoryOwner<byte> message)
    {
        if (_disposing || _clearing)
            return null;

        // Is this a tunnel message?
        if (!PubSubDataFormat.ContainsMagic(message.Memory.Span))
            return null;

        var encodedClientId = _encodedClientId.AsSpan();
        if (encodedClientId.Length == 0)
            return null;

        // Is this our tunnel message?
        if (!PubSubDataFormat.IsAddressedTo(message.Memory.Span, _encodedClientId.AsSpan()))
            return null;

        return PubSubDataFormat.GetFromAddress(message.Memory.Span);
    }
}