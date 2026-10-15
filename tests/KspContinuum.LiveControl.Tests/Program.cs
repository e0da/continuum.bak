using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KspContinuum;

static class Program
{
    static int assertions;
    static readonly string Session = Guid.NewGuid().ToString("D");
    static readonly string Vessel = Guid.NewGuid().ToString("D");

    static void Check(bool value, string reason)
    {
        assertions++;
        if (!value) throw new Exception(reason);
    }

    static LiveVesselSnapshot VesselSample() => new LiveVesselSnapshot {
        vesselId = Vessel, name = "Test craft", body = "Minmus", situation = "LANDED",
        loaded = true, packed = false, parts = 14, universalTime = 100,
        altitude = 5, surfaceSpeed = 0, orbitalSpeed = 8, throttle = 0
    };

    static LiveControlReply Execute(LiveControlPlane control, string command, long frame, long tick,
        Func<LiveVesselSnapshot> capture) => control.Execute(LiveControlRequest.Parse(command), frame, tick, capture);

    static string Exchange(LoopbackSnapshotServer server, string command, Func<string, string> handle, bool pump)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", server.Port);
        client.ReceiveTimeout = 5000;
        using NetworkStream stream = client.GetStream();
        byte[] input = Encoding.ASCII.GetBytes(command + "\n");
        stream.Write(input);
        Task worker = pump ? Task.Run(() => {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (!server.DrainOne(handle))
            {
                deadline.Token.ThrowIfCancellationRequested();
                Thread.Yield();
            }
        }) : Task.CompletedTask;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string line = reader.ReadLine() ?? throw new Exception("Missing reply");
        worker.GetAwaiter().GetResult();
        return line;
    }

    static void PersistentConnection(LoopbackSnapshotServer server, LiveControlPlane control,
        Func<LiveVesselSnapshot> capture)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", server.Port);
        client.ReceiveTimeout = 5000;
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Task pump = Task.Run(() => {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            int handled = 0;
            while (handled < 3)
            {
                if (server.DrainOne(command => {
                    if (LiveControlRequest.Parse(command).requestId == "persisted2")
                        throw new InvalidOperationException("Simulated main-thread failure");
                    return ReportJson.Encode(Execute(control, command, 30 + handled, 8, capture));
                })) handled++;
                else { deadline.Token.ThrowIfCancellationRequested(); Thread.Yield(); }
            }
        });
        foreach (string command in new[] { "hello 1.0.0 persisted1", "snapshot 1.0.0 persisted2 " + Session + " 6 7",
            "snapshot 1.0.0 persisted3 " + Session + " 6 7" })
        {
            byte[] bytes = Encoding.ASCII.GetBytes(command + "\n");
            stream.Write(bytes);
            using var json = JsonDocument.Parse(reader.ReadLine() ?? throw new Exception("Missing persistent reply"));
            Check(json.RootElement.GetProperty("requestId").GetString() == command.Split(' ')[2], "persistent request correlation");
            if (command.Contains("persisted2", StringComparison.Ordinal))
                Check(json.RootElement.GetProperty("reason").GetString() == "handler-failed",
                    "handler failure stays correlated");
            else if (command.StartsWith("snapshot", StringComparison.Ordinal))
                Check(json.RootElement.GetProperty("snapshot").GetProperty("parts").GetInt32() == 14,
                    "persistent snapshot reached source");
        }
        pump.GetAwaiter().GetResult();
    }

    static void PushedSweepConnection(LoopbackSnapshotServer server, LiveControlPlane control,
        Func<LiveVesselSnapshot> capture)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", server.Port);
        client.ReceiveTimeout = 5000;
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Task pump = Task.Run(() => {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            int handled = 0;
            while (handled < 2)
            {
                if (server.DrainOne(command => {
                    LiveControlRequest request = LiveControlRequest.Parse(command);
                    LiveControlReply reply = control.Execute(request, 40 + handled, 9, capture,
                        null, () => new LiveSweepStatus { state = "running", window = "stock-01", windowIndex = 0 });
                    if (request.operation == LiveControlOperation.SubscribeSweep && reply.status == "ok")
                    {
                        server.EnablePush();
                        server.PublishLatest(ReportJson.Encode(control.CreateSweepObservation(
                            new LiveSweepStatus { state = "running", window = "clear-cache-04", windowIndex = 1 }, 41, 9)), false);
                        server.PublishLatest(ReportJson.Encode(control.CreateSweepObservation(
                            new LiveSweepStatus { state = "running", window = "stock-02", windowIndex = 4 }, 42, 10)), false);
                        server.PublishLatest(ReportJson.Encode(control.CreateSweepObservation(
                            new LiveSweepStatus { state = "complete", directory = "/runtime/report",
                                window = "full-publication-02", windowIndex = 5 }, 43, 11)), true);
                        server.PublishLatest(ReportJson.Encode(control.CreateSweepObservation(
                            new LiveSweepStatus { state = "running", window = "new-run", windowIndex = 0 }, 44, 12)), false);
                    }
                    handled++;
                    return ReportJson.Encode(reply);
                })) { }
                else { deadline.Token.ThrowIfCancellationRequested(); Thread.Yield(); }
            }
        });
        byte[] subscribe = Encoding.ASCII.GetBytes(
            "sweep-subscribe 1.2.0 pushed1 " + Session + " 6 dry-buoyancy\n");
        stream.Write(subscribe);
        using (var reply = JsonDocument.Parse(reader.ReadLine() ?? throw new Exception("Missing subscribe reply")))
            Check(reply.RootElement.GetProperty("requestId").GetString() == "pushed1" &&
                reply.RootElement.GetProperty("status").GetString() == "ok", "subscription reply precedes observations");
        using (var observation = JsonDocument.Parse(reader.ReadLine() ?? throw new Exception("Missing pushed observation")))
            Check(observation.RootElement.GetProperty("status").GetString() == "observation" &&
                observation.RootElement.GetProperty("sweep").GetProperty("state").GetString() == "complete" &&
                observation.RootElement.GetProperty("identity").GetProperty("observedFixedCallbacks").GetInt64() == 11,
                "slow consumer receives coalesced terminal observation");
        byte[] hello = Encoding.ASCII.GetBytes("hello 1.2.0 pushed2\n");
        stream.Write(hello);
        using (var reply = JsonDocument.Parse(reader.ReadLine() ?? throw new Exception("Missing duplex reply")))
            Check(reply.RootElement.GetProperty("requestId").GetString() == "pushed2" &&
                reply.RootElement.GetProperty("status").GetString() == "ok", "command reply remains correlated after push");
        pump.GetAwaiter().GetResult();
    }

    static void QueuedTimeoutCannotExecute()
    {
        using var server = new LoopbackSnapshotServer(0, 100, 250);
        int effects = 0;
        string line = Exchange(server, "hello 1.0.0 queued-timeout", _ => { effects++; return "unexpected"; }, false);
        using var reply = JsonDocument.Parse(line);
        Check(reply.RootElement.GetProperty("status").GetString() == "rejected" &&
            reply.RootElement.GetProperty("reason").GetString() == "game-thread-timeout-cancelled-before-execution" &&
            reply.RootElement.GetProperty("requestId").GetString() == "queued-timeout", "queued timeout explicitly cancels its correlated request");
        Check(!server.DrainOne(_ => { effects++; return "unexpected"; }) && effects == 0,
            "timed-out queued request never executes when the game thread resumes");
    }

    static void StartedTimeoutRetainsExecution()
    {
        using var server = new LoopbackSnapshotServer(0, 100, 250);
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var client = new TcpClient(); client.Connect("127.0.0.1", server.Port); client.ReceiveTimeout = 2000;
        using NetworkStream stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.UTF8);
        int effects = 0;
        Task pump = Task.Run(() => {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!server.DrainOne(command => {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(2))) throw new Exception("Started handler gate was not released");
                Interlocked.Increment(ref effects);
                return ReportJson.Encode(new LiveControlReply { status = "ok", requestId = LiveControlRequest.Parse(command).requestId });
            })) { deadline.Token.ThrowIfCancellationRequested(); Thread.Yield(); }
        });
        try
        {
            byte[] input = Encoding.ASCII.GetBytes("clock-enable 1.4.0 started-timeout " + Session + " 1\n"); stream.Write(input);
            Check(started.Wait(TimeSpan.FromSeconds(1)), "game thread started the controlled handler");
            using var reply = JsonDocument.Parse(reader.ReadLine() ?? throw new Exception("Missing started-timeout reply"));
            Check(reply.RootElement.GetProperty("status").GetString() == "in-progress" &&
                reply.RootElement.GetProperty("reason").GetString() == "handler-still-running" &&
                reply.RootElement.GetProperty("requestId").GetString() == "started-timeout", "started timeout retains request identity and reports unfinished execution");
            Check(!pump.IsCompleted && Volatile.Read(ref effects) == 0, "transport timeout does not finish or cancel the blocked handler");
        }
        finally { release.Set(); pump.GetAwaiter().GetResult(); }
        Check(effects == 1 && !server.DrainOne(_ => throw new Exception("Started operation was replayed")),
            "started request completes its original effect exactly once after timeout");
    }

    static void Main()
    {
        var workerReceipt = new OwnedFlightWorkerStatus { Ready = true, Epoch = 9,
            Progress = new OwnedFlightTimelineProgress { Stamp = new OwnedRuntimeStamp(9, 12, 2, 3, 4, .125, "receipt-frame") } };
        using (var receipt = JsonDocument.Parse(ReportJson.Encode(workerReceipt)))
        {
            var stamp = receipt.RootElement.GetProperty("Progress").GetProperty("Stamp");
            Check(stamp.GetProperty("StateRevision").GetInt64() == 12 && stamp.GetProperty("Time").GetDouble() == .125,
                "cold worker receipt preserves readonly committed state stamp");
        }
        var control = new LiveControlPlane(Session);
        control.Observe("Flight", Vessel);
        int captures = 0;
        LiveVesselSnapshot Capture() { captures++; return VesselSample(); }
        LiveControlReply hello = Execute(control, "hello 1.0.0 hello1", 20, 3, Capture);
        Check(hello.status == "ok" && hello.identity.epoch == 1 && hello.snapshot == null &&
            hello.requestId == "hello1" && hello.protocolVersion == "1.0.0", "hello identity");
        Check(Array.IndexOf(hello.capabilities, "playable-rigid-control") >= 0 && captures == 0, "live control capabilities");
        int clockCalls=0;
        LiveClockStatus Clock(LiveControlOperation operation){clockCalls++;return new LiveClockStatus{state="owned",owned=true,enabled=true,universalTime=100,publications=7,reportJson="{}"};}
        var clock=control.Execute(LiveControlRequest.Parse("clock-enable 1.4.0 clock1 "+Session+" 1"),20,3,Capture,clockOperation:Clock);
        Check(clock.status=="ok"&&clock.clock.owned&&clockCalls==1&&captures==0,"owned clock command reaches its explicit owner");
        Check(control.Execute(LiveControlRequest.Parse("clock-disable 1.4.0 staleclock "+Session+" 2"),20,3,Capture,clockOperation:Clock).reason=="stale-identity"&&clockCalls==1,"stale clock command never reaches owner");
        Check(control.Execute(LiveControlRequest.Parse("clock-warp128 1.3.0 oldclock "+Session+" 1"),20,3,Capture,clockOperation:Clock).status=="rejected"&&clockCalls==1,"owned warp requires explicit protocol");
        using(var json=JsonDocument.Parse(ReportJson.Encode(clock)))Check(json.RootElement.GetProperty("clock").GetProperty("publications").GetInt64()==7,"owned status serialization");
        int lightingCalls=0;string lightingPath=Path.Combine(Path.GetTempPath(),"continuum-lighting-facts.json");
        LiveClockStatus Lighting(LiveControlOperation operation)
        {
            Check(operation==LiveControlOperation.ClockLighting,"lighting request stays a distinct read-only operation");lightingCalls++;
            return new LiveClockStatus{state="owned",owned=true,enabled=true,universalTime=100,publications=7,lightingPath=lightingPath};
        }
        var lightingReply=control.Execute(LiveControlRequest.Parse("clock-lighting 1.4.0 lighting1 "+Session+" 1"),20,3,Capture,clockOperation:Lighting);
        Check(lightingReply.status=="ok"&&lightingReply.clock.lightingPath==lightingPath&&lightingReply.clock.owned&&lightingReply.clock.enabled&&captures==0&&lightingCalls==1,
            "lighting returns saved path without a vessel capture or ownership mutation");
        using(var json=JsonDocument.Parse(ReportJson.Encode(lightingReply)))Check(json.RootElement.GetProperty("clock").GetProperty("lightingPath").GetString()==lightingPath,"lighting path has an explicit encoded response field");
        Check(control.Execute(LiveControlRequest.Parse("clock-lighting 1.4.0 stalelight "+Session+" 2"),20,3,Capture,clockOperation:Lighting).reason=="stale-identity"&&lightingCalls==1,
            "stale lighting request never calls diagnostic producer");
        Check(control.Execute(LiveControlRequest.Parse("clock-lighting 1.3.0 oldlight "+Session+" 1"),20,3,Capture,clockOperation:Lighting).status=="rejected"&&lightingCalls==1,
            "lighting requires protocol1.4");
        Check(control.Execute(LiveControlRequest.Parse("clock-lighting 1.4.0 emptylight "+Session+" 1"),20,3,Capture,
            clockOperation:_=>new LiveClockStatus{state="owned",owned=true,enabled=true}).reason=="invalid-clock-status","lighting success cannot omit the saved path");
        var lightingFailure=control.Execute(LiveControlRequest.Parse("clock-lighting 1.4.0 failedlight "+Session+" 1"),20,3,Capture,
            clockOperation:_=>new LiveClockStatus{state="owned",owned=true,enabled=true,operationError="diagnostic-write-failed"});
        Check(lightingFailure.status=="rejected"&&lightingFailure.reason=="diagnostic-write-failed"&&lightingFailure.clock.owned&&lightingFailure.clock.enabled,
            "diagnostic failure returns error while retaining owner status");
        int rigidCalls = 0;
        LiveRigidStatus Rigid(LiveControlOperation operation)
        {
            rigidCalls++;
            return new LiveRigidStatus { state = "owned", enabled = true, owned = true, parts = 512, bodies = 426,
                sessionId = Session, vesselId = Vessel, epoch = 2, physicsSteps = 9000, renderFrames = 10000,
                framesPerSecond = 80, simulationRate = 1, mass = 64, throttle = .5f };
        }
        LiveControlReply rigid = control.Execute(LiveControlRequest.Parse("rigid-enable 1.3.0 rigid1 " + Session + " 1"),
            20, 3, Capture, rigidOperation: Rigid);
        Check(rigid.status == "ok" && rigid.rigid.owned && rigidCalls == 1 && captures == 0, "typed rigid request reaches owner only");
        using (var json = JsonDocument.Parse(ReportJson.Encode(rigid)))
            Check(json.RootElement.GetProperty("rigid").GetProperty("physicsSteps").GetInt64() == 9000, "rigid native status serialization");
        Check(control.Execute(LiveControlRequest.Parse("rigid-disable 1.3.0 stale " + Session + " 2"), 20, 3, Capture,
            rigidOperation: Rigid).reason == "stale-identity" && rigidCalls == 1, "stale rigid command has no ownership effect");
        Check(control.Execute(LiveControlRequest.Parse("rigid-retry 1.2.0 old " + Session + " 1"), 20, 3, Capture,
            rigidOperation: Rigid).reason == "invalid-request" && rigidCalls == 1, "rigid effects require new protocol");
        Check(control.Execute(LiveControlRequest.Parse("rigid-status 1.3.0 absent " + Session + " 1"), 20, 3, Capture).status == "unavailable", "missing rigid service is explicit");
        Check(control.Execute(LiveControlRequest.Parse("rigid-retry 1.3.0 retiring " + Session + " 1"), 20, 3, Capture,
            rigidOperation: _ => new LiveRigidStatus { state = "waiting-stock", operationError = "handback-required-before-retry" }).status == "rejected", "unsuccessful rigid mutation cannot acknowledge success");
        Check(control.Execute(LiveControlRequest.Parse("rigid-status 1.3.0 invalid " + Session + " 1"), 20, 3, Capture,
            rigidOperation: _ => new LiveRigidStatus { state = "owned", simulationRate = double.NaN }).reason == "invalid-rigid-status", "invalid performance observation is rejected");
        LiveSweepStatus StartSweep() => new LiveSweepStatus { state = "running", window = "stock-01", windowIndex = 0 };
        LiveSweepStatus SweepStatus() => new LiveSweepStatus { state = "complete", directory = "/runtime/report", window = "full-publication-02", windowIndex = 5 };
        LiveControlReply started = control.Execute(LiveControlRequest.Parse("sweep-start 1.1.0 sweep1 " + Session + " 1 dry-buoyancy"),
            20, 3, Capture, StartSweep, SweepStatus);
        Check(started.status == "ok" && started.protocolVersion == "1.1.0" && started.sweep.state == "running" &&
            started.sweep.windowIndex == 0, "start named sweep");
        LiveControlReply completed = control.Execute(LiveControlRequest.Parse("sweep-status 1.1.0 sweep2 " + Session + " 1 dry-buoyancy"),
            20, 3, Capture, StartSweep, SweepStatus);
        Check(completed.status == "ok" && completed.sweep.state == "complete" && completed.sweep.windowIndex == 5,
            "poll named sweep");
        LiveControlReply subscribed = control.Execute(LiveControlRequest.Parse(
            "sweep-subscribe 1.2.0 sweep3 " + Session + " 1 dry-buoyancy"),
            20, 3, Capture, StartSweep, SweepStatus);
        Check(subscribed.status == "ok" && subscribed.protocolVersion == "1.2.0" &&
            subscribed.sweep.state == "complete", "subscribe named sweep");
        using (var json = JsonDocument.Parse(ReportJson.Encode(completed)))
        {
            JsonElement sweep = json.RootElement.GetProperty("sweep");
            Check(sweep.GetProperty("state").GetString() == "complete" &&
                sweep.GetProperty("directory").GetString() == "/runtime/report" &&
                sweep.GetProperty("windowIndex").GetInt32() == 5,
                "populated sweep reply encoding");
        }
        LiveControlReply busyQuit = control.Execute(LiveControlRequest.Parse("quit-when-idle 1.1.0 quit1 " + Session + " 1"),
            20, 3, Capture, StartSweep, SweepStatus, () => "experiment-busy");
        Check(busyQuit.status == "rejected" && busyQuit.reason == "experiment-busy", "active sweep blocks quit");
        LiveControlReply acceptedQuit = control.Execute(LiveControlRequest.Parse("quit-when-idle 1.1.0 quit2 " + Session + " 1"),
            20, 3, Capture, StartSweep, SweepStatus, () => null);
        Check(acceptedQuit.status == "ok", "idle host accepts quit");
        Check(Execute(control, "snapshot 1.0.0 snap1 " + Session + " 1 3", 21, 4, Capture).snapshot.parts == 14, "snapshot");
        control.Observe("Flight", Vessel, true);
        Check(Execute(control, "snapshot 1.0.0 rebound " + Session + " 1 3", 21, 4, Capture).reason == "stale-identity",
            "same GUID with a replacement runtime object invalidates epoch");
        control.Observe("Flight", Guid.NewGuid().ToString("D"));
        Check(Execute(control, "snapshot 1.0.0 snap2 " + Session + " 1 3", 22, 5, Capture).reason == "stale-identity", "vessel switch invalidation");
        Check(captures == 1, "stale request must not capture");
        control.Observe("Flight", Vessel);
        Check(Execute(control, "snapshot 1.0.0 snap3 " + Session + " 4 6", 23, 6, Capture).status == "ok", "new epoch");
        Check(Execute(control, "snapshot 1.0.0 snap4 " + Guid.NewGuid().ToString("D") + " 4 6", 23, 6, Capture).reason == "stale-identity", "session invalidation");
        Check(Execute(control, "snapshot 1.0.0 snap5 " + Session + " 4 7", 23, 6, Capture).reason == "tick-not-yet-observed", "tick lower bound");
        Check(Execute(control, "snapshot 1.0.0 snap6 " + Session + " 4 6 extra", 23, 6, Capture).reason == "invalid-request", "strict fields");
        Check(Execute(control, "snapshot 2.0.0 snap7 " + Session + " 4 6", 23, 6, Capture).reason == "unsupported-version", "version negotiation");
        Check(Execute(control, "frobnicate 1.0.0 snap8", 23, 6, Capture).reason == "unknown-operation", "unknown operation");
        Check(Execute(control, "snapshot 1.0.0 snap9 " + Session + " 4 6", 23, 6, () => new LiveVesselSnapshot { vesselId = Vessel, parts = 1,
            universalTime = double.NaN }).reason == "capture-changed-or-invalid", "nonfinite capture rejected");
        Check(Execute(control, "snapshot 1.0.0 snap10 " + Session + " 4 6", 23, 6, () => {
            LiveVesselSnapshot sample = VesselSample(); sample.name = new string('x', 129); return sample;
        }).reason == "capture-changed-or-invalid", "unbounded name rejected");
        control.Observe("Flight", null);
        Check(Execute(control, "snapshot 1.0.0 snap11 " + Session + " 5 7", 24, 7, Capture).reason == "no-active-vessel", "empty flight");
        Check(captures == 2, "unavailable request must not capture");
        using var server = new LoopbackSnapshotServer(0);
        string wire = Exchange(server, "hello 1.0.0 wire1", command => ReportJson.Encode(Execute(control, command, 25, 7, Capture)), true);
        using (var json = JsonDocument.Parse(wire))
            Check(json.RootElement.GetProperty("identity").GetProperty("sessionId").GetString() == Session, "socket reply");
        int beforeStale = captures;
        wire = Exchange(server, "snapshot 1.0.0 wire2 " + Session + " 4 7",
            command => ReportJson.Encode(Execute(control, command, 26, 8, Capture)), true);
        using (var json = JsonDocument.Parse(wire))
            Check(json.RootElement.GetProperty("reason").GetString() == "stale-identity", "socket stale rejection");
        Check(captures == beforeStale, "socket stale request did not touch source");
        control.Observe("Flight", Vessel);
        PersistentConnection(server, control, Capture);
        PushedSweepConnection(server, control, Capture);
        wire = Exchange(server, new string('x', 257), _ => throw new Exception("Oversized frame reached game thread"), false);
        using (var json = JsonDocument.Parse(wire))
            Check(json.RootElement.GetProperty("reason").GetString() == "invalid-frame", "bounded frame");
        QueuedTimeoutCannotExecute();
        StartedTimeoutRetainsExecution();
        Console.WriteLine("PASS: " + assertions + " live control assertions; loopback handoff exercised without Unity.");
    }
}
