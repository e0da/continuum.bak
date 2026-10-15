using KspContinuum;

internal sealed class CapacityOptions
{
    public string Family { get; set; } = "station";
    public string Provider { get; set; } = "wheel";
    public string Strategy { get; set; } = "reference";
    public string StateFormat { get; set; } = "legacy";
    public int Parts { get; set; } = 4096;
    public int Bodies { get; set; } = 256;
    public int Regions { get; set; } = 1;
    public int Steps { get; set; } = 16;
    public int ContinuationSteps { get; set; } = 3;
    public double Delta { get; set; } = .015625;
    public double ActiveFraction { get; set; } = .125;
    public double Throttle { get; set; } = .25;
    public string Output { get; set; } = "";
    public bool DeadlineProbe { get; set; }
    public void Validate()
    {
        if (StateFormat != "legacy" && StateFormat != "columnar") throw new ArgumentException("State format must be legacy|columnar.");
        if (Family != "station" && Family != "grid" || Provider != "wheel" && Provider != "engine" && Provider != "none" || Strategy != "reference" && Strategy != "compiled")
            throw new ArgumentException("Family must be station|grid, provider wheel|engine|none, and strategy reference|compiled.");
        if (!PowerOfTwo(Parts) || !PowerOfTwo(Bodies) || !PowerOfTwo(Regions) || Regions > Bodies || Bodies > Parts)
            throw new ArgumentException("Positive power-of-two dimensions must satisfy regions <= bodies <= parts.");
        if (Steps < 1 || Steps > 1000000 || ContinuationSteps < 1 || ContinuationSteps > 4096 || !double.IsFinite(Delta) || Delta <= 0 || Delta > .1)
            throw new ArgumentException("Steps, continuation and positive delta <= 0.1 are bounded.");
        if (!double.IsFinite(ActiveFraction) || ActiveFraction < 0 || ActiveFraction > 1 || !double.IsFinite(Throttle) || Throttle < 0 || Throttle > 1)
            throw new ArgumentException("Activity fraction and throttle must be finite within 0..1.");
        double active = Parts / Regions * ActiveFraction;
        if (Provider != "none" && (active != Math.Floor(active) || Parts / Regions > 1 && (long)active % 2 != 0))
            throw new ArgumentException("Activity must select an integer number of symmetric provider pairs per region (or its single part).");
        if (!Path.IsPathFullyQualified(Output)) throw new ArgumentException("Output must be a fresh absolute directory.");
    }
    static bool PowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}

internal sealed class CapacityFixture
{
    public const string Recipe = "continuum-capacity-generated/v1";
    public static readonly Vec InitialVelocity = new(3, -2, .5);
    public readonly CapacityOptions Options;
    public readonly OwnedRuntimePartDefinition[] Parts;
    public readonly OwnedRuntimeBodyDefinition[] Bodies;
    public readonly OwnedRuntimeResourceDefinition[] Resources;
    public readonly OwnedRuntimeConnection[] Connections;
    public readonly OwnedRuntimeActuatorSpec[] Actuators;
    public readonly OwnedRuntimeSurfaceSpec[] Surfaces;
    public readonly OwnedRuntimeThermalLink[] ThermalLinks;
    public readonly long[] ProviderKeys;
    public readonly int[] WheelSlots;
    public readonly bool[] Enabled;
    public readonly OwnedRuntimeBodyBinding[] BodyBindings;
    public int ActiveRows { get; }
    public int PartsPerBody => Options.Parts / Options.Bodies;
    public int BodiesPerRegion => Options.Bodies / Options.Regions;
    public int PartsPerRegion => Options.Parts / Options.Regions;

    public CapacityFixture(CapacityOptions options)
    {
        Options = options;
        int maxParts = options.StateFormat == "columnar" ? OwnedRuntimeCapacity.Research.MaxParts : OwnedRuntimeDefinition.MaximumParts;
        if (options.Parts > maxParts)
            throw new ArgumentOutOfRangeException(nameof(options.Parts), "Selected state admission permits at most " + maxParts + " authored parts.");
        Parts = new OwnedRuntimePartDefinition[options.Parts]; Bodies = new OwnedRuntimeBodyDefinition[options.Bodies];
        Resources = new OwnedRuntimeResourceDefinition[options.Parts * (options.Provider == "engine" ? 2 : 1)];
        ProviderKeys = options.Provider == "wheel" ? new long[options.Parts] : Array.Empty<long>();
        Actuators = options.Provider == "none" ? Array.Empty<OwnedRuntimeActuatorSpec>() : new OwnedRuntimeActuatorSpec[options.Parts];
        WheelSlots = new int[Actuators.Length]; Enabled = new bool[Actuators.Length];
        Surfaces = new OwnedRuntimeSurfaceSpec[options.Parts]; ThermalLinks = new OwnedRuntimeThermalLink[options.Parts - options.Regions];
        Connections = new OwnedRuntimeConnection[options.Bodies - options.Regions]; BodyBindings = options.StateFormat == "legacy" ? new OwnedRuntimeBodyBinding[options.Bodies] : Array.Empty<OwnedRuntimeBodyBinding>();
        Vec[] coordinates = Coordinates(options.Family, BodiesPerRegion);
        int edge = 0, thermal = 0, activePerRegion = (int)(PartsPerRegion * options.ActiveFraction);
        for (int b = 0; b < Bodies.Length; b++)
        {
            Bodies[b] = new OwnedRuntimeBodyDefinition(b + 1, new RigidAssemblyMember(b, 1, coordinates[b % BodiesPerRegion], Rotation.Identity, new Vec(), new Vec(1, 1, 1), Rotation.Identity));
            if (BodyBindings.Length != 0) BodyBindings[b] = new OwnedRuntimeBodyBinding { sourceBodyId = b + 1, bodyId = b + 1, bodySlot = b };
            if (b % BodiesPerRegion != 0) Connections[edge] = new OwnedRuntimeConnection(++edge, (b - 1) * PartsPerBody + 1, b * PartsPerBody + 1);
        }
        for (int p = 0; p < Parts.Length; p++)
        {
            int body = p / PartsPerBody, localPart = p % PartsPerRegion;
            Parts[p] = new OwnedRuntimePartDefinition(p + 1, "generated-part-" + (p + 1), body + 1, new Vec(), Rotation.Identity, 1000);
            Surfaces[p] = new OwnedRuntimeSurfaceSpec(p, new Vec(), new Vec(0, 1, 0), 0, 0, .25, .5, 0);
            if (localPart != 0) ThermalLinks[thermal] = new OwnedRuntimeThermalLink(p - 1, p, .1); if (localPart != 0) thermal++;
            int electric = options.Provider == "engine" ? 2 * p + 1 : p;
            Resources[electric] = new OwnedRuntimeResourceDefinition(electric + 1, p + 1, "ElectricCharge", 100, 0, 100);
            if (options.Provider == "none") continue;
            Enabled[p] = localPart < (activePerRegion + 1) / 2 || localPart >= PartsPerRegion - activePerRegion / 2;
            if (Enabled[p]) ActiveRows++;
            WheelSlots[p] = options.Provider == "wheel" ? p : -1;
            if (options.Provider == "wheel")
            {
                ProviderKeys[p] = 1000000000L + p;
                Actuators[p] = new OwnedRuntimeActuatorSpec(p, OwnedRuntimeActuatorKind.ReactionWheel, new Vec(), new Vec(1, 0, 0), .1,
                    electricResource: electric, electricUnitsPerSecond: .01, heatWatts: 1, wheelMomentumCapacity: 100);
            }
            else
            {
                Resources[2 * p] = new OwnedRuntimeResourceDefinition(2 * p + 1, p + 1, "Fuel", 1, 1, 1);
                Vec point = coordinates[body % BodiesPerRegion] * -1;
                Vec axis = new(localPart < PartsPerRegion / 2 || PartsPerRegion == 1 ? 1 : -1, 0, 0);
                Actuators[p] = new OwnedRuntimeActuatorSpec(p, OwnedRuntimeActuatorKind.Engine, point, axis, .2, new[] { "Fuel" }, new[] { 1d }, 300,
                    electricResource: electric, electricRate: .01, heatWatts: 1);
            }
        }
    }
    static Vec[] Coordinates(string family, int count)
    {
        var result = new Vec[count]; int side = (int)Math.Ceiling(Math.Pow(count, 1d / 3)); Vec sum = new();
        for (int i = 0; i < count; i++)
        {
            result[i] = family == "station" ? new Vec(2d * i, 0, 0) : new Vec(2d * (i % side), 2d * (i / side % side), 2d * (i / side / side));
            sum += result[i];
        }
        Vec center = sum * (1d / count); for (int i = 0; i < count; i++) result[i] += center * -1;
        return result;
    }
    public Vec RegionRoot(int region) => new(region * (4d * BodiesPerRegion + 100), 0, 0);
    public OwnedRuntimeControls Controls => new() { Throttle = Options.Provider == "engine" ? Options.Throttle : 0,
        Attitude = Options.Provider == "wheel" ? new Vec(Options.Throttle, 0, 0) : new Vec(), EnginesEnabled = true, WheelsEnabled = true };
    public OwnedRuntimeInput Input => new(Controls.Throttle, Controls.Attitude.X, Controls.Attitude.Y, Controls.Attitude.Z);
    public OwnedRuntimeEnvironment Environment => new("generated-vacuum/v1", 0, 0, 0, 300, new Vec(), new Vec(), radiationTemperature: 300);
    public OwnedRuntimeSourceBinding Source(string fixtureSha, string partSha) => new()
    {
        runId = "generated-capacity", vesselId = "generated-regions-" + Options.Regions, processId = System.Environment.ProcessId,
        nativeManifestSha256 = fixtureSha, partSpecsSha256 = partSha, rendererManifestSha256 = "", colliderGeometrySha256 = "", supplyCaptureSha256 = "",
        scope = "Generated Core fixture: source hash slots bind authored numeric recipes and synthetic identity ledger; no KSP/native capture.",
        bodies = BodyBindings, renderers = Array.Empty<OwnedRuntimeRendererBinding>(), subtractedNativeCenterVelocity = new double[3], sourceFrameVelocity = new double[3],
        omissions = new[] { "No imported assets, render instances, colliders, contacts, stock providers or native host." }
    };
}
