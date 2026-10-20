using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RemoteConnect.HostService;
using RemoteConnect.LocalIpc;
using RemoteConnect.Security;

namespace RemoteConnect.UnitTests;

/// <summary>Service mode: the service ↔ agent message validation and the agent supervisor's state machine.</summary>
public class IpcMessageTests
{
    private static readonly string Fingerprint = Convert.ToHexString(SHA256.HashData("service-mode test key"u8));
    private static readonly string DeviceId = DeviceIds.TryFromFingerprint(Fingerprint, out var id) ? id : throw new InvalidOperationException();

    internal static AgentStatusMessage ValidStatus() => new(
        AgentState.Ready, DeviceId, Fingerprint, "Winlogon", 47070, AgentServerStatus.Online, 1, UnattendedReady: true, "0.2.0");

    private static IpcMessage RoundTrip(IpcMessage message, IpcSender sender)
    {
        var frame = IpcCodec.Encode(message, sender);
        int length = IpcCodec.ReadHeader(frame.AsSpan(0, IpcCodec.HeaderSize), out var type);
        Assert.Equal(frame.Length - IpcCodec.HeaderSize, length);
        return IpcCodec.Decode(type, frame.AsSpan(IpcCodec.HeaderSize), sender);
    }

    private static byte[] Header(uint length, byte type)
    {
        var header = new byte[IpcCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        header[4] = type;
        return header;
    }

    private static IpcMessage DecodeJson(IpcMessageType type, string json, IpcSender sender = IpcSender.Agent)
        => IpcCodec.Decode(type, Encoding.UTF8.GetBytes(json), sender);

    [Fact]
    public void Heartbeat_and_status_round_trip()
    {
        Assert.Equal(new HeartbeatMessage(42), RoundTrip(new HeartbeatMessage(42), IpcSender.Service));
        Assert.Equal(new HeartbeatMessage(7), RoundTrip(new HeartbeatMessage(7), IpcSender.Agent));
        Assert.Equal(ValidStatus(), RoundTrip(ValidStatus(), IpcSender.Agent));
        Assert.Equal(ValidStatus() with { LanPort = null, InputDesktop = string.Empty }, RoundTrip(ValidStatus() with { LanPort = null, InputDesktop = string.Empty }, IpcSender.Agent));
    }

    [Fact]
    public void Status_may_only_come_from_the_agent()
    {
        Assert.Throws<IpcProtocolException>(() => IpcCodec.Encode(ValidStatus(), IpcSender.Service));
        var payload = IpcCodec.Encode(ValidStatus(), IpcSender.Agent).AsSpan(IpcCodec.HeaderSize).ToArray();
        Assert.Throws<IpcProtocolException>(() => IpcCodec.Decode(IpcMessageType.Status, payload, IpcSender.Service));
    }

    [Theory]
    [InlineData(0u, 1)]                              // empty
    [InlineData((uint)IpcCodec.MaxPayloadBytes + 1, 1)] // too large
    [InlineData(uint.MaxValue, 1)]                   // absurd length
    [InlineData(10u, 0)]                             // type 0 does not exist
    [InlineData(10u, 3)]                             // nor does 3 (no command messages)
    [InlineData(10u, 255)]
    public void Header_rejects_bad_length_or_unknown_type(uint length, int type)
        => Assert.Throws<IpcProtocolException>(() => IpcCodec.ReadHeader(Header(length, (byte)type), out _));

    [Theory]
    [InlineData("{\"sequence\":-1}")]                         // negative
    [InlineData("{}")]                                        // missing field
    [InlineData("{\"sequence\":1,\"command\":\"cmd.exe\"}")]  // unknown field
    [InlineData("{\"sequence\":\"1\"}")]                      // wrong type
    [InlineData("{\"sequence\":1,}")]                         // trailing comma
    [InlineData("{\"sequence\":1 /* x */}")]                  // comment
    [InlineData("[1]")]
    [InlineData("not json")]
    [InlineData("null")]
    public void Malformed_heartbeats_are_rejected(string json)
        => Assert.Throws<IpcProtocolException>(() => DecodeJson(IpcMessageType.Heartbeat, json));

    public static IEnumerable<object[]> InvalidStatuses()
    {
        var ok = ValidStatus();
        yield return new object[] { ok with { DeviceId = "12345" } };
        yield return new object[] { ok with { DeviceId = "012345678" } };
        yield return new object[] { ok with { DeviceId = DeviceId == "111111111" ? "222222222" : "111111111" } }; // not derived from the key
        yield return new object[] { ok with { Fingerprint = Fingerprint.ToLowerInvariant() } };
        yield return new object[] { ok with { Fingerprint = Fingerprint[..63] } };
        yield return new object[] { ok with { InputDesktop = "Winlogon\\..\\x" } };
        yield return new object[] { ok with { InputDesktop = new string('a', IpcCodec.MaxTextLength + 1) } };
        yield return new object[] { ok with { AgentVersion = string.Empty } };
        yield return new object[] { ok with { AgentVersion = "1.0; rm -rf" } };
        yield return new object[] { ok with { LanPort = 0 } };
        yield return new object[] { ok with { LanPort = 70000 } };
        yield return new object[] { ok with { ActiveSessions = -1 } };
        yield return new object[] { ok with { ActiveSessions = IpcCodec.MaxActiveSessions + 1 } };
        yield return new object[] { ok with { State = (AgentState)99 } };
        yield return new object[] { ok with { Server = (AgentServerStatus)99 } };
    }

    [Theory]
    [MemberData(nameof(InvalidStatuses))]
    public void Invalid_status_values_are_rejected_on_both_ends(AgentStatusMessage status)
    {
        Assert.Throws<IpcProtocolException>(() => IpcCodec.Encode(status, IpcSender.Agent));
        Assert.Throws<IpcProtocolException>(() => IpcCodec.Validate(status, IpcSender.Agent));
    }

    [Fact]
    public void Status_json_must_use_named_enums_and_every_field()
    {
        var json = Encoding.UTF8.GetString(IpcCodec.Encode(ValidStatus(), IpcSender.Agent).AsSpan(IpcCodec.HeaderSize));
        Assert.Contains("\"state\":\"ready\"", json);
        Assert.IsType<AgentStatusMessage>(DecodeJson(IpcMessageType.Status, json));

        Assert.Throws<IpcProtocolException>(() => DecodeJson(IpcMessageType.Status, json.Replace("\"state\":\"ready\"", "\"state\":1")));
        Assert.Throws<IpcProtocolException>(() => DecodeJson(IpcMessageType.Status, json.Replace("\"state\":\"ready\"", "\"state\":\"admin\"")));
        Assert.Throws<IpcProtocolException>(() => DecodeJson(IpcMessageType.Status, json.Replace(",\"agentVersion\":\"0.2.0\"", string.Empty)));
        Assert.Throws<IpcProtocolException>(() => DecodeJson(IpcMessageType.Status, json.Replace("{", "{\"run\":\"calc.exe\",")));
        Assert.Throws<IpcProtocolException>(() => DecodeJson(IpcMessageType.Heartbeat, json)); // a status is not a heartbeat
    }

    [Fact]
    public async Task Channel_reads_frames_until_clean_end_and_rejects_truncation()
    {
        var stream = new MemoryStream();
        stream.Write(IpcCodec.Encode(new HeartbeatMessage(1), IpcSender.Agent));
        stream.Write(IpcCodec.Encode(ValidStatus(), IpcSender.Agent));
        stream.Position = 0;
        await using (var channel = new IpcChannel(stream, IpcSender.Service, peerProcessId: 1))
        {
            Assert.Equal(new HeartbeatMessage(1), await channel.ReceiveAsync(default));
            Assert.Equal(ValidStatus(), await channel.ReceiveAsync(default));
            Assert.Null(await channel.ReceiveAsync(default)); // pipe closed
        }

        var frame = IpcCodec.Encode(ValidStatus(), IpcSender.Agent);
        await using (var truncated = new IpcChannel(new MemoryStream(frame[..^3]), IpcSender.Service, 1))
        {
            await Assert.ThrowsAsync<IpcProtocolException>(() => truncated.ReceiveAsync(default));
        }

        await using (var halfHeader = new IpcChannel(new MemoryStream(frame[..2]), IpcSender.Service, 1))
        {
            await Assert.ThrowsAsync<IpcProtocolException>(() => halfHeader.ReceiveAsync(default));
        }

        // The service receiving a status that claims to come from the service's direction is impossible by construction;
        // an agent-side channel must refuse a status frame (only the agent sends status).
        await using var agentSide = new IpcChannel(new MemoryStream(frame), IpcSender.Agent, 1);
        await Assert.ThrowsAsync<IpcProtocolException>(() => agentSide.ReceiveAsync(default));
    }

    [Fact]
    public void Service_settings_fail_closed()
    {
        var dir = Directory.CreateTempSubdirectory("rc-service-settings").FullName;
        try
        {
            var path = Path.Combine(dir, "service-settings.json");
            var defaults = ServiceModeSettingsFile.Load(path, out var error);
            Assert.Null(error);
            Assert.False(defaults.UnattendedReady);

            File.WriteAllText(path, "{ \"unattendedEnabled\": true, ");
            var broken = ServiceModeSettingsFile.Load(path, out error);
            Assert.NotNull(error);
            Assert.False(broken.UnattendedEnabled);

            File.WriteAllText(path, "{ \"unattendedEnabled\": true, \"lanPort\": 99999, \"unattendedAllowList\": [\"482 913 207\", \"nope\"] }");
            var normalized = ServiceModeSettingsFile.Load(path, out error);
            Assert.Null(error);
            Assert.False(normalized.UnattendedEnabled); // enabled without a password means off
            Assert.Equal(47070, normalized.LanPort);
            Assert.Equal(new[] { "482913207" }, normalized.UnattendedAllowList);

            var settings = new ServiceModeSettings { UnattendedEnabled = true, UnattendedPasswordHash = PasswordHasher.Hash("correct horse"), Alias = "  Office PC  " };
            ServiceModeSettingsFile.Save(path, settings);
            var loaded = ServiceModeSettingsFile.Load(path, out error);
            Assert.Null(error);
            Assert.True(loaded.UnattendedReady);
            Assert.Equal("Office PC", loaded.Alias);
            Assert.DoesNotContain("unattendedReady", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class AgentSupervisorTests
{
    private const int Pid = 4100;

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualTime _time = new();

    private AgentSupervisorStateMachine NewMachine() => new(_time);

    /// <summary>Boots into <paramref name="session"/> and brings the agent to Running.</summary>
    private AgentSupervisorStateMachine Running(uint session = 1, int pid = Pid)
    {
        var m = NewMachine();
        Assert.Equal(new LaunchAgent(session), Assert.Single(m.OnConsoleSessionChanged(session)));
        m.OnAgentLaunched(pid);
        m.OnAgentStatus(pid);
        Assert.Equal(AgentPhase.Running, m.Phase);
        return m;
    }

    [Fact]
    public void Boot_launches_the_agent_into_the_console_session()
    {
        var m = NewMachine();
        var action = Assert.Single(m.OnConsoleSessionChanged(1));
        Assert.Equal(new LaunchAgent(1), action);
        Assert.Equal(AgentPhase.Starting, m.Phase);

        m.OnAgentLaunched(Pid);
        m.OnHeartbeat(Pid);
        Assert.Equal(AgentPhase.Starting, m.Phase); // a heartbeat is not a status report
        m.OnAgentStatus(Pid);
        Assert.Equal(AgentPhase.Running, m.Phase);
        Assert.Equal(1u, m.AgentSession);
    }

    [Theory]
    [InlineData(0u)]          // services' session
    [InlineData(0xFFFFFFFFu)] // no session attached to the console
    public void Never_launches_into_session_zero_or_no_session(uint session)
    {
        var m = NewMachine();
        Assert.Empty(m.OnConsoleSessionChanged(session));
        Assert.Empty(m.OnConsoleSessionChanged(null));
        _time.Now += TimeSpan.FromMinutes(5);
        Assert.Empty(m.OnTick());
        Assert.Equal(AgentPhase.Idle, m.Phase);
    }

    [Fact]
    public void Session_switch_stops_the_old_agent_first_then_launches_in_the_new_session()
    {
        var m = Running(session: 1);
        var stop = Assert.IsType<StopAgent>(Assert.Single(m.OnConsoleSessionChanged(2)));
        Assert.Equal(Pid, stop.ProcessId);
        Assert.False(stop.Force); // graceful: the agent ends its sessions and releases input
        Assert.Equal(AgentPhase.Stopping, m.Phase);

        Assert.Empty(m.OnConsoleSessionChanged(3)); // switched again while stopping: the latest session wins
        Assert.Equal(new LaunchAgent(3), Assert.Single(m.OnAgentExited(Pid)));
        Assert.Equal(0, m.ConsecutiveFailures);
    }

    [Fact]
    public void Transient_loss_of_the_console_session_leaves_the_agent_running()
    {
        var m = Running(session: 1);
        Assert.Empty(m.OnConsoleSessionChanged(0xFFFFFFFF));
        Assert.Empty(m.OnConsoleSessionChanged(1));
        Assert.Equal(AgentPhase.Running, m.Phase);
    }

    [Fact]
    public void Crashes_are_relaunched_with_exponential_backoff()
    {
        var m = Running();
        var expected = new[] { 2, 4, 8, 16, 32, 60, 60 };
        int pid = Pid;
        foreach (var seconds in expected)
        {
            Assert.Empty(m.OnAgentExited(pid));
            Assert.Equal(AgentPhase.Backoff, m.Phase);
            _time.Now += TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1);
            Assert.Empty(m.OnTick());
            _time.Now += TimeSpan.FromMilliseconds(1);
            Assert.Equal(new LaunchAgent(1), Assert.Single(m.OnTick()));
            m.OnAgentLaunched(++pid);
            m.OnAgentStatus(pid);
        }

        Assert.Equal(expected.Length, m.ConsecutiveFailures);
    }

    [Fact]
    public void An_agent_that_ran_stably_starts_a_fresh_backoff()
    {
        var m = Running();
        m.OnAgentExited(Pid);
        _time.Now += TimeSpan.FromSeconds(2);
        m.OnTick();
        m.OnAgentLaunched(Pid + 1);
        m.OnAgentStatus(Pid + 1);
        Assert.Equal(1, m.ConsecutiveFailures);

        _time.Now += m.Policy.StableRunTime;
        for (int i = 0; i < 12; i++)
        {
            _time.Now += TimeSpan.FromSeconds(5);
            m.OnHeartbeat(Pid + 1);
        }

        m.OnAgentExited(Pid + 1);
        Assert.Equal(1, m.ConsecutiveFailures);
        Assert.Equal(_time.Now + TimeSpan.FromSeconds(2), m.RestartAt);
    }

    [Fact]
    public void A_silent_agent_is_terminated_and_relaunched()
    {
        var m = Running();
        _time.Now += TimeSpan.FromSeconds(20);
        m.OnHeartbeat(Pid);
        _time.Now += m.Policy.HeartbeatTimeout;
        Assert.Empty(m.OnTick());

        _time.Now += TimeSpan.FromSeconds(1);
        var stop = Assert.IsType<StopAgent>(Assert.Single(m.OnTick()));
        Assert.True(stop.Force);
        Assert.Empty(m.OnAgentExited(Pid));
        Assert.Equal(AgentPhase.Backoff, m.Phase);
        Assert.Equal(1, m.ConsecutiveFailures);
    }

    [Fact]
    public void An_agent_that_never_reports_is_terminated()
    {
        var m = NewMachine();
        m.OnConsoleSessionChanged(1);
        m.OnAgentLaunched(Pid);
        _time.Now += m.Policy.StartTimeout + TimeSpan.FromSeconds(1);
        var stop = Assert.IsType<StopAgent>(Assert.Single(m.OnTick()));
        Assert.True(stop.Force);
        m.OnAgentExited(Pid);
        Assert.Equal(AgentPhase.Backoff, m.Phase);
    }

    [Fact]
    public void A_graceful_stop_that_hangs_is_escalated_once()
    {
        var m = Running(session: 1);
        m.OnConsoleSessionChanged(2);
        _time.Now += m.Policy.StopTimeout + TimeSpan.FromSeconds(1);
        var force = Assert.IsType<StopAgent>(Assert.Single(m.OnTick()));
        Assert.True(force.Force);
        Assert.Empty(m.OnTick());
        Assert.Equal(new LaunchAgent(2), Assert.Single(m.OnAgentExited(Pid)));
    }

    [Fact]
    public void Failed_launch_backs_off_and_a_new_session_retries_at_once()
    {
        var m = NewMachine();
        m.OnConsoleSessionChanged(1);
        Assert.Empty(m.OnLaunchFailed());
        Assert.Equal(AgentPhase.Backoff, m.Phase);
        Assert.Equal(1, m.ConsecutiveFailures);

        Assert.Equal(new LaunchAgent(2), Assert.Single(m.OnConsoleSessionChanged(2)));
        Assert.Equal(0, m.ConsecutiveFailures);
    }

    [Fact]
    public void Service_stop_ends_the_agent_and_launches_nothing_more()
    {
        var m = Running();
        var stop = Assert.IsType<StopAgent>(Assert.Single(m.OnServiceStopping()));
        Assert.False(stop.Force);
        Assert.Empty(m.OnConsoleSessionChanged(2));
        _time.Now += m.Policy.StopTimeout + TimeSpan.FromSeconds(1);
        Assert.True(Assert.IsType<StopAgent>(Assert.Single(m.OnTick())).Force);
        Assert.Empty(m.OnAgentExited(Pid));
        _time.Now += TimeSpan.FromMinutes(5);
        Assert.Empty(m.OnTick());
        Assert.Equal(AgentPhase.Stopped, m.Phase);
    }

    [Fact]
    public void Events_from_an_old_agent_are_ignored()
    {
        var m = Running(session: 1);
        m.OnConsoleSessionChanged(2);
        m.OnAgentExited(Pid);
        m.OnAgentLaunched(Pid + 1);

        Assert.Empty(m.OnAgentExited(Pid));
        Assert.Empty(m.OnAgentStatus(Pid));
        Assert.Equal(AgentPhase.Starting, m.Phase);
        Assert.Equal(Pid + 1, m.AgentProcessId);
        Assert.Equal(2u, m.AgentSession);
    }

    [Fact]
    public void Sign_out_ends_the_agent_and_the_next_console_session_gets_a_new_one()
    {
        // Signing out ends the session (and the agent with it); Windows then shows the sign-in screen in a new session.
        var m = Running(session: 1);
        Assert.Empty(m.OnAgentExited(Pid));
        Assert.Equal(new LaunchAgent(3), Assert.Single(m.OnConsoleSessionChanged(3)));
    }
}
