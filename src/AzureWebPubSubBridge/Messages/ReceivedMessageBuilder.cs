using System.Collections.Concurrent;

namespace AzureWebPubSubBridge.Messages;

public class ReceivedMessageBuilder : IDisposable
{
    public readonly DateTimeOffset Created = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<long, ReceivedMessageSegment> _segments = new();

    public volatile bool IsComplete;
    private volatile int _totalMessageSize;
    private volatile int _totalMessageSizeReceived;

    public ReceivedMessageBuilder(int totalSize)
    {
        _totalMessageSize = totalSize;
    }

    public int TotalMessageSize => _totalMessageSize;

    public void Dispose()
    {
        foreach (var segment in _segments.ToArray()) segment.Value.Dispose();
    }

    public void AddSegment(ReceivedMessageSegment messageSegment)
    {
        if (_segments.TryAdd(messageSegment.SequenceNumber, messageSegment))
        {
            var isComplete = Interlocked.Add(ref _totalMessageSizeReceived, messageSegment.Length) >= _totalMessageSize;
            if (isComplete) IsComplete = true;
        }
    }

    public SortedDictionary<long, ReceivedMessageSegment> GetMessages()
    {
        var segments = _segments.ToArray();
        _segments.Clear();
        if (_totalMessageSizeReceived > _totalMessageSize)
        {
            // Dispose all buffers to prevent leaking memory due to extraneous error
            foreach (var segment in segments)
                segment.Value.Dispose();

            throw new InvalidOperationException("Received more message data than what was expected");
        }

        SortedDictionary<long, ReceivedMessageSegment> result = new();
        foreach (var segment in segments)
            result.Add(segment.Key, segment.Value);

        return result;
    }
}