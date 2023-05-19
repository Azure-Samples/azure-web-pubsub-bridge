namespace AzureWebPubSubBridge.Exceptions;

public class SocksConnectionFailedException : Exception
{
    public SocksConnectionFailedException(string message)
        : base(message)
    {
    }
}