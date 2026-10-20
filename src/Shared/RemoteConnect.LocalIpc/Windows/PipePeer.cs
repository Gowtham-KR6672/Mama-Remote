using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RemoteConnect.LocalIpc.Windows;

/// <summary>The process on the other end of a named pipe, as reported by the kernel (not by the peer).</summary>
[SupportedOSPlatform("windows")]
public sealed record PipePeerInfo(int ProcessId, string ImagePath, SecurityIdentifier? User)
{
    public bool IsLocalSystem => User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
}

[SupportedOSPlatform("windows")]
public static class PipePeer
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;

    public static PipePeerInfo Client(SafePipeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out uint pid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetNamedPipeClientProcessId failed.");
        }

        return Describe((int)pid);
    }

    public static PipePeerInfo Server(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint pid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetNamedPipeServerProcessId failed.");
        }

        return Describe((int)pid);
    }

    /// <summary>Image path and account of a running process (used for pipe peers and for stale-agent cleanup).</summary>
    public static PipePeerInfo Describe(int processId)
    {
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open process {processId}.");
        }

        return new PipePeerInfo(processId, ImagePath(process), TokenUser(process));
    }

    public static string ImagePath(SafeProcessHandle process)
    {
        var buffer = new char[1024];
        int size = buffer.Length;
        if (!QueryFullProcessImageName(process, 0, buffer, ref size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryFullProcessImageName failed.");
        }

        return new string(buffer, 0, size);
    }

    private static SecurityIdentifier? TokenUser(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
        {
            return null;
        }

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return identity.User;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] exeName, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}
