using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Proxy.Client.Core.Tun;

/// <summary>
/// Wintun 虚拟网卡驱动原生 P/Invoke 接口定义
/// 官方文档参考: https://git.zx2c4.com/wintun/about/
/// </summary>
public static class Wintun
{
    private const string DllName = "wintun.dll";

    public static bool IsWintunAvailable()
    {
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        return File.Exists(Path.Combine(currentDir, DllName)) ||
               File.Exists(Path.Combine(currentDir, "x64", DllName)) ||
               File.Exists(Path.Combine(Environment.SystemDirectory, DllName));
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WintunCreateAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string pool,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        in Guid requestedGuid);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WintunOpenAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string pool,
        [MarshalAs(UnmanagedType.LPWStr)] string name);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern void WintunCloseAdapter(IntPtr adapter);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern void WintunEndSession(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern void WintunSendPacket(IntPtr session, IntPtr packet);
}
