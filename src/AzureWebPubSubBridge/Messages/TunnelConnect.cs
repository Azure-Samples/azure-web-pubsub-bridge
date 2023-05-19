namespace AzureWebPubSubBridge.Messages;

public class TunnelConnect
{
    public string? ClientId { get; set; }

    public bool Connected { get; set; }

    public bool Disconnect { get; set; }

    public string? DomainName { get; set; }

    public string? IpAddress { get; set; }

    public int Port { get; set; }

    public string? ServerId { get; set; }
}