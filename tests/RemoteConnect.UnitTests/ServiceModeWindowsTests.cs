using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RemoteConnect.HostService;
using RemoteConnect.LocalIpc;
using RemoteConnect.LocalIpc.Windows;
using RemoteConnect.Platform;
using RemoteConnect.Platform.Windows;

namespace RemoteConnect.UnitTests;

/// <summary>
/// Service-mode pieces that need real Windows objects: the named pipe and its peer checks, the install-folder ACL check
/// and the input-desktop thread. They run as the test user, never as LocalSystem, and inject no input.
/// Each test returns early on other platforms.
/// </summary>
[SupportedOSPlatform("windows")]
public class ServiceModeWindowsTests
{
    private static string PipeName() => "RemoteConnect.Test." + Guid.NewGuid().ToString("N");

    private static bool RunningAsSystem()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.IsSystem;
    }

    [Fact]
    public async Task Pipe_round_trip_with_verified_peers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = PipeName();
        using var me = WindowsIdentity.GetCurrent();
        await using var server = AgentPipeServer.Create(name, ServicePipe.CreateSecurity(me.User!));
        var accept = server.AcceptAsync(Environment.ProcessId, Environment.ProcessPath!, default);

        await using var agent = await ServicePipe.ConnectAsync(name, Environment.ProcessPath!, requireLocalSystem: false, TimeSpan.FromSeconds(5), default);
        await using var service = await accept;
        Assert.Equal(Environment.ProcessId, agent.PeerProcessId);
        Assert.Equal(Environment.ProcessId, service.PeerProcessId);

        await agent.SendAsync(IpcMessageTests.ValidStatus(), default);
        await agent.SendAsync(new HeartbeatMessage(1), default);
        Assert.Equal(IpcMessageTests.ValidStatus(), await service.ReceiveAsync(default));
        Assert.Equal(new HeartbeatMessage(1), await service.ReceiveAsync(default));
        await service.SendAsync(new HeartbeatMessage(1), default);
        Assert.Equal(new HeartbeatMessage(1), await agent.ReceiveAsync(default));

        await service.DisposeAsync(); // the service closing the pipe is the agent's signal to exit
        Assert.Null(await agent.ReceiveAsync(default));
    }

    [Fact]
    public async Task Pipe_server_rejects_any_process_other_than_the_launched_agent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = PipeName();
        using var me = WindowsIdentity.GetCurrent();
        await using var server = AgentPipeServer.Create(name, ServicePipe.CreateSecurity(me.User!));
        var accept = server.AcceptAsync(Environment.ProcessId + 1, Environment.ProcessPath!, default);
        await using var client = await ServicePipe.ConnectAsync(name, Environment.ProcessPath!, requireLocalSystem: false, TimeSpan.FromSeconds(5), default);
        await Assert.ThrowsAsync<IpcPeerRejectedException>(() => accept);

        var name2 = PipeName();
        await using var server2 = AgentPipeServer.Create(name2, ServicePipe.CreateSecurity(me.User!));
        var accept2 = server2.AcceptAsync(Environment.ProcessId, @"C:\Program Files\RemoteConnect\RemoteConnect.Desktop.exe", default);
        await using var client2 = await ServicePipe.ConnectAsync(name2, Environment.ProcessPath!, requireLocalSystem: false, TimeSpan.FromSeconds(5), default);
        await Assert.ThrowsAsync<IpcPeerRejectedException>(() => accept2);
    }

    [Fact]
    public async Task Agent_refuses_a_pipe_server_that_is_not_the_LocalSystem_service()
    {
        if (!OperatingSystem.IsWindows() || RunningAsSystem())
        {
            return;
        }

        using var me = WindowsIdentity.GetCurrent();
        var name = PipeName();
        await using (var squatter = AgentPipeServer.Create(name, ServicePipe.CreateSecurity(me.User!)))
        {
            _ = squatter.AcceptAsync(Environment.ProcessId, Environment.ProcessPath!, default);
            await Assert.ThrowsAsync<IpcPeerRejectedException>(() =>
                ServicePipe.ConnectAsync(name, Environment.ProcessPath!, requireLocalSystem: true, TimeSpan.FromSeconds(5), default));
        }

        var name2 = PipeName();
        await using var wrongImage = AgentPipeServer.Create(name2, ServicePipe.CreateSecurity(me.User!));
        _ = wrongImage.AcceptAsync(Environment.ProcessId, Environment.ProcessPath!, default);
        await Assert.ThrowsAsync<IpcPeerRejectedException>(() =>
            ServicePipe.ConnectAsync(name2, @"C:\Program Files\RemoteConnect\RemoteConnect.HostService.exe", requireLocalSystem: false, TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task Production_pipe_acl_admits_only_LocalSystem()
    {
        if (!OperatingSystem.IsWindows() || RunningAsSystem())
        {
            return;
        }

        var security = ServicePipe.CreateSecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow
            && ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny
            && ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid));
        Assert.All(rules.Where(r => r.AccessControlType == AccessControlType.Allow),
            r => Assert.True(((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid)));

        // A real pipe with the production ACL: this (non-SYSTEM) test user cannot even open it.
        var name = PipeName();
        await using var server = AgentPipeServer.Create(name, security);
        _ = server.AcceptAsync(Environment.ProcessId, Environment.ProcessPath!, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ServicePipe.ConnectAsync(name, Environment.ProcessPath!, requireLocalSystem: false, TimeSpan.FromSeconds(5), default));
    }

    [Fact]
    public async Task A_second_pipe_with_the_same_name_cannot_be_created()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The service creates its pipe as the first instance: if a squatter already holds the name, creation fails.
        using var me = WindowsIdentity.GetCurrent();
        var name = PipeName();
        await using var first = AgentPipeServer.Create(name, ServicePipe.CreateSecurity(me.User!));
        Assert.ThrowsAny<Exception>(() => AgentPipeServer.Create(name, ServicePipe.CreateSecurity(me.User!)));
    }

    [Fact]
    public void Install_folder_check_rejects_user_writable_locations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A temp folder is writable (and owned) by the current user: the agent must never be started from there.
        var dir = Directory.CreateTempSubdirectory("rc-install-check").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "RemoteConnect.Desktop.exe"), "x");
            Assert.NotNull(InstallLocationCheck.FindProblem(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);

        // Program Files style: SYSTEM/Administrators full, Users read & execute, CREATOR OWNER for new children only.
        var programFiles = new DirectorySecurity();
        programFiles.SetOwner(admins);
        programFiles.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        programFiles.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        programFiles.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        programFiles.AddAccessRule(new FileSystemAccessRule(creatorOwner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        Assert.Null(InstallLocationCheck.Check(programFiles, "dir", isDirectory: true));

        var writable = new DirectorySecurity();
        writable.SetOwner(admins);
        writable.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.Contains("can be modified", InstallLocationCheck.Check(writable, "dir", isDirectory: true));

        var userOwned = new FileSecurity();
        userOwned.SetOwner(users);
        Assert.Contains("owned by", InstallLocationCheck.Check(userOwned, "file", isDirectory: false));
    }

    [Fact]
    public void Input_desktop_thread_runs_work_on_its_own_thread_and_reports_errors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // In an interactive test run this is "Default"; it is null when the input desktop is out of reach (e.g. CI service).
        var name = InputDesktop.TryGetName();
        Assert.True(name is null || name.Length > 0);

        using var thread = new InputDesktopThread("test");
        int caller = Environment.CurrentManagedThreadId;
        int worker = thread.Invoke(() => Environment.CurrentManagedThreadId);
        Assert.NotEqual(caller, worker);
        Assert.Equal(worker, thread.Invoke(() => Environment.CurrentManagedThreadId)); // always the same thread
        Assert.Throws<InvalidOperationException>(() => thread.Invoke<int>(() => throw new InvalidOperationException("boom")));
        if (name is not null)
        {
            Assert.Equal(name, thread.CurrentDesktop);
        }
    }

    [Fact]
    public void Input_desktop_capturer_forwards_to_the_wrapped_capturer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var inner = new FakeCapturer();
        using (var capturer = new InputDesktopCapturer(inner))
        {
            var display = Assert.Single(capturer.GetDisplays());
            Assert.True(capturer.Capture(display, new FrameBuffer(), 100, 100));
            Assert.NotEqual(Environment.CurrentManagedThreadId, inner.CaptureThread);
        }

        Assert.True(inner.Disposed);
    }

    /// <summary>
    /// Real GDI capture through the input-desktop thread (what the service-mode agent uses), in memory only.
    /// Opt-in like the plain GDI test: set RC_TEST_REAL_CAPTURE=1.
    /// </summary>
    [Fact]
    public void Input_desktop_capture_returns_a_real_frame_when_enabled()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("RC_TEST_REAL_CAPTURE") != "1")
        {
            return;
        }

        using var capturer = PlatformServices.CreateInputDesktopCapturer();
        var displays = capturer.GetDisplays();
        Assert.NotEmpty(displays);
        var frame = new FrameBuffer();
        Assert.True(capturer.Capture(displays[0], frame, 1280, 720));
        Assert.InRange(frame.Width, 2, 1280);
        Assert.Contains(frame.Pixels.Where((_, i) => i % 4 != 3), b => b != 0);
        Array.Clear(frame.Pixels);
    }

    private sealed class FakeCapturer : IScreenCapturer
    {
        public int CaptureThread { get; private set; }

        public bool Disposed { get; private set; }

        public IReadOnlyList<DisplayInfo> GetDisplays() => new[] { new DisplayInfo(0, "Fake", true, 0, 0, 4, 4, 4, 4) };

        public bool Capture(DisplayInfo display, FrameBuffer target, int maxWidth, int maxHeight)
        {
            CaptureThread = Environment.CurrentManagedThreadId;
            target.EnsureSize(4, 4);
            return true;
        }

        public void Dispose() => Disposed = true;
    }
}
