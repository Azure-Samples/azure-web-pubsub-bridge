namespace AzureWebPubSubBridge.Protocols.Socks;

public enum SocksCommandType : byte
{
    Connect = 0x01,
    Bind = 0x02,
    UdpAssociate = 0x03
}

public enum AddressType : byte
{
    IPv4 = 0x01,
    DomainName = 0x03,
    IPv6 = 0x04
}