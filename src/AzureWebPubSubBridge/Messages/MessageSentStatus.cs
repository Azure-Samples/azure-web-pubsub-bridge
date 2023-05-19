namespace AzureWebPubSubBridge.Messages;

public record MessageSentStatus(bool Succeeded)
{
    public long AckId { get; init; }

    public int AmountConsumed { get; init; }

    public bool Duplicated { get; init; }

    public Exception? Error { get; init; }
}