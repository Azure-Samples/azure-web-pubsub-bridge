namespace AzureWebPubSubBridge.Exceptions;

public class PubSubWebSocketStoppedException : Exception
{
    public PubSubWebSocketStoppedException(string? message = null, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public class PubSubTunnelBackoffException : Exception
{
    public PubSubTunnelBackoffException(string? message = null, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}