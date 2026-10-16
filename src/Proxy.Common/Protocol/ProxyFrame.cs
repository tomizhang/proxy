using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;

namespace Proxy.Common.Protocol;

/// <summary>
/// 自定义代理二进制协议帧
/// </summary>
public sealed class ProxyFrame
{
    public const byte MagicByte1 = 0x50; // 'P'
    public const byte MagicByte2 = 0x58; // 'X'
    public const byte CurrentVersion = 0x01;

    public byte Version { get; set; } = CurrentVersion;
    public ProxyCommand Command { get; set; }
    public ulong SessionId { get; set; }

    public AddressType AddressType { get; set; } = AddressType.None;
    public string? TargetHost { get; set; }
    public ushort TargetPort { get; set; }

    public ReadOnlyMemory<byte> Payload { get; set; } = ReadOnlyMemory<byte>.Empty;

    public static ProxyFrame CreateConnect(ulong sessionId, string host, ushort port, AddressType addrType)
    {
        return new ProxyFrame
        {
            Command = ProxyCommand.ConnectTcp,
            SessionId = sessionId,
            AddressType = addrType,
            TargetHost = host,
            TargetPort = port
        };
    }

    public static ProxyFrame CreateData(ulong sessionId, ReadOnlyMemory<byte> data)
    {
        return new ProxyFrame
        {
            Command = ProxyCommand.DataTcp,
            SessionId = sessionId,
            Payload = data
        };
    }

    public static ProxyFrame CreateClose(ulong sessionId)
    {
        return new ProxyFrame
        {
            Command = ProxyCommand.CloseTcp,
            SessionId = sessionId
        };
    }

    public static ProxyFrame CreateUdp(ulong sessionId, string host, ushort port, AddressType addrType, ReadOnlyMemory<byte> packet)
    {
        return new ProxyFrame
        {
            Command = ProxyCommand.UdpPacket,
            SessionId = sessionId,
            AddressType = addrType,
            TargetHost = host,
            TargetPort = port,
            Payload = packet
        };
    }

    public static ProxyFrame CreatePing()
    {
        return new ProxyFrame
        {
            Command = ProxyCommand.Ping
        };
    }

    public static ProxyFrame CreatePong()
    {
        return new ProxyFrame
        {
            Command = ProxyCommand.Pong
        };
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(MagicByte1);
        ms.WriteByte(MagicByte2);
        ms.WriteByte(Version);
        ms.WriteByte((byte)Command);

        Span<byte> numBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(numBuf, SessionId);
        ms.Write(numBuf);

        ms.WriteByte((byte)AddressType);

        if (AddressType != AddressType.None)
        {
            BinaryPrimitives.WriteUInt16BigEndian(numBuf.Slice(0, 2), TargetPort);
            ms.Write(numBuf.Slice(0, 2));

            byte[] hostBytes = Encoding.UTF8.GetBytes(TargetHost ?? string.Empty);
            if (hostBytes.Length > 255)
            {
                throw new InvalidOperationException("目标主机地址过长 (超出 255 字节)");
            }
            ms.WriteByte((byte)hostBytes.Length);
            ms.Write(hostBytes);
        }

        BinaryPrimitives.WriteInt32BigEndian(numBuf.Slice(0, 4), Payload.Length);
        ms.Write(numBuf.Slice(0, 4));

        if (!Payload.IsEmpty)
        {
            ms.Write(Payload.Span);
        }

        return ms.ToArray();
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> span, out ProxyFrame? frame)
    {
        frame = null;
        if (span.Length < 17) // 基础最小长度: Magic(2) + Ver(1) + Cmd(1) + SessionId(8) + AddrType(1) + PayloadLen(4) = 17
        {
            return false;
        }

        if (span[0] != MagicByte1 || span[1] != MagicByte2)
        {
            return false;
        }

        byte version = span[2];
        var command = (ProxyCommand)span[3];
        ulong sessionId = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(4, 8));
        var addrType = (AddressType)span[12];

        int offset = 13;
        string? targetHost = null;
        ushort targetPort = 0;

        if (addrType != AddressType.None)
        {
            if (span.Length < offset + 3) // Port(2) + HostLen(1)
            {
                return false;
            }

            targetPort = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
            offset += 2;

            byte hostLen = span[offset++];
            if (span.Length < offset + hostLen + 4)
            {
                return false;
            }

            targetHost = Encoding.UTF8.GetString(span.Slice(offset, hostLen));
            offset += hostLen;
        }

        if (span.Length < offset + 4)
        {
            return false;
        }

        int payloadLen = BinaryPrimitives.ReadInt32BigEndian(span.Slice(offset, 4));
        offset += 4;

        if (payloadLen < 0 || span.Length < offset + payloadLen)
        {
            return false;
        }

        ReadOnlyMemory<byte> payload = payloadLen > 0 ? span.Slice(offset, payloadLen).ToArray() : ReadOnlyMemory<byte>.Empty;

        frame = new ProxyFrame
        {
            Version = version,
            Command = command,
            SessionId = sessionId,
            AddressType = addrType,
            TargetHost = targetHost,
            TargetPort = targetPort,
            Payload = payload
        };

        return true;
    }
}
