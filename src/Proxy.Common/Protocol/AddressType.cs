namespace Proxy.Common.Protocol;

public enum AddressType : byte
{
    None = 0x00,
    IPv4 = 0x01,
    Domain = 0x03,
    IPv6 = 0x04
}
