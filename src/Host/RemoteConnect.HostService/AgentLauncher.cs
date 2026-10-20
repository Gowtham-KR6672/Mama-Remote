using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RemoteConnect.LocalIpc;
using RemoteConnect.LocalIpc.Windows;

namespace RemoteConnect.HostService;

/// <summary>A launched session agent: its process handle (which also keeps the PID from being reused) and exit task.</summary>
[SupportedOSPlatform("windows")]
internal sealed class AgentProcess : IDisposable
{
    private readonly SafeProcessHandle _handle;
    private readonly RegisteredWaitHandle _registration;
    private readonly ProcessWaitHandle _waitHandle;

    public AgentProcess(SafeProcessHandle handle, int processId, uint sessionId)
    {
        _handle = handle;
        ProcessId = processId;
        SessionId = sessionId;
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitHandle = new ProcessWaitHandle(handle);
        _registration = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) =>
        {
            try
            {
                NativeMethods.GetExitCodeProcess(_handle, out uint code);
                exited.TrySetResult(unchecked((int)code));
            }
            catch (ObjectDisposedException)
            {
                exited.TrySetResult(-1); // disposed while the exit was being reported
            }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        Exited = exited.Task;
    }

    public int ProcessId { get; }

    public uint SessionId { get; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    public Task<int> Exited { get; }

    public void Kill()
    {
        if (!Exited.IsCompleted)
        {
            NativeMethods.TerminateProcess(_handle, 1);
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _waitHandle.Dispose();
        _handle.Dispose();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
            => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}

/// <summary>
/// Starts the session agent in a given session. The executable and its single argument are fixed (the agent next to
/// this service, with <c>--agent</c>); nothing received over IPC or from users can change what is started.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AgentLauncher
{
    /// <summary>
    /// LocalSystem token → privilege-free copy → moved into <paramref name="sessionId"/> → agent started on
    /// <c>winsta0\default</c>. Keeping the LocalSystem identity is what allows the agent to attach to the Winlogon
    /// (sign-in / UAC) desktop; dropping every privilege (SeDebug, SeTcb, SeImpersonate, …) limits what a compromised
    /// agent could do with it.
    /// </summary>
    public static AgentProcess Launch(uint sessionId, string agentPath)
    {
        string directory = Path.GetDirectoryName(agentPath)!;
        const uint access = NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_ASSIGN_PRIMARY
            | NativeMethods.TOKEN_ADJUST_DEFAULT | NativeMethods.TOKEN_ADJUST_SESSIONID;
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), access, out var self))
        {
            throw Error("OpenProcessToken");
        }

        using (self)
        {
            if (!NativeMethods.CreateRestrictedToken(self, NativeMethods.DISABLE_MAX_PRIVILEGE, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
            {
                throw Error("CreateRestrictedToken");
            }

            using (restricted)
            {
                if (!NativeMethods.DuplicateTokenEx(restricted, NativeMethods.MAXIMUM_ALLOWED, IntPtr.Zero,
                        NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out var primary))
                {
                    throw Error("DuplicateTokenEx");
                }

                using (primary)
                {
                    uint session = sessionId;
                    if (!NativeMethods.SetTokenInformation(primary, NativeMethods.TokenSessionId, ref session, sizeof(uint)))
                    {
                        throw Error("SetTokenInformation(TokenSessionId)");
                    }

                    if (!NativeMethods.CreateEnvironmentBlock(out var environment, primary, false))
                    {
                        throw Error("CreateEnvironmentBlock");
                    }

                    try
                    {
                        var startup = new NativeMethods.STARTUPINFO
                        {
                            cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                            lpDesktop = @"winsta0\default",
                        };
                        var commandLine = $"\"{agentPath}\" {ServiceModeLayout.AgentArgument}\0".ToCharArray();
                        if (!NativeMethods.CreateProcessAsUser(primary, agentPath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                                NativeMethods.CREATE_UNICODE_ENVIRONMENT, environment, directory, ref startup, out var info))
                        {
                            throw Error("CreateProcessAsUser");
                        }

                        NativeMethods.CloseHandle(info.hThread);
                        return new AgentProcess(new SafeProcessHandle(info.hProcess, ownsHandle: true), info.dwProcessId, sessionId);
                    }
                    finally
                    {
                        NativeMethods.DestroyEnvironmentBlock(environment);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Ends agents left behind by a previous instance of the service (e.g. after a crash). Only processes that run
    /// as LocalSystem from this exact agent path are touched; users' own RemoteConnect windows are never affected.
    /// </summary>
    public static void KillStaleAgents(string agentPath, ILogger logger)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(agentPath)))
        {
            using (process)
            {
                try
                {
                    var peer = PipePeer.Describe(process.Id);
                    if (peer.IsLocalSystem && ServiceModeLayout.SamePath(peer.ImagePath, agentPath))
                    {
                        logger.LogWarning("Ending a stale session agent (process {Pid}) left by a previous service instance", process.Id);
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Exited meanwhile, or not ours to inspect.
                }
            }
        }
    }

    private static Win32Exception Error(string api)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{api} failed: {new Win32Exception(code).Message}");
    }
}
