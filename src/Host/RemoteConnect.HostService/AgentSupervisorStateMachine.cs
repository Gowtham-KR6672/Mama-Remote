namespace RemoteConnect.HostService;

public enum AgentPhase
{
    /// <summary>No agent and nothing scheduled (no console session yet).</summary>
    Idle,

    /// <summary>Agent launched; waiting for its first status report over the pipe.</summary>
    Starting,

    /// <summary>Agent reported in and sends heartbeats.</summary>
    Running,

    /// <summary>Agent asked to exit (console session switched, or it misbehaved); waiting for the process to end.</summary>
    Stopping,

    /// <summary>Agent crashed or could not start; a relaunch is scheduled at <see cref="AgentSupervisorStateMachine.RestartAt"/>.</summary>
    Backoff,

    /// <summary>The service is stopping: no further launches.</summary>
    Stopped,
}

public abstract record SupervisorAction;

/// <summary>Start the session agent in <paramref name="SessionId"/> (the executable and arguments are fixed).</summary>
public sealed record LaunchAgent(uint SessionId) : SupervisorAction;

/// <summary>Graceful: close the agent's pipe (it ends its sessions and exits). Forced: terminate the process.</summary>
public sealed record StopAgent(int ProcessId, bool Force, string Reason) : SupervisorAction;

public sealed record SupervisorPolicy
{
    /// <summary>From launch to the agent's first status message (the agent starts a UI framework, so be generous).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a graceful stop may take before the process is terminated.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>An agent that ran this long before crashing starts a fresh backoff sequence.</summary>
    public TimeSpan StableRunTime { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Decides when the session agent runs and in which session. Pure logic without Win32: the service feeds it events
/// (console session changes, launches, status/heartbeats, exits, clock ticks) and executes the actions it returns,
/// which keeps the session-switch and crash-restart rules unit-testable.
/// <list type="bullet">
/// <item>The agent always follows the active console session; session 0 and "no session" are never targets.</item>
/// <item>A transient "no console session" (during a switch) leaves a running agent alone.</item>
/// <item>On a switch the old agent is stopped first (it holds the LAN port), then the new one is launched at once.</item>
/// <item>Crashes, failed launches, silent or hung agents are relaunched with exponential backoff.</item>
/// </list>
/// </summary>
public sealed class AgentSupervisorStateMachine
{
    private const uint NoSession = 0xFFFFFFFF;
    private static readonly IReadOnlyList<SupervisorAction> None = Array.Empty<SupervisorAction>();

    private readonly TimeProvider _time;
    private DateTimeOffset _phaseSince;
    private DateTimeOffset _lastHeartbeat;
    private DateTimeOffset _runningSince;
    private bool _stopIsFailure;
    private bool _forceSent;

    public AgentSupervisorStateMachine(TimeProvider? time = null, SupervisorPolicy? policy = null)
    {
        _time = time ?? TimeProvider.System;
        Policy = policy ?? new SupervisorPolicy();
        _phaseSince = Now;
    }

    public SupervisorPolicy Policy { get; }

    public AgentPhase Phase { get; private set; } = AgentPhase.Idle;

    /// <summary>The active console session the agent should serve (null when there is none).</summary>
    public uint? ConsoleSession { get; private set; }

    /// <summary>Session of the current (or launching) agent.</summary>
    public uint? AgentSession { get; private set; }

    public int? AgentProcessId { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? RestartAt { get; private set; }

    private DateTimeOffset Now => _time.GetUtcNow();

    public static bool IsUsableSession(uint? sessionId) => sessionId is { } s && s != 0 && s != NoSession;

    public IReadOnlyList<SupervisorAction> OnConsoleSessionChanged(uint? sessionId)
    {
        uint? target = IsUsableSession(sessionId) ? sessionId : null;
        bool changed = target != ConsoleSession;
        ConsoleSession = target;
        switch (Phase)
        {
            case AgentPhase.Idle:
                return target is uint s ? Launch(s) : None;

            case AgentPhase.Backoff:
                if (!changed || target is not uint next)
                {
                    return None;
                }

                // Someone else is at the console now: start fresh instead of waiting out a crash backoff.
                ConsecutiveFailures = 0;
                return Launch(next);

            case AgentPhase.Starting:
            case AgentPhase.Running:
                return target is null || target == AgentSession ? None : BeginStop("console session changed", failure: false, force: false);

            default:
                return None; // Stopping: the exit handler launches into the latest console session. Stopped: nothing.
        }
    }

    public IReadOnlyList<SupervisorAction> OnAgentLaunched(int processId)
    {
        if (Phase == AgentPhase.Starting && AgentProcessId is null)
        {
            AgentProcessId = processId;
            _lastHeartbeat = Now;
        }

        return None;
    }

    public IReadOnlyList<SupervisorAction> OnLaunchFailed()
    {
        if (Phase != AgentPhase.Starting)
        {
            return None;
        }

        AgentProcessId = null;
        AgentSession = null;
        return ScheduleRestart();
    }

    /// <summary>The agent's (validated) status arrived over its verified pipe.</summary>
    public IReadOnlyList<SupervisorAction> OnAgentStatus(int processId)
    {
        if (processId != AgentProcessId || Phase is not (AgentPhase.Starting or AgentPhase.Running))
        {
            return None;
        }

        _lastHeartbeat = Now;
        if (Phase == AgentPhase.Starting)
        {
            Enter(AgentPhase.Running);
            _runningSince = Now;
        }

        return None;
    }

    public IReadOnlyList<SupervisorAction> OnHeartbeat(int processId)
    {
        if (processId == AgentProcessId && Phase is AgentPhase.Starting or AgentPhase.Running)
        {
            _lastHeartbeat = Now;
        }

        return None;
    }

    public IReadOnlyList<SupervisorAction> OnAgentExited(int processId)
    {
        if (processId != AgentProcessId)
        {
            return None; // an agent we already gave up on
        }

        var phase = Phase;
        bool ranStably = phase == AgentPhase.Running && Now - _runningSince >= Policy.StableRunTime;
        AgentProcessId = null;
        AgentSession = null;
        _forceSent = false;
        switch (phase)
        {
            case AgentPhase.Stopped:
                return None;

            case AgentPhase.Stopping when !_stopIsFailure:
                return ConsoleSession is uint s ? Launch(s) : Enter(AgentPhase.Idle);

            case AgentPhase.Stopping:
                return ScheduleRestart();

            default:
                // Exited on its own: a crash, or its session ended (sign-out). Either way, relaunch with backoff.
                if (ranStably)
                {
                    ConsecutiveFailures = 0;
                }

                return ScheduleRestart();
        }
    }

    public IReadOnlyList<SupervisorAction> OnServiceStopping()
    {
        if (Phase == AgentPhase.Stopped)
        {
            return None;
        }

        var wasStopping = Phase == AgentPhase.Stopping;
        Enter(AgentPhase.Stopped);
        RestartAt = null;
        if (AgentProcessId is not int pid)
        {
            return None;
        }

        if (wasStopping)
        {
            return None; // a stop is already under way; the tick still escalates to a forced stop
        }

        _forceSent = false;
        return new[] { new StopAgent(pid, Force: false, "service stopping") };
    }

    public IReadOnlyList<SupervisorAction> OnTick()
    {
        var now = Now;
        switch (Phase)
        {
            case AgentPhase.Starting when AgentProcessId is not null && now - _phaseSince > Policy.StartTimeout:
                return BeginStop("the agent did not report in time", failure: true, force: true);

            case AgentPhase.Running when now - _lastHeartbeat > Policy.HeartbeatTimeout:
                return BeginStop("the agent stopped sending heartbeats", failure: true, force: true);

            case AgentPhase.Stopping or AgentPhase.Stopped
                when AgentProcessId is int pid && !_forceSent && now - _phaseSince > Policy.StopTimeout:
                _forceSent = true;
                return new[] { new StopAgent(pid, Force: true, "the agent did not exit in time") };

            case AgentPhase.Backoff when RestartAt is { } at && now >= at:
                RestartAt = null;
                return ConsoleSession is uint s ? Launch(s) : Enter(AgentPhase.Idle);

            default:
                return None;
        }
    }

    /// <summary>Delay before relaunch attempt number <paramref name="failures"/> (1-based): 2 s, 4 s, 8 s … capped.</summary>
    public TimeSpan BackoffFor(int failures)
    {
        double factor = Math.Pow(2, Math.Clamp(failures - 1, 0, 16));
        var delay = TimeSpan.FromTicks((long)Math.Min(Policy.InitialBackoff.Ticks * factor, Policy.MaxBackoff.Ticks));
        return delay < Policy.InitialBackoff ? Policy.InitialBackoff : delay;
    }

    private IReadOnlyList<SupervisorAction> Launch(uint sessionId)
    {
        Enter(AgentPhase.Starting);
        AgentSession = sessionId;
        AgentProcessId = null;
        RestartAt = null;
        _lastHeartbeat = Now;
        return new[] { new LaunchAgent(sessionId) };
    }

    private IReadOnlyList<SupervisorAction> BeginStop(string reason, bool failure, bool force)
    {
        if (AgentProcessId is not int pid)
        {
            // Launch requested but no process yet (cannot happen with the service's synchronous launch); start over.
            AgentSession = null;
            return failure ? ScheduleRestart() : ConsoleSession is uint s ? Launch(s) : Enter(AgentPhase.Idle);
        }

        Enter(AgentPhase.Stopping);
        _stopIsFailure = failure;
        _forceSent = force;
        return new[] { new StopAgent(pid, force, reason) };
    }

    private IReadOnlyList<SupervisorAction> ScheduleRestart()
    {
        ConsecutiveFailures++;
        Enter(AgentPhase.Backoff);
        RestartAt = Now + BackoffFor(ConsecutiveFailures);
        return None;
    }

    private IReadOnlyList<SupervisorAction> Enter(AgentPhase phase)
    {
        Phase = phase;
        _phaseSince = Now;
        return None;
    }
}
