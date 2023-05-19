namespace AzureWebPubSubBridge;

public class BridgeConfiguration
{
    public const string Local = "Local";
    public const string Remote = "Remote";

    public ConnectConfiguration Connect { get; set; } = new();

    public int DefaultRequestTimeoutSeconds { get; set; } = 10 * 60;

    public string Hub { get; set; } = default!;

    public int PacketSize { get; set; } = 1024 * 512;

    public int Port { get; set; } = 42500;

    public Uri PubSubEndpoint { get; set; } = default!;

    public string? PubSubKey { get; set; } = null;

    public bool StopOnError { get; set; } = false;
}

public class ConnectConfiguration
{
    public string? DomainName { get; set; }

    public string? IpAddress { get; set; }

    public int Port { get; set; }

    public string ServerId { get; set; } = Guid.NewGuid().ToString("N");
}