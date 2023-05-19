namespace AzureWebPubSubBridge.Messages;

public record ReceivedMessageId(string From, long MessageId)
{
    private readonly int _hashCode = HashCode.Combine(From, MessageId);

    public virtual bool Equals(ReceivedMessageId? other)
    {
        return GetHashCode() == other?.GetHashCode();
    }

    public override int GetHashCode()
    {
        return _hashCode;
    }
}