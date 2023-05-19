namespace AzureWebPubSubBridge.Messages;

public record ReceivedMessage(ReceivedMessageId MessageId, SortedDictionary<long, ReceivedMessageSegment> Segments,
    int TotalMessageSize, string Sender) : IDisposable
{
    public void Dispose()
    {
        foreach (var (_, segment) in Segments)
            segment.Dispose();
    }
}