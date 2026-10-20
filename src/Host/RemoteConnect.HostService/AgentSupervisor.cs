using System.ComponentModel;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteConnect.LocalIpc;
using RemoteConnect.LocalIpc.Windows;

namespace RemoteConnect.HostService;

/// <summary>
/// Runs the session agent in the active console session — from power-on at the sign-in screen, through sign-in,
/// lock and user switching — and relaunches it when it crashes or hangs. All decisions come from
/// <see cref="AgentSupervisorStateMachine"/>; this class only performs them and feeds events back, one at a time.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AgentSupervisor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly ConsoleSessionSignal _signal;
    private readonly AgentSupervisorStateMachine _machine = new();
    private readonly Channel<SupervisorEvent> _events = Channel.CreateUnbounded<SupervisorEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _installDirectory = AppContext.BaseDirectory;
    private readonly string _agentPath;

    private AgentInstance? _agent;
    private AgentStatusMessage? _lastStatus;
    private string? _launchProblem;
    private ServicePublicStatus? _published;

    public AgentSupervisor(ILogger<AgentSupervisor> logger, ConsoleSessionSignal signal)
    {
        _logger = logger;
        _signal = signal;
        _agentPath = ServiceModeLayout.AgentPath(_installDirectory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RemoteConnect host service started from {Directory}", _installDirectory);
        Directory.CreateDirectory(ServiceModeLayout.LogDirectory); // inside the LocalSystem profile: SYSTEM + Administrators only
        AgentLauncher.KillStaleAgents(_agentPath, _logger);
        Publish();

        var pump = PumpAsync(stoppingToken);
        try
        {
            await foreach (var ev in _events.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                Handle(ev);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await ShutdownAsync().ConfigureAwait(false);
        await pump.ConfigureAwait(false);
        _logger.LogInformation("RemoteConnect host service stopped");
    }

    /// <summary>Clock ticks plus console-session polling; a session-change notification triggers an immediate poll.</summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        uint? lastSession = null;
        while (!ct.IsCancellationRequested)
        {
            uint session = NativeMethods.WTSGetActiveConsoleSessionId();
            if (session != lastSession)
            {
                lastSession = session;
                Post(new SessionEvent(session));
            }

            Post(new TickEvent());
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(PollInterval);
                await _signal.WaitAsync(wait.Token).ConfigureAwait(false);
                _signal.Reset();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void Post(SupervisorEvent ev) => _events.Writer.TryWrite(ev);

    private void Handle(SupervisorEvent ev)
    {
        var actions = ev switch
        {
            SessionEvent e => OnSession(e.SessionId),
            TickEvent => _machine.OnTick(),
            StatusEvent e => OnStatus(e),
            HeartbeatEvent e => _machine.OnHeartbeat(e.ProcessId),
            ExitedEvent e => OnExited(e),
            _ => Array.Empty<SupervisorAction>(),
        };
        Execute(actions);
        Publish();
    }

    private IReadOnlyList<SupervisorAction> OnSession(uint sessionId)
    {
        _logger.LogInformation("Active console session: {Session}", sessionId == NativeMethods.NoConsoleSession ? "none" : sessionId);
        return _machine.OnConsoleSessionChanged(sessionId);
    }

    private IReadOnlyList<SupervisorAction> OnStatus(StatusEvent e)
    {
        if (_agent?.ProcessId != e.ProcessId)
        {
            return Array.Empty<SupervisorAction>();
        }

        if (_lastStatus is null || _lastStatus.State != e.Status.State)
        {
            _logger.LogInformation("Agent {Pid} reports {State} (device {Id}, desktop {Desktop}, unattended ready: {Ready})",
                e.ProcessId, e.Status.State, e.Status.DeviceId, e.Status.InputDesktop, e.Status.UnattendedReady);
        }

        _lastStatus = e.Status;
        return _machine.OnAgentStatus(e.ProcessId);
    }

    private IReadOnlyList<SupervisorAction> OnExited(ExitedEvent e)
    {
        if (_agent?.ProcessId == e.ProcessId)
        {
            _logger.Log(_machine.Phase is AgentPhase.Stopping or AgentPhase.Stopped ? LogLevel.Information : LogLevel.Warning,
                "Session agent {Pid} exited with code {Code}", e.ProcessId, e.ExitCode);
            _agent.Dispose();
            _agent = null;
        }

        return _machine.OnAgentExited(e.ProcessId);
    }

    private void Execute(IReadOnlyList<SupervisorAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case LaunchAgent launch:
                    Launch(launch.SessionId);
                    break;

                case StopAgent stop when _agent?.ProcessId == stop.ProcessId:
                    _logger.LogInformation("Stopping session agent {Pid}{Mode}: {Reason}", stop.ProcessId, stop.Force ? " (terminate)" : string.Empty, stop.Reason);
                    if (stop.Force)
                    {
                        _agent.Process.Kill();
                    }
                    else
                    {
                        _agent.CloseLink(); // the agent ends its sessions, releases input and exits when its pipe closes
                    }

                    break;
            }
        }
    }

    private void Launch(uint sessionId)
    {
        var problem = InstallLocationCheck.FindProblem(_installDirectory);
        if (problem is not null)
        {
            if (problem != _launchProblem)
            {
                _logger.LogError("Not starting the session agent: {Problem} Reinstall with scripts\\install-windows.ps1 -ServiceMode.", problem);
            }

            _launchProblem = problem;
            Execute(_machine.OnLaunchFailed());
            return;
        }

        AgentPipeServer pipe;
        try
        {
            pipe = AgentPipeServer.Create(ServiceModeLayout.PipeName, ServicePipe.CreateSecurity());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // FirstPipeInstance failed: some other process already owns the name. Never share it.
            LaunchFailed($"the pipe name is already in use by another process (possible pipe squatting): {ex.Message}");
            return;
        }

        AgentProcess process;
        try
        {
            process = AgentLauncher.Launch(sessionId, _agentPath);
        }
        catch (Win32Exception ex)
        {
            pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
            LaunchFailed(ex.Message);
            return;
        }

        _launchProblem = null;
        _agent = new AgentInstance(process, pipe);
        _logger.LogInformation("Started session agent {Pid} in session {Session}", process.ProcessId, sessionId);
        Execute(_machine.OnAgentLaunched(process.ProcessId));
        _ = ServeAsync(_agent);
        _ = process.Exited.ContinueWith(t => Post(new ExitedEvent(process.ProcessId, t.Result)), TaskScheduler.Default);
    }

    private void LaunchFailed(string reason)
    {
        if (reason != _launchProblem)
        {
            _logger.LogError("Could not start the session agent: {Reason}", reason);
        }

        _launchProblem = reason;
        Execute(_machine.OnLaunchFailed());
    }

    /// <summary>Accepts the agent's connection (exact PID + image path), then relays heartbeats and status as events.</summary>
    private async Task ServeAsync(AgentInstance agent)
    {
        int pid = agent.ProcessId;
        var ct = agent.Link.Token;
        try
        {
            IpcChannel channel;
            using (var accept = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                accept.CancelAfter(_machine.Policy.StartTimeout);
                channel = await agent.Pipe.AcceptAsync(pid, _agentPath, accept.Token).ConfigureAwait(false);
            }

            while (true)
            {
                var message = await channel.ReceiveAsync(ct).ConfigureAwait(false);
                switch (message)
                {
                    case null:
                        return;

                    case HeartbeatMessage heartbeat:
                        await channel.SendAsync(heartbeat, ct).ConfigureAwait(false);
                        Post(new HeartbeatEvent(pid));
                        break;

                    case AgentStatusMessage status:
                        Post(new StatusEvent(pid, status));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IpcPeerRejectedException ex)
        {
            _logger.LogWarning("Rejected a connection on the agent pipe: {Message}", ex.Message);
        }
        catch (IpcProtocolException ex)
        {
            _logger.LogWarning("Agent {Pid} broke the IPC protocol ({Message}); closing its pipe", pid, ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogInformation("Agent {Pid} pipe closed: {Message}", pid, ex.Message);
        }
        finally
        {
            await agent.Pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ShutdownAsync()
    {
        Execute(_machine.OnServiceStopping());
        var deadline = DateTime.UtcNow + _machine.Policy.StopTimeout + TimeSpan.FromSeconds(2);
        while (_agent is not null && DateTime.UtcNow < deadline)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                Handle(await _events.Reader.ReadAsync(wait.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                Execute(_machine.OnTick());
            }
        }

        if (_agent is not null)
        {
            _agent.Process.Kill();
            _agent.Dispose();
            _agent = null;
        }

        Publish();
    }

    private void Publish()
    {
        string state = _machine.Phase switch
        {
            AgentPhase.Idle => _machine.ConsoleSession is null ? "WaitingForConsole" : "Idle",
            AgentPhase.Starting => "Starting",
            AgentPhase.Running => _lastStatus?.State == AgentState.Degraded ? "Degraded" : "Ready",
            AgentPhase.Stopping => "Switching",
            AgentPhase.Backoff => _launchProblem is not null ? "Blocked" : "Restarting",
            _ => "Stopped",
        };
        var status = new ServicePublicStatus(state, _machine.ConsoleSession, _lastStatus?.DeviceId, _lastStatus?.UnattendedReady ?? false,
            _machine.Phase == AgentPhase.Running ? _lastStatus?.InputDesktop : null, DateTimeOffset.UtcNow);
        if (_published is not null && _published with { Updated = status.Updated } == status)
        {
            return;
        }

        try
        {
            ServiceStatusRegistry.Write(status);
            _published = status;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _logger.LogWarning("Could not publish service status: {Message}", ex.Message);
        }
    }

    private abstract record SupervisorEvent;

    private sealed record SessionEvent(uint SessionId) : SupervisorEvent;

    private sealed record TickEvent : SupervisorEvent;

    private sealed record StatusEvent(int ProcessId, AgentStatusMessage Status) : SupervisorEvent;

    private sealed record HeartbeatEvent(int ProcessId) : SupervisorEvent;

    private sealed record ExitedEvent(int ProcessId, int ExitCode) : SupervisorEvent;

    private sealed class AgentInstance(AgentProcess process, AgentPipeServer pipe) : IDisposable
    {
        public AgentProcess Process { get; } = process;

        public AgentPipeServer Pipe { get; } = pipe;

        /// <summary>Cancelled to close the pipe (graceful stop).</summary>
        public CancellationTokenSource Link { get; } = new();

        public int ProcessId => Process.ProcessId;

        public void CloseLink() => Link.Cancel();

        public void Dispose()
        {
            Link.Cancel();
            Process.Dispose();
        }
    }
}
