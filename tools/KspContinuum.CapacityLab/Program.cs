using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KspContinuum;

internal static partial class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static bool interrupted;
    sealed class Stage
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "running";
        public long Ticks { get; set; }
        public long AllocatedBytes { get; set; }
        public long WorkingSetBefore { get; set; }
        public long WorkingSetAfter { get; set; }
        public long? PeakWorkingSetAfter { get; set; }
    }
    sealed class Receipt
    {
        public string Schema { get; set; } = "continuum-capacity-runtime/v1";
        public string Status { get; set; } = "incomplete";
        public string Failure { get; set; } = "";
        public string ActiveStage { get; set; } = "startup";
        public string Scope { get; set; } = "Generated real Core numeric execution, no rendering or stock/native provider/contact/collision qualification. Rigid regions are disconnected owners inside one portable world; they are not KSP vessels. Construction and persistence are cold stages. Per-stage costs are sequential; Core nested clocks are nonadditive. CompleteTicks includes measured cold/hot stages, invariant checks and checkpoint IO, excluding final receipt serialization.";
        public CapacityOptions Options { get; set; } = new();
        public string ToolSha256 { get; set; } = "";
        public string CoreSha256 { get; set; } = "";
        public string FixtureSha256 { get; set; } = "";
        public string PartRecipeSha256 { get; set; } = "";
        public string Host { get; set; } = Environment.MachineName;
        public string Runtime { get; set; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        public string Architecture { get; set; } = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        public int ProcessId { get; set; } = Environment.ProcessId;
        public int ExitCode { get; set; }
        public long ClockFrequency { get; set; } = Stopwatch.Frequency;
        public long CompleteTicks { get; set; }
        public long CompleteAllocatedBytes { get; set; }
        public long? PeakWorkingSet { get; set; }
        public string PeakWorkingSetAvailability => PeakWorkingSet.HasValue ? "available" : "unavailable-process-api";
        public List<Stage> Stages { get; set; } = new();
        public int AuthoredParts { get; set; }
        public int PhysicalBodies { get; set; }
        public int RigidRegions { get; set; }
        public int Connections { get; set; }
        public int ResourceRows { get; set; }
        public int ProviderStateRows { get; set; }
        public int ActuatorRows { get; set; }
        public int EnabledActuatorRows { get; set; }
        public int SurfaceRows { get; set; }
        public int ThermalEdges { get; set; }
        public int ColliderRows { get; set; }
        public int VisibleInstances { get; set; }
        public int UniqueAssets { get; set; }
        public int FlexibleJoints { get; set; }
        public int ActiveContacts { get; set; }
        public int ResultingFragments { get; set; }
        public int CompletedSteps { get; set; }
        public int ContinuationSteps { get; set; }
        public int DisplacementRegionsChecked { get; set; }
        public long CommittedEpoch { get; set; }
        public long CommittedStateRevision { get; set; }
        public double SimulationTime { get; set; }
        public double TotalMass { get; set; }
        public double MaximumMassResidual { get; set; }
        public double MaximumCenterResidual { get; set; }
        public double MaximumInertiaRelativeResidual { get; set; }
        public double MaximumDisplacementResidual { get; set; }
        public double MaximumVelocityResidual { get; set; }
        public double MaximumResourceResidual { get; set; }
        public double MaximumProviderResidual { get; set; }
        public double MaximumTemperature { get; set; }
        public bool UncommittedBankUnchanged { get; set; }
        public bool RestoredPacketIdentical { get; set; }
        public bool ContinuationPacketIdentical { get; set; }
        public string CheckpointSha256 { get; set; } = "";
        public string ContinuationSha256 { get; set; } = "";
        public object? DeadlineObservation { get; set; }
        public long CheckpointBytes { get; set; }
        public OwnedRuntimeSystemCounters? Systems { get; set; }
        public OwnedRuntimeWork? BankWork { get; set; }
    }
    static int Main(string[] arguments)
    {
        if (arguments.Length >= 2 && arguments[0] == "--mode" && arguments[1] == "compare") return CompareCapacity(arguments);
        int mode = Array.IndexOf(arguments, "--mode");
        if (mode >= 0 && mode + 1 < arguments.Length && arguments[mode + 1] == "spatial") return RunSpatial(arguments);
        Console.CancelKeyPress += (_, e) => { interrupted = true; e.Cancel = true; };
        var receipt = new Receipt(); string output = ""; bool outputOwned = false; long start = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            var options = Parse(arguments); options.Validate(); receipt.Options = options; output = Path.GetFullPath(options.Output);
            if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Output already exists; choose a fresh directory.");
            Directory.CreateDirectory(output); outputOwned = true;
            receipt.ToolSha256 = FileHash(Assembly.GetExecutingAssembly().Location); receipt.CoreSha256 = FileHash(typeof(OwnedRuntimeWorld).Assembly.Location);
            byte[] recipe = JsonSerializer.SerializeToUtf8Bytes(new { schema = CapacityFixture.Recipe, options.Family, options.Provider, options.Parts, options.Bodies, options.Regions, options.ActiveFraction,
                dryBodyMassKg = 1, dryPrincipalKgMeters2 = new[] { 1d, 1d, 1d }, heatCapacityJoulesPerKelvin = 1000,
                electricCapacityUnits = 100, fuelCapacityKg = options.Provider == "engine" ? 1 : 0,
                wheelTorqueNewtonMeters = .1, engineForceNewtons = .2, specificImpulseSeconds = 300, electricRate = .01, heatWatts = 1,
                thermalConductance = .1, radiationArea = .25, emissivity = .5, temperatureKelvin = 300,
                physicalRecipe = "Authored parts alias unit dry bodies. Parts and point resources are at their body origin. Station is a centered line; grid is a centered lattice. Engine forces act at region origin, with opposing paired axes; a single-part engine region has actual net thrust. Region roots are separated. No contact/render rows." }, Json);
            WriteNew(Path.Combine(output, "fixture.json"), recipe); receipt.FixtureSha256 = Hash(recipe); receipt.PartRecipeSha256 = Hash(Encoding.UTF8.GetBytes("part-recipe/v1:" + receipt.FixtureSha256));
            CapacityFixture fixture = Measure(receipt, "fixture-construction", () => new CapacityFixture(options));
            receipt.AuthoredParts = fixture.Parts.Length; receipt.PhysicalBodies = fixture.Bodies.Length; receipt.ResourceRows = fixture.Resources.Length; receipt.ProviderStateRows = fixture.ProviderKeys.Length;
            receipt.ActuatorRows = fixture.Actuators.Length; receipt.EnabledActuatorRows = fixture.ActiveRows; receipt.SurfaceRows = fixture.Surfaces.Length; receipt.ThermalEdges = fixture.ThermalLinks.Length;
            var definition = Measure(receipt, "definition-construction", () => new OwnedRuntimeDefinition("generated-" + receipt.FixtureSha256, 0, fixture.Parts, fixture.Bodies, fixture.Resources, fixture.ProviderKeys));
            var topology = Measure(receipt, "topology-compilation", () => OwnedRuntimeTopology.Compile(definition, fixture.Connections));
            receipt.RigidRegions = topology.RegionCount; receipt.Connections = topology.ConnectionCount;
            Need(topology.RegionCount == options.Regions, "Compiled region count differs from authored partition.");
            var world = Measure(receipt, "world-construction", () => new OwnedRuntimeWorld(definition, topology, new OwnedRuntimeMotion { Orientation = Rotation.Identity, Velocity = CapacityFixture.InitialVelocity }));
            Measure(receipt, "region-placement", () =>
            {
                var motions = new OwnedRuntimeMotion[options.Regions]; for (int r = 0; r < motions.Length; r++) motions[r] = new OwnedRuntimeMotion { RootPosition = fixture.RegionRoot(r), Orientation = Rotation.Identity, Velocity = CapacityFixture.InitialVelocity };
                world.SetRegionMotions(world.Stamp, motions, world.Stamp.Time, world.Stamp.FrameId); return true;
            });
            var systems = Measure(receipt, "systems-construction", () => new OwnedRuntimeSystems(world, fixture.Actuators, fixture.Surfaces, fixture.ThermalLinks, observeClock: true, wheelProviderSlots: fixture.WheelSlots));
            Measure(receipt, "systems-configuration", () => { for (int a = 0; a < fixture.Enabled.Length; a++) systems.SetEnabled(a, fixture.Enabled[a]); systems.SetExecutionStrategy(options.Strategy == "compiled" ? OwnedRuntimeExecutionStrategy.Compiled : OwnedRuntimeExecutionStrategy.Reference); world.SetWorkObservation(world.Stamp, true); return true; });
            var host = Measure(receipt, "host-construction", () => new OwnedRuntimeHost(world, systems));
            var imported = new OwnedRuntimeImportResult { World = world, Systems = systems, Source = fixture.Source(receipt.FixtureSha256, receipt.PartRecipeSha256) };
            var environment = fixture.Environment; var controls = fixture.Controls; var plan = new OwnedRuntimeStepPlan(options.Delta);
            Measure(receipt, "initial-analytic-checks", () => { Check(fixture, world, receipt); return true; });
            if (options.DeadlineProbe)
            {
                var stamp = world.Stamp;
                systems.SetDeadlineStrategy(stamp, OwnedRuntimeDeadlineStrategy.Retained);
                var first = Measure(receipt, "deadline-first", () => systems.FindActuatorBoundary(stamp, controls, options.Delta));
                var repeated = Measure(receipt, "deadline-repeat", () => systems.FindActuatorBoundary(stamp, controls, options.Delta));
                Need(world.Stamp.Matches(stamp) && first.Supported && repeated.Supported && first.DeltaSeconds == repeated.DeltaSeconds && first.Reached == repeated.Reached
                    && first.Kind == repeated.Kind && first.Resource == repeated.Resource && first.Actuator == repeated.Actuator && first.Certification == repeated.Certification
                    && first.FallbackReason == repeated.FallbackReason && first.SourceStamp.Matches(repeated.SourceStamp),
                    "Retained deadline changed state or its repeat frontier.");
                receipt.DeadlineObservation = new { first = DeadlineDetails(first), repeated = DeadlineDetails(repeated) };
            }
            Measure(receipt, "step-first", () => { host.Advance(options.Delta, fixture.Input, environment, controls); receipt.CompletedSteps++; Frontier(receipt, world); return true; });
            Measure(receipt, "step-steady", () => { for (int s = 1; s < options.Steps; s++) { Cancellation(); host.Advance(options.Delta, fixture.Input, environment, controls); receipt.CompletedSteps++; Frontier(receipt, world); } return true; });
            receipt.Systems = systems.Counters.Snapshot(); receipt.BankWork = world.Work;
            Measure(receipt, "stepped-analytic-checks", () => { Check(fixture, world, receipt); return true; });
            var checkpoint = Measure(receipt, "checkpoint-capture", () => host.CaptureCheckpoint(imported, controls, environment, plan));
            byte[] bytes = Measure(receipt, "checkpoint-encode", () => OwnedRuntimeCheckpointBinary.Encode(checkpoint)); receipt.CheckpointBytes = bytes.Length; receipt.CheckpointSha256 = Hash(bytes);
            Measure(receipt, "checkpoint-write", () => { WriteNew(Path.Combine(output, "checkpoint.ofcb"), bytes); return true; });
            byte[] persisted = Measure(receipt, "checkpoint-read", () => File.ReadAllBytes(Path.Combine(output, "checkpoint.ofcb")));
            Need(persisted.AsSpan().SequenceEqual(bytes), "Persisted packet differs.");
            var decoded = Measure(receipt, "checkpoint-decode", () => OwnedRuntimeCheckpointBinary.Decode(persisted));
            OwnedRuntimeRestoredState restoredState = null!;
            var restoredHost = Measure(receipt, "checkpoint-restore", () => host.RestoreCheckpoint(decoded, receipt.FixtureSha256, receipt.PartRecipeSha256, out restoredState));
            var restoredImport = new OwnedRuntimeImportResult { World = restoredState.World, Systems = restoredState.Systems, Source = restoredState.Source };
            Measure(receipt, "restored-exact-check", () => { byte[] restored = OwnedRuntimeCheckpointBinary.Encode(restoredHost.CaptureCheckpoint(restoredImport, restoredState.Controls, restoredState.Environment, restoredState.Plan)); Need(restored.AsSpan().SequenceEqual(bytes), "Complete restored packet differs."); receipt.RestoredPacketIdentical = true; return true; });
            Measure(receipt, "uncommitted-rollback-check", () =>
            {
                using (var step = world.BeginStep(world.Stamp, options.Delta, fixture.Input))
                { if (world.ResourceCount > 0) step.SetResourceQuantity(0, 0); if (definition.ProviderStateCount > 0) step.SetProviderState(0, 77); step.SetTemperature(0, 310); }
                byte[] after = OwnedRuntimeCheckpointBinary.Encode(host.CaptureCheckpoint(imported, controls, environment, plan)); Need(after.AsSpan().SequenceEqual(bytes), "Discarded transaction changed complete checkpoint state."); receipt.UncommittedBankUnchanged = true; return true;
            });
            receipt.Systems = systems.Counters.Snapshot(); receipt.BankWork = world.Work;
            Measure(receipt, "continuation-original", () => { for (int s = 0; s < options.ContinuationSteps; s++) { Cancellation(); host.Advance(options.Delta, fixture.Input, environment, controls); receipt.ContinuationSteps++; Frontier(receipt, world); } return true; });
            Measure(receipt, "continuation-restored", () => { for (int s = 0; s < options.ContinuationSteps; s++) { Cancellation(); restoredHost.Advance(options.Delta, fixture.Input, restoredState.Environment, restoredState.Controls); } return true; });
            Measure(receipt, "continuation-exact-check", () =>
            {
                byte[] original = OwnedRuntimeCheckpointBinary.Encode(host.CaptureCheckpoint(imported, controls, environment, plan));
                byte[] restored = OwnedRuntimeCheckpointBinary.Encode(restoredHost.CaptureCheckpoint(restoredImport, restoredState.Controls, restoredState.Environment, restoredState.Plan));
                WriteNew(Path.Combine(output, "continuation-original.ofcb"), original); WriteNew(Path.Combine(output, "continuation-restored.ofcb"), restored);
                Need(original.AsSpan().SequenceEqual(restored), "Complete original/restored continuation differs."); receipt.ContinuationPacketIdentical = true; receipt.ContinuationSha256 = Hash(original);
                Check(fixture, world, receipt); Check(fixture, restoredState.World, receipt); return true;
            });
            receipt.ContinuationSteps = options.ContinuationSteps; receipt.SimulationTime = world.Stamp.Time; receipt.Status = "passed"; receipt.ActiveStage = "complete";
        }
        catch (OperationCanceledException error) { receipt.Status = "interrupted"; receipt.Failure = error.ToString(); receipt.ExitCode = 130; }
        catch (Exception error)
        { receipt.Status = error is ArgumentException && receipt.ActiveStage is "fixture-construction" or "definition-construction" or "topology-compilation" ? "unsupported" : error is InvalidDataException ? "invalid" : "failed"; receipt.Failure = error.ToString(); receipt.ExitCode = 2; }
        finally
        {
            receipt.CompleteTicks = Stopwatch.GetTimestamp() - start; receipt.CompleteAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated; using var process = Process.GetCurrentProcess(); process.Refresh(); receipt.PeakWorkingSet = Peak(process);
            if (outputOwned)
            {
                try { WriteNew(Path.Combine(output, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, Json)); }
                catch (Exception error) { Console.Error.WriteLine("Receipt write failed: " + error); receipt.ExitCode = 2; }
            }
        }
        Console.WriteLine(receipt.Status + " at " + receipt.ActiveStage + ": " + output); if (receipt.ExitCode != 0) Console.Error.WriteLine(receipt.Failure); return receipt.ExitCode;
    }
    static T Measure<T>(Receipt receipt, string name, Func<T> action)
    {
        Cancellation(); receipt.ActiveStage = name; using var process = Process.GetCurrentProcess(); process.Refresh();
        var stage = new Stage { Name = name, WorkingSetBefore = process.WorkingSet64 }; receipt.Stages.Add(stage);
        long start = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
        try { T result = action(); stage.Status = "passed"; return result; }
        catch { stage.Status = "failed"; throw; }
        finally { stage.Ticks = Stopwatch.GetTimestamp() - start; stage.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated; process.Refresh(); stage.WorkingSetAfter = process.WorkingSet64; stage.PeakWorkingSetAfter = Peak(process); }
    }
    static void Check(CapacityFixture f, OwnedRuntimeWorld world, Receipt receipt)
    {
        using var read = world.AcquireRead(); var o = f.Options; double time = read.Stamp.Time;
        Need(read.PartCount == o.Parts && read.BodyCount == o.Bodies && read.RegionCount == o.Regions, "Committed bank dimensions differ.");
        double fuel = 0, expectedFuel = o.Provider == "engine" ? o.Parts - f.ActiveRows * .2 * o.Throttle / (300 * 9.80665) * time : 0;
        for (int p = 0; p < o.Parts; p++)
        {
            bool enabled = o.Provider != "none" && f.Enabled[p]; int e = o.Provider == "engine" ? 2 * p + 1 : p;
            double expected = 100 - (enabled ? .01 * o.Throttle * time : 0); receipt.MaximumResourceResidual = Math.Max(receipt.MaximumResourceResidual, Math.Abs(read.GetResourceQuantity(e) - expected));
            if (o.Provider == "engine") fuel += read.GetResourceQuantity(2 * p);
            if (o.Provider == "wheel") receipt.MaximumProviderResidual = Math.Max(receipt.MaximumProviderResidual, Math.Abs(read.GetProviderState(p) - (enabled ? -.1 * o.Throttle * time : 0)));
            double temperature = read.GetTemperature(p); Need(double.IsFinite(temperature) && temperature > 0, "Temperature is outside its physical domain."); receipt.MaximumTemperature = Math.Max(receipt.MaximumTemperature, temperature);
        }
        receipt.MaximumResourceResidual = Math.Max(receipt.MaximumResourceResidual, Math.Abs(fuel - expectedFuel) / Math.Max(1, o.Parts));
        double total = 0;
        for (int r = 0; r < o.Regions; r++)
        {
            var region = read.GetRegion(r); double mass = 0; Vec center = new();
            for (int b = r * f.BodiesPerRegion; b < (r + 1) * f.BodiesPerRegion; b++)
            {
                double bodyMass = 1; if (o.Provider == "engine") for (int p = b * f.PartsPerBody; p < (b + 1) * f.PartsPerBody; p++) bodyMass += read.GetResourceQuantity(2 * p);
                receipt.MaximumMassResidual = Math.Max(receipt.MaximumMassResidual, Math.Abs(read.GetBody(b).Mass - bodyMass)); mass += bodyMass; center += f.Bodies[b].Member.LocalPosition * bodyMass;
            }
            center *= 1 / mass; total += mass; receipt.MaximumMassResidual = Math.Max(receipt.MaximumMassResidual, Math.Abs(region.Properties.Mass - mass) / Math.Max(1, mass));
            receipt.MaximumCenterResidual = Math.Max(receipt.MaximumCenterResidual, Distance(region.Properties.CenterOfMass, center));
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int b = r * f.BodiesPerRegion; b < (r + 1) * f.BodiesPerRegion; b++)
            {
                double m = read.GetBody(b).Mass; Vec d = f.Bodies[b].Member.LocalPosition + center * -1;
                xx += 1 + m * (d.Y * d.Y + d.Z * d.Z); yy += 1 + m * (d.X * d.X + d.Z * d.Z); zz += 1 + m * (d.X * d.X + d.Y * d.Y);
                xy -= m * d.X * d.Y; xz -= m * d.X * d.Z; yz -= m * d.Y * d.Z;
            }
            var i = region.Properties.Inertia; double scale = Math.Max(1, Math.Max(xx, Math.Max(yy, zz)));
            receipt.MaximumInertiaRelativeResidual = Math.Max(receipt.MaximumInertiaRelativeResidual, new[] { Math.Abs(i.XX - xx), Math.Abs(i.XY - xy), Math.Abs(i.XZ - xz), Math.Abs(i.YY - yy), Math.Abs(i.YZ - yz), Math.Abs(i.ZZ - zz) }.Max() / scale);
            if (o.Provider != "engine" || f.PartsPerRegion > 1 || o.Throttle == 0 || f.ActiveRows == 0)
            {
                receipt.DisplacementRegionsChecked = o.Regions;
                receipt.MaximumDisplacementResidual = Math.Max(receipt.MaximumDisplacementResidual, Distance(region.Motion.RootPosition, f.RegionRoot(r) + CapacityFixture.InitialVelocity * time));
                receipt.MaximumVelocityResidual = Math.Max(receipt.MaximumVelocityResidual, Distance(region.Motion.Velocity, CapacityFixture.InitialVelocity));
            }
        }
        receipt.TotalMass = total; double expectedMass = o.Bodies + expectedFuel; receipt.MaximumMassResidual = Math.Max(receipt.MaximumMassResidual, Math.Abs(total - expectedMass) / Math.Max(1, expectedMass));
        Need(receipt.MaximumMassResidual <= 1e-9 && receipt.MaximumCenterResidual <= 1e-8 && receipt.MaximumInertiaRelativeResidual <= 1e-9, "Independent mass/center/tensor oracle differs.");
        Need(receipt.MaximumDisplacementResidual <= 1e-7 && receipt.MaximumVelocityResidual <= 1e-8, "Balanced inertial displacement oracle differs.");
        Need(receipt.MaximumResourceResidual <= 1e-8 && receipt.MaximumProviderResidual <= 1e-8, "Independent resource/wheel-rate oracle differs (depletion/saturation is outside this fixture interval).");
    }
    static CapacityOptions Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Use --mode runtime --family station|grid --parts N --bodies N --regions N --provider wheel|engine|none --active-fraction F --throttle F --steps N --delta S --strategy reference|compiled --output NEW_ABSOLUTE_DIR.");
        var result = new CapacityOptions(); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            string key = args[i], value = args[i + 1]; if (!seen.Add(key)) throw new ArgumentException("Duplicate option: " + key);
            int Number() => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); double Real() => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            switch (key)
            {
                case "--mode": if (value != "runtime") throw new ArgumentException("This entry point admits runtime mode only."); break;
                case "--family": result.Family = value; break; case "--provider": result.Provider = value; break; case "--strategy": result.Strategy = value; break;
                case "--parts": result.Parts = Number(); break; case "--bodies": result.Bodies = Number(); break; case "--regions": result.Regions = Number(); break;
                case "--steps": result.Steps = Number(); break; case "--continuation-steps": result.ContinuationSteps = Number(); break;
                case "--delta": result.Delta = Real(); break; case "--active-fraction": result.ActiveFraction = Real(); break; case "--throttle": result.Throttle = Real(); break;
                case "--output": result.Output = value; break; default: throw new ArgumentException("Unknown option: " + key);
                case "--deadline-probe": result.DeadlineProbe = bool.Parse(value); break;
            }
        }
        return result;
    }
    static int RunSpatial(string[] args)
    {
        string output = ""; bool owned = false; string status = "failed", failure = ""; object? result = null;
        int count = 1048576; string kind = "separated"; long candidates = 2000000, tests = 10000000, visits = 100000000, memory = 536870912;
        long start = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread(); int exit = 2;
        using var process = Process.GetCurrentProcess(); process.Refresh(); long rss = process.WorkingSet64;
        try
        {
            if (args.Length % 2 != 0) throw new ArgumentException("Spatial mode accepts option/value pairs.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (!seen.Add(args[i])) throw new ArgumentException("Duplicate spatial option."); string value = args[i + 1];
                switch (args[i])
                {
                    case "--mode": if (value != "spatial") throw new ArgumentException("Expected spatial mode."); break;
                    case "--parts": count = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); break;
                    case "--case": kind = value; break; case "--output": output = value; break;
                    case "--maximum-candidates": candidates = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); break;
                    case "--maximum-pair-tests": tests = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); break;
                    case "--maximum-bucket-visits": visits = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); break;
                    case "--maximum-memory-bytes": memory = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException("Unknown spatial option: " + args[i]);
                }
            }
            if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Spatial output must be a fresh absolute directory.");
            Directory.CreateDirectory(output); owned = true;
            string nativeCase = kind == "sparse" ? "sparse-contact" : kind == "dense" ? "dense-overlap" : kind;
            var limits = new KspContinuum.CapacityLab.CapacityBroadphase.Limits(maximumCandidates: candidates, maximumPairTests: tests, maximumBucketVisits: visits, maximumMemoryBytes: memory);
            var observation = KspContinuum.CapacityLab.CapacityBroadphase.Run(count, nativeCase, limits);
            result = observation; status = observation.Status; failure = observation.Reason; exit = observation.Complete ? 0 : 2;
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            long complete = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - allocated; process.Refresh();
            if (owned)
            {
                var receipt = new { schema = "continuum-capacity-spatial-consumer/v1", status, failure, exitCode = exit,
                    toolSha256 = FileHash(Assembly.GetExecutingAssembly().Location), coreSha256 = FileHash(typeof(OwnedRuntimeWorld).Assembly.Location),
                    processId = Environment.ProcessId, host = Environment.MachineName, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    partsPerCraft = count, caseKind = kind, maximumCandidates = candidates, maximumPairTests = tests, maximumBucketVisits = visits, maximumMemoryBytes = memory,
                    completeTicks = complete, clockFrequency = Stopwatch.Frequency, allocatedBytes = bytes, workingSetBefore = rss, workingSetAfter = process.WorkingSet64, peakWorkingSet = Peak(process), peakWorkingSetAvailability = Peak(process).HasValue ? "available" : "unavailable-process-api",
                    scope = "Two generated primitive AABB craft. Result is bounded broadphase candidates, never native contacts/collision response or a million-body runtime solve. Complete clock includes CLI admission, generation, build, query and candidate hashing; excludes final receipt serialization.", result };
                try { WriteNew(Path.Combine(output, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, Json)); }
                catch (Exception error) { Console.Error.WriteLine("Spatial receipt write failed: " + error); exit = 2; }
            }
        }
        Console.WriteLine(status + ": " + output); if (exit != 0) Console.Error.WriteLine(failure); return exit;
    }
    static long? Peak(Process process) { long value = process.PeakWorkingSet64; return value > 0 ? value : null; }
    static object DeadlineDetails(OwnedRuntimeActuatorBoundary value) => new { value.Strategy, value.Certification, value.FallbackReason, value.ProbeCount,
        value.Supported, value.Reached, value.DeltaSeconds, value.Actuator, value.Resource, value.Kind, value.Reason, value.SourceStamp, value.ConfigurationRevision,
        value.PredictionAvailable, value.PredictedKind, value.PredictedActuator, value.PredictedResource,
        predictedDelta = double.IsFinite(value.PredictedDelta) ? (double?)value.PredictedDelta : null,
        predictedLowerDelta = double.IsFinite(value.PredictedLowerDelta) ? (double?)value.PredictedLowerDelta : null,
        predictedUpperDelta = double.IsFinite(value.PredictedUpperDelta) ? (double?)value.PredictedUpperDelta : null };
    static double Distance(Vec a, Vec b) { Vec d = a + b * -1; return Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z); }
    static void Frontier(Receipt receipt, OwnedRuntimeWorld world) { var stamp = world.Stamp; receipt.SimulationTime = stamp.Time; receipt.CommittedEpoch = stamp.Epoch; receipt.CommittedStateRevision = stamp.StateRevision; }
    static void Need(bool pass, string message) { if (!pass) throw new InvalidDataException(message); }
    static void Cancellation() { if (interrupted) throw new OperationCanceledException("Interrupted between complete owned-runtime transactions."); }
    static string FileHash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static void WriteNew(string path, byte[] bytes) { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true); }
}
