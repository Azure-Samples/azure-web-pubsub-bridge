using AzureWebPubSubBridge.Messages;

namespace AzureWebPubSubBridge.Exceptions;

public class HandleMessageException : Exception
{
    public HandleMessageException(string message, IPubSubMessage handledMessage, Exception? innerException = null)
        : base(message, innerException)
    {
        HandledMessage = handledMessage;
    }

    public IPubSubMessage HandledMessage { get; }
}

public class HandleServerJoinMessageException : HandleMessageException
{
    public HandleServerJoinMessageException(string message, IAckPubSubMessage handledMessage,
        Exception? innerException = null)
        : base(message, handledMessage, innerException)
    {
    }
}

public class HandleDisconnectedMessageException : HandleMessageException
{
    public HandleDisconnectedMessageException(string message,
        IDisconnectedSystemPubSubMessage pubSubMessage, Exception? innerException = null)
        : base(message, pubSubMessage, innerException)
    {
    }
}