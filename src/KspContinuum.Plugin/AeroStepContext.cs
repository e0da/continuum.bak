using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class AeroStepContext : IDisposable
    {
        const string Owner = "continuum.aero-step-context";
        const string StockSha = "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const int Roles = 6, MaxParts = 8192, MaxMethods = 512, MaxWindows = 4096;
        static readonly MethodInfo Fixed = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "FixedUpdate", Type.EmptyTypes);
        static readonly MethodInfo Integrate = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "Integrate", new[] { typeof(Part) });
        static readonly MethodInfo Aero = AccessTools.DeclaredMethod(typeof(FlightIntegrator), "UpdateAerodynamics", new[] { typeof(Part) });
        static readonly MethodInfo Faces = AccessTools.DeclaredMethod(typeof(DragCubeList), "AddSurfaceDragDirection", new[] { typeof(Vector3), typeof(float), typeof(DragCubeList.CubeData).MakeByRefType() });
        static readonly MethodInfo Surface = AccessTools.DeclaredMethod(typeof(PhysicsGlobals), "DragCurveValue", new[] { typeof(PhysicsGlobals.SurfaceCurvesList), typeof(float), typeof(float) });
        static readonly MethodInfo Evaluate = AccessTools.DeclaredMethod(typeof(FloatCurve), "Evaluate", new[] { typeof(float) });
        static readonly MethodInfo ProviderGetter = AccessTools.PropertyGetter(typeof(ModuleLiftingSurface), "IsLifting");
        static readonly MethodInfo InterfaceGetter = AccessTools.PropertyGetter(typeof(ILiftProvider), "IsLifting");
        static readonly MethodInfo CachedMethod = AccessTools.DeclaredMethod(typeof(AeroStepContext), "CachedEvaluate");
        static readonly MethodInfo[] Targets = { Fixed, Aero, Faces, Surface };
        static readonly int[] Tokens = { 100677457, 100677495, 100664780, 100665161 };
        static readonly int[] Instructions = { 501, 554, 274, 71 };
        static AeroStepContext active;
        [ThreadStatic] static Context current;
        readonly Harmony harmony = new Harmony(Owner);
        readonly Vessel vessel;
        readonly Func<bool> capturing;
        readonly int thread = Thread.CurrentThread.ManagedThreadId;
        readonly Context epoch = new Context();
        readonly Window[] windows = new Window[MaxWindows];
        readonly HashSet<MethodBase> graph = new HashSet<MethodBase>();
        readonly HashSet<Type> providerTypes = new HashSet<Type>();
        Part[] parts;
        DragCubeList[] cubes;
        ILiftProvider[] providers;
        Part[][] children;
        bool disposed, disabled, installing, cleaning, acquired;
        int windowCount, verifyWindows;
        long sequence;

        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-aero-step-context/v1", mode, runId, vesselId;
            public string status = "installing", cleanupStatus = "not-installed", detail, lastFallback;
            public string assemblySha256 = StockSha, assemblyMvid = ConvectionSparseKernel.Mvid.ToString("D");
            public string scope = "Original root Integrate, original nine reached Mach Evaluate sites and six lazy exact float values. All directional/weighted arithmetic, recursive part order and native force publication remain original. No global FloatCurve hook or cross-root value reuse.";
            public string ownershipScope = "Reserved frozen research patch/managed-callgraph ownership lease, qualified at installation and cleanup. No per-root Harmony/reflection/hash scan. Root typed guards reject changed part/cube/provider owners and curve alias disagreement before Integrate. Concurrent or within-root curve/native/patch mutation is unsupported; cleanup detection invalidates evidence and does not roll back native forces.";
            public string timingScope = "Query windows charge original root elapsed plus complete context prefix/admission/reset/end bookkeeping; root contains lookup/first-use Evaluate/native force costs. Installation and first invocation retained separately. Verify is original-return same-state numerical proof, not a stock/candidate complete-method speedup comparator; it records at most two captured roots and then runs stock. No FPS claim.";
            public string fallbackScope = "Admission failure runs the entire original root without memoization. Unexpected in-root key/context or nonfinite result stops future reuse and marks this experiment invalid; no synthetic native-force rollback or all-or-none publication claim.";
            public string[] roleOrder = { "surface-tail", "surface", "surface-tip", "surface-multiplier-and-exposure", "cd-power", "body-lift-mach" };
            public int processId, boundParts, patchedMethods, shapeAdmissions, transpilerExecutions, cleanupRecompositions, staticCallsites, errors, originalExceptions, recursionStops, safetyStops, verificationMismatches, droppedWindows;
            public long clockFrequency = Stopwatch.Frequency, installationStartedTicks, installationEndedTicks, cleanupStartedTicks, cleanupEndedTicks;
            public long firstInvocationAdmissionTicks, firstInvocationRootTicks, firstInvocationContextCompleteTicks;
            public bool firstInvocationCaptured;
            public long calls, captureCalls, admittedRoots, captureAdmittedRoots, fallbacks, captureFallbacks, completedRoots, captureCompletedRoots, memoizedCalls, captureMemoizedCalls, originalEvaluations, verificationEvaluations;
            public MethodRow[] methods = new MethodRow[0];
            public Window[] windows = new Window[0];
        }
        [Serializable] public sealed class MethodRow
        {
            public string declaringType, method, assemblyMvid, ilSha256;
            public int metadataToken, instructionCount, shapeApplications, cleanupApplications;
            public bool patched;
            public int[] roles = new int[0];
        }
        [Serializable] public sealed class Window
        {
            public long sequence, fixedStartedTicks, startedTicks, endedTicks, fixedEndedTicks, rootTicks, prefixTicks, admissionTicks, endBookkeepingTicks, contextCompleteTicks, verificationTicks;
            public int frame, parts, inputBits, reachedRoleMask, aeroEntries, faceEntries, surfaceEntries;
            public double universalTime, mach;
            public bool completed, captureActiveAtEnd, firstInvocation, originalReturnVerification;
            public string fallback, mismatch;
            public long[] calls = new long[Roles], hits = new long[Roles], originalEvaluations = new long[Roles];
            public int[] firstValueBits = new int[Roles];
        }
        sealed class Context
        {
            internal AeroStepContext owner;
            internal FlightIntegrator integrator;
            internal Window window;
            internal bool eligible, verifying, captured;
            internal long fixedStart, prefixTicks, rootStart, admissionTicks;
            internal bool firstInvocation;
            internal int inputBits, validMask, aeroEntries, faceEntries, surfaceEntries;
            internal readonly FloatCurve[] curves = new FloatCurve[Roles];
            internal readonly AnimationCurve[] native = new AnimationCurve[Roles];
            internal readonly float[] values = new float[Roles];
        }
        [StructLayout(LayoutKind.Explicit)] struct FloatBits
        {
            [FieldOffset(0)] internal float value;
            [FieldOffset(0)] internal int bits;
        }
        public ReportData Report { get; private set; }

        AeroStepContext(string mode, string runId, Func<bool> capturing)
        {
            this.capturing = capturing; vessel = FlightGlobals.ActiveVessel;
            Report = new ReportData { mode = mode, runId = runId, vesselId = vessel.id.ToString("D"), processId = Process.GetCurrentProcess().Id,
                installationStartedTicks = Stopwatch.GetTimestamp() };
            epoch.owner = this;
            try
            {
                Require(active == null, "One aero step context required.");
                StartupPinnedGameIdentity.Require(typeof(FlightIntegrator).Assembly);
                for (int i = 0; i < Targets.Length; i++) ConvectionSparseKernel.Pin(Targets[i], Targets[i].DeclaringType, Tokens[i]);
                ConvectionSparseKernel.Pin(Integrate, typeof(FlightIntegrator), 100677461);
                ConvectionSparseKernel.Pin(Evaluate, typeof(FloatCurve), 100681489);
                BindParts(); QualifyGraph();
                for (int i = 0; i < windows.Length; i++) windows[i] = new Window();
                active = this; acquired = true; installing = true;
                for (int i = 0; i < Targets.Length; i++)
                {
                    harmony.Patch(Targets[i], prefix: i == 0 ? new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroStepContext), "Prefix")) : null,
                        transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroStepContext), "Transform")),
                        finalizer: i == 0 ? new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AeroStepContext), "Finalizer")) : null);
                    Report.patchedMethods++;
                }
                Require(Report.shapeAdmissions == 4 && Report.staticCallsites == 9 && OwnedGraph(), "Aero root/callsite/ownership installation differs.");
                Report.status = Report.cleanupStatus = "installed";
            }
            catch (Exception error)
            {
                Fault(error); disabled = true; Report.status = "installation-failed";
                RemoveHooks();
            }
            finally { installing = false; Report.installationEndedTicks = Stopwatch.GetTimestamp(); }
        }
        public static AeroStepContext Create(string[] args, string runId, Func<bool> capturing)
        {
            string mode = Environment.GetEnvironmentVariable("CONTINUUM_AERO_STEP_CONTEXT");
            if (string.IsNullOrEmpty(mode)) return null;
            if ((mode != "verify" && mode != "query") || string.IsNullOrEmpty(runId) || capturing == null || FlightGlobals.ActiveVessel == null ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_INTEGRATION_ATTRIBUTION")) ||
                args.Any(arg => arg == "--continuum-callback-attribution" || arg == "--continuum-flight-integrator-update-attribution" ||
                    arg == "--continuum-live-setdrag-provider" || arg == "--continuum-setdrag-stress-candidate" ||
                    (arg.StartsWith("--continuum-convection-replacement=", StringComparison.Ordinal) && arg != "--continuum-convection-replacement=query")))
                throw new InvalidOperationException("Aero step context requires one verify/query environment mode and excludes competing aero/broad/provider observers or convection verification.");
            return new AeroStepContext(mode, runId, capturing);
        }
        static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
        static int Bits(float value) { return new FloatBits { value = value }.bits; }
        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        void BindParts()
        {
            Require(vessel.parts.Count > 0 && vessel.parts.Count <= MaxParts && vessel.rootPart != null, "Bound vessel topology outside domain.");
            parts = vessel.parts.ToArray(); cubes = new DragCubeList[parts.Length]; providers = new ILiftProvider[parts.Length]; children = new Part[parts.Length][];
            var bound = new HashSet<Part>(parts);
            Require(bound.Count == parts.Length && bound.Contains(vessel.rootPart), "Bound topology has duplicate/missing owners.");
            for (int i = 0; i < parts.Length; i++)
            {
                Part part = parts[i]; Require(part != null && part.GetType() == typeof(Part) && part.DragCubes != null, "Unsupported concrete part/cube owner.");
                cubes[i] = part.DragCubes; providers[i] = part.bodyLiftOnlyProvider;
                children[i] = part.children.ToArray();
                foreach (Part child in part.children) Require(bound.Contains(child), "Child outside bound vessel.");
                if (providers[i] == null) continue;
                Type type = providers[i].GetType();
                if (providerTypes.Contains(type)) continue;
                InterfaceMapping map = type.GetInterfaceMap(typeof(ILiftProvider));
                int getter = Array.FindIndex(map.InterfaceMethods, method => Equals(method, InterfaceGetter));
                Require(getter >= 0 && Equals(map.TargetMethods[getter], ProviderGetter), "Unsupported ILiftProvider.IsLifting dispatch target.");
                providerTypes.Add(type);
            }
            Report.boundParts = parts.Length;
        }
        void QualifyGraph()
        {
            var pending = new Stack<MethodBase>(); pending.Push(Integrate); pending.Push(Fixed); pending.Push(ProviderGetter);
            pending.Push(AccessTools.PropertyGetter(typeof(FloatCurve), "Curve"));
            pending.Push(AccessTools.PropertyGetter(typeof(PhysicsGlobals), "SurfaceCurves"));
            pending.Push(AccessTools.PropertyGetter(typeof(PhysicsGlobals), "DragCurveCdPower"));
            pending.Push(AccessTools.PropertyGetter(typeof(PhysicsGlobals), "BodyLiftCurve"));
            while (pending.Count != 0)
            {
                MethodBase method = pending.Pop();
                if (!graph.Add(method)) continue;
                Require(graph.Count <= MaxMethods && Harmony.GetPatchInfo(method)?.Owners.Any() != true, "Original aero callgraph is too broad or already owned.");
                MethodBody body = method.GetMethodBody();
                if (method.Module.Assembly != typeof(FlightIntegrator).Assembly || Equals(method, Fixed)) continue;
                Require(method.Module.ModuleVersionId == ConvectionSparseKernel.Mvid && body != null && body.ExceptionHandlingClauses.Count == 0,
                    "Original reachable managed method identity/exception shape differs.");
                ILGenerator generator;
                foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method, out generator))
                {
                    Require(instruction.opcode != OpCodes.Calli && instruction.opcode != OpCodes.Ldftn && instruction.opcode != OpCodes.Ldvirtftn,
                        "Unknown indirect managed callback in aero root.");
                    if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt && instruction.opcode != OpCodes.Newobj) continue;
                    MethodBase called = instruction.operand as MethodBase;
                    Require(called != null, "Unknown managed call target.");
                    if (Equals(called, InterfaceGetter)) { pending.Push(ProviderGetter); continue; }
                    Require(!called.DeclaringType.IsInterface, "Unsupported reachable interface callback.");
                    var virtualCall = called as MethodInfo;
                    Require(virtualCall == null || !virtualCall.IsVirtual || virtualCall.IsFinal || called.DeclaringType == typeof(FlightIntegrator) ||
                        called.Module.Assembly != typeof(FlightIntegrator).Assembly, "Unsupported reachable virtual game callback.");
                    pending.Push(called);
                }
            }
            Report.methods = graph.OrderBy(method => method.Module.ModuleVersionId).ThenBy(method => method.MetadataToken).Select(method => new MethodRow {
                declaringType = method.DeclaringType.FullName, method = method.Name, metadataToken = method.MetadataToken,
                assemblyMvid = method.Module.ModuleVersionId.ToString("D"), ilSha256 = method.GetMethodBody() == null ? null : ConvectionSparseKernel.Hash(method.GetMethodBody().GetILAsByteArray()),
                patched = Targets.Any(target => Equals(target, method)) }).ToArray();
        }
        bool OwnedGraph()
        {
            foreach (MethodBase method in graph)
            {
                int index = Array.FindIndex(Targets, target => Equals(target, method)); Patches info = Harmony.GetPatchInfo(method);
                if (index < 0) { if (info?.Owners.Any() == true) return false; continue; }
                if (info == null || info.Owners.Any(owner => owner != Owner) || info.Transpilers.Count != 1 || info.Postfixes.Count != 0 ||
                    info.Prefixes.Count != (index == 0 ? 1 : 0) || info.Finalizers.Count != (index == 0 ? 1 : 0) ||
                    !Equals(info.Transpilers[0].PatchMethod, AccessTools.DeclaredMethod(typeof(AeroStepContext), "Transform"))) return false;
                if (index == 0 && (!Equals(info.Prefixes[0].PatchMethod, AccessTools.DeclaredMethod(typeof(AeroStepContext), "Prefix")) ||
                    !Equals(info.Finalizers[0].PatchMethod, AccessTools.DeclaredMethod(typeof(AeroStepContext), "Finalizer")))) return false;
            }
            return true;
        }
        static int Role(CodeInstruction[] code, int index, MethodBase method)
        {
            if (!code[index].Calls(Evaluate)) return -1;
            if (Equals(method, Surface))
            {
                Require(index >= 2 && code[index - 1].opcode == OpCodes.Ldarg_2 && code[index - 2].opcode == OpCodes.Ldfld, "Surface Mach operand shape differs.");
                FieldInfo field = code[index - 2].operand as FieldInfo;
                Require(field != null && field.DeclaringType == typeof(PhysicsGlobals.SurfaceCurvesList), "Surface role owner differs.");
                switch (field.Name) { case "dragCurveTail": return 0; case "dragCurveSurface": return 1; case "dragCurveTip": return 2; case "dragCurveMultiplier": return 3; }
                throw new InvalidOperationException("Unknown surface Mach role.");
            }
            if (Equals(method, Faces) && code[index - 1].opcode == OpCodes.Ldarg_2)
            {
                Require(index >= 2 && code[index - 2].opcode == OpCodes.Ldfld, "Six-face Mach operand shape differs.");
                FieldInfo field = code[index - 2].operand as FieldInfo;
                Require(field != null && field.DeclaringType == typeof(DragCubeList), "Six-face Mach role owner differs.");
                if (field.Name == "DragCurveCdPower") return 4;
                Require(field.Name == "DragCurveMultiplier", "Unknown six-face Mach role."); return 3;
            }
            if (Equals(method, Aero))
            {
                Require(index >= 4 && code[index - 1].opcode == OpCodes.Conv_R4 && code[index - 2].opcode == OpCodes.Ldfld &&
                    Equals(code[index - 2].operand, AccessTools.Field(typeof(FlightIntegrator), "mach")) && code[index - 3].opcode == OpCodes.Ldarg_0 &&
                    code[index - 4].opcode == OpCodes.Ldfld && Equals(code[index - 4].operand, AccessTools.Field(typeof(PhysicsGlobals.LiftingSurfaceCurve), "liftMachCurve")),
                    "Body-lift Mach operand shape differs."); return 5;
            }
            return -1;
        }
        static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> source, ILGenerator generator, MethodBase __originalMethod)
        {
            AeroStepContext owner = active; Require(owner != null, "Aero context patch owner missing.");
            int target = Array.FindIndex(Targets, method => Equals(method, __originalMethod)); Require(target >= 0, "Unknown aero context patch identity.");
            var code = source.Select(item => new CodeInstruction(item)).ToArray();
            Require(code.Length == Instructions[target] && code.All(item => item.blocks.Count == 0), "Pinned aero context IL shape differs.");
            var result = new List<CodeInstruction>(); var roles = new List<int>(); int roots = 0;
            if (target != 0)
            {
                var entry = new CodeInstruction(OpCodes.Ldc_I4, target - 1); entry.labels.AddRange(code[0].labels); code[0].labels.Clear(); result.Add(entry);
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AeroStepContext), "CountEntry")));
            }
            LocalBuilder integrator = generator.DeclareLocal(typeof(FlightIntegrator)), part = generator.DeclareLocal(typeof(Part));
            for (int i = 0; i < code.Length; i++)
            {
                if (target == 0 && code[i].Calls(Integrate))
                {
                    roots++; var first = new CodeInstruction(OpCodes.Stloc, part); first.labels.AddRange(code[i].labels); code[i].labels.Clear(); result.Add(first);
                    result.Add(new CodeInstruction(OpCodes.Stloc, integrator)); result.Add(new CodeInstruction(OpCodes.Ldloc, integrator)); result.Add(new CodeInstruction(OpCodes.Ldloc, part));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AeroStepContext), "BeginRoot")));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, integrator)); result.Add(new CodeInstruction(OpCodes.Ldloc, part)); result.Add(code[i]);
                    result.Add(new CodeInstruction(OpCodes.Ldloc, integrator));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AeroStepContext), "EndRoot"))); continue;
                }
                int role = Role(code, i, __originalMethod);
                if (role < 0) { result.Add(code[i]); continue; }
                Require(i == 0 || (code[i - 1].opcode != OpCodes.Tailcall && code[i - 1].opcode != OpCodes.Constrained), "Unsupported Mach call prefix.");
                var load = new CodeInstruction(OpCodes.Ldc_I4, role); load.labels.AddRange(code[i].labels); result.Add(load); result.Add(new CodeInstruction(OpCodes.Call, CachedMethod)); roles.Add(role);
            }
            int[][] expected = { new int[0], new[] { 5 }, new[] { 4, 3 }, new[] { 0, 1, 3, 1, 2, 3 } };
            Require(roles.SequenceEqual(expected[target]) && (target != 0 || roots == 1), "Nine original Mach roles/root order differs.");
            MethodRow row = owner.Report.methods.Single(item => item.assemblyMvid == __originalMethod.Module.ModuleVersionId.ToString("D") && item.metadataToken == __originalMethod.MetadataToken);
            if (row.shapeApplications == 0)
            {
                Require(owner.installing && !owner.cleaning, "First aero context admission outside installation.");
                row.roles = roles.ToArray(); row.instructionCount = code.Length; owner.Report.shapeAdmissions++; owner.Report.staticCallsites += roles.Count;
            }
            else
            {
                Require(owner.cleaning && row.roles.SequenceEqual(roles) && row.instructionCount == code.Length, "Aero context reapplication outside exact owned cleanup.");
                row.cleanupApplications++; owner.Report.cleanupRecompositions++;
            }
            row.shapeApplications++; owner.Report.transpilerExecutions++; return result;
        }
        static void Prefix(FlightIntegrator __instance, out Context __state)
        {
            long start = Stopwatch.GetTimestamp(); AeroStepContext owner = active; __state = null;
            if (owner == null || owner.disposed || owner.disabled || Thread.CurrentThread.ManagedThreadId != owner.thread || !ReferenceEquals(__instance.Vessel, owner.vessel)) return;
            if (current != null) { current.eligible = false; owner.Report.recursionStops++; owner.disabled = true; return; }
            __state = owner.epoch; __state.integrator = __instance; __state.window = null; __state.eligible = false; __state.validMask = 0;
            __state.rootStart = 0; __state.firstInvocation = false;
            __state.aeroEntries = __state.faceEntries = __state.surfaceEntries = 0; __state.fixedStart = start; __state.captured = owner.capturing();
            current = __state; __state.prefixTicks = Stopwatch.GetTimestamp() - start;
        }
        bool Admit(FlightIntegrator integrator, Part root)
        {
            if (integrator.GetType() != typeof(FlightIntegrator) || !ReferenceEquals(FlightGlobals.ActiveVessel, vessel) || !ReferenceEquals(root, vessel.rootPart) ||
                vessel.packed || !vessel.loaded || vessel.parts.Count != parts.Length) return Reject("bound-context-or-topology-changed");
            float mach = (float)integrator.mach; if (!Finite(mach)) return Reject("nonfinite-mach");
            PhysicsGlobals.SurfaceCurvesList surface = PhysicsGlobals.SurfaceCurves;
            epoch.curves[0] = surface.dragCurveTail; epoch.curves[1] = surface.dragCurveSurface; epoch.curves[2] = surface.dragCurveTip; epoch.curves[3] = surface.dragCurveMultiplier;
            epoch.curves[4] = PhysicsGlobals.DragCurveCdPower; epoch.curves[5] = PhysicsGlobals.BodyLiftCurve == null ? null : PhysicsGlobals.BodyLiftCurve.liftMachCurve;
            for (int i = 0; i < Roles; i++)
            {
                if (epoch.curves[i] == null || epoch.curves[i].Curve == null) return Reject("missing-curve-role-" + i);
                epoch.native[i] = epoch.curves[i].Curve;
            }
            for (int i = 0; i < parts.Length; i++)
            {
                Part part = parts[i]; DragCubeList cube = cubes[i];
                if (!ReferenceEquals(vessel.parts[i], part) || !ReferenceEquals(part.DragCubes, cube) || !ReferenceEquals(part.bodyLiftOnlyProvider, providers[i]) ||
                    part.children.Count != children[i].Length) return Reject("part-cube-provider-owner-changed");
                for (int child = 0; child < children[i].Length; child++)
                    if (!ReferenceEquals(part.children[child], children[i][child])) return Reject("part-child-topology-changed");
                PhysicsGlobals.SurfaceCurvesList row = cube.SurfaceCurves;
                if (!ReferenceEquals(row.dragCurveTail, epoch.curves[0]) || !ReferenceEquals(row.dragCurveSurface, epoch.curves[1]) ||
                    !ReferenceEquals(row.dragCurveTip, epoch.curves[2]) || !ReferenceEquals(row.dragCurveMultiplier, epoch.curves[3]) ||
                    !ReferenceEquals(cube.DragCurveMultiplier, epoch.curves[3]) || !ReferenceEquals(cube.DragCurveCdPower, epoch.curves[4])) return Reject("part-curve-role-aliases-differ");
            }
            epoch.inputBits = Bits(mach); return true;
        }
        bool Reject(string reason) { Report.lastFallback = reason; return false; }
        static void BeginRoot(FlightIntegrator integrator, Part root)
        {
            Context context = current; if (context == null || !ReferenceEquals(context.integrator, integrator)) return;
            AeroStepContext owner = context.owner; long admission = Stopwatch.GetTimestamp(); owner.Report.calls++;
            if (context.captured) owner.Report.captureCalls++;
            context.verifying = owner.Report.mode == "verify";
            if (context.verifying && (!context.captured || owner.verifyWindows >= 2)) return;
            try
            {
                context.validMask = 0; context.eligible = owner.Admit(integrator, root);
                context.firstInvocation = owner.sequence == 0;
                Window window = null;
                if (context.captured)
                {
                    if (owner.windowCount >= MaxWindows) { owner.Report.droppedWindows++; context.eligible = false; }
                    else
                    {
                        window = owner.windows[owner.windowCount++]; context.window = window; window.sequence = owner.sequence; window.frame = Time.frameCount;
                        window.parts = owner.parts.Length; window.mach = integrator.mach; window.universalTime = Planetarium.GetUniversalTime(); window.inputBits = context.inputBits;
                        window.fixedStartedTicks = context.fixedStart; window.prefixTicks = context.prefixTicks; window.originalReturnVerification = context.verifying;
                        window.firstInvocation = context.firstInvocation; window.fallback = context.eligible ? null : owner.Report.lastFallback;
                        if (context.verifying && context.eligible) owner.verifyWindows++;
                    }
                }
                owner.sequence++;
                if (context.eligible) { owner.Report.admittedRoots++; if (context.captured) owner.Report.captureAdmittedRoots++; }
                else { owner.Report.fallbacks++; if (context.captured) owner.Report.captureFallbacks++; }
                long now = Stopwatch.GetTimestamp(); context.admissionTicks = now - admission; context.rootStart = now;
                if (window != null) { window.admissionTicks = context.admissionTicks; window.startedTicks = now; }
            }
            catch (Exception error) { owner.Fault(error); context.eligible = false; owner.disabled = true; }
        }
        static void CountEntry(int kind)
        {
            Context context = current; if (context == null || !context.eligible) return;
            if (kind == 0) context.aeroEntries++; else if (kind == 1) context.faceEntries++; else context.surfaceEntries++;
        }
        static float CachedEvaluate(FloatCurve curve, float input, int role)
        {
            Context context = current;
            if (context == null || !context.eligible) return curve.Evaluate(input);
            AeroStepContext owner = context.owner; Window window = context.window;
            if (role < 0 || role >= Roles || !ReferenceEquals(curve, context.curves[role]) || Bits(input) != context.inputBits)
            {
                owner.StopReuse("unexpected-in-root-curve-input-key"); return curve.Evaluate(input);
            }
            if (window != null) window.calls[role]++;
            int bit = 1 << role;
            if ((context.validMask & bit) == 0)
            {
                float value = curve.Evaluate(input); owner.Report.originalEvaluations++;
                if (window != null) { window.originalEvaluations[role]++; window.firstValueBits[role] = Bits(value); }
                if (!Finite(value)) { owner.StopReuse("nonfinite-first-role-result"); return value; }
                context.values[role] = value; context.validMask |= bit; return value;
            }
            if (window != null) window.hits[role]++;
            if (context.verifying)
            {
                long start = Stopwatch.GetTimestamp(); float original = curve.Evaluate(input); owner.Report.verificationEvaluations++;
                if (window != null) { window.verificationTicks += Stopwatch.GetTimestamp() - start; window.originalEvaluations[role]++; }
                if (Bits(original) != Bits(context.values[role]))
                {
                    owner.Report.verificationMismatches++; if (window != null) window.mismatch = "role=" + role + ",cachedBits=" + Bits(context.values[role]) + ",originalBits=" + Bits(original);
                    owner.StopReuse("same-state-value-bit-mismatch");
                }
                return original;
            }
            owner.Report.memoizedCalls++; if (context.captured) owner.Report.captureMemoizedCalls++;
            return context.values[role];
        }
        void StopReuse(string reason)
        {
            Report.safetyStops++; Report.lastFallback = reason; disabled = true;
            if (current != null) { current.eligible = false; if (current.window != null) current.window.fallback = reason; }
        }
        static void EndRoot(FlightIntegrator integrator)
        {
            long ended = Stopwatch.GetTimestamp(); Context context = current; if (context == null || !ReferenceEquals(context.integrator, integrator)) return;
            Window window = context.window; AeroStepContext owner = context.owner;
            if (window != null)
            {
                window.endedTicks = ended; window.rootTicks = ended - window.startedTicks; window.captureActiveAtEnd = owner.capturing();
                window.aeroEntries = context.aeroEntries; window.faceEntries = context.faceEntries; window.surfaceEntries = context.surfaceEntries; window.reachedRoleMask = context.validMask;
                window.completed = true;
                if (window.fallback == null && (window.aeroEntries != window.parts || window.surfaceEntries != 6 * window.faceEntries ||
                    window.calls.Sum() < 3L * window.surfaceEntries + 6L * window.faceEntries))
                    owner.StopReuse("original-callsite-execution-coverage-differs");
            }
            context.eligible = false;
            if (context.rootStart != 0)
            {
                owner.Report.completedRoots++; if (context.captured) owner.Report.captureCompletedRoots++;
                if (context.firstInvocation)
                {
                    owner.Report.firstInvocationAdmissionTicks = context.admissionTicks; owner.Report.firstInvocationRootTicks = ended - context.rootStart;
                    owner.Report.firstInvocationContextCompleteTicks = context.prefixTicks + context.admissionTicks + Stopwatch.GetTimestamp() - context.rootStart;
                    owner.Report.firstInvocationCaptured = context.captured;
                }
            }
            if (window != null)
            {
                window.endBookkeepingTicks = Stopwatch.GetTimestamp() - ended;
                window.contextCompleteTicks = window.prefixTicks + window.admissionTicks + window.rootTicks + window.endBookkeepingTicks;
            }
        }
        static Exception Finalizer(Exception __exception, Context __state)
        {
            if (__state == null) return __exception;
            try
            {
                if (__exception != null) { __state.owner.Report.originalExceptions++; __state.owner.disabled = true; }
                if (__state.window != null)
                {
                    __state.window.fixedEndedTicks = Stopwatch.GetTimestamp();
                    if (__exception != null) __state.window.completed = false;
                    if (__state.window.endedTicks == 0) __state.window.completed = false;
                }
            }
            finally { __state.eligible = false; if (ReferenceEquals(current, __state)) current = null; }
            return __exception;
        }
        void Fault(Exception error) { Report.errors++; Report.detail = error.GetType().Name + ": " + error.Message; }
        void RemoveHooks()
        {
            if (!acquired) { Report.cleanupStatus = "not-installed"; return; }
            cleaning = true;
            try
            {
                harmony.UnpatchAll(Owner);
                Report.cleanupStatus = Targets.Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(Owner) == true) ? "cleanup-error" : "removed-owned-hooks";
            }
            catch (Exception error) { Fault(error); Report.cleanupStatus = "cleanup-error"; }
            finally { cleaning = false; if (ReferenceEquals(active, this)) active = null; }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; Report.cleanupStartedTicks = Stopwatch.GetTimestamp();
            if (Report.patchedMethods == Targets.Length && !OwnedGraph()) { Report.errors++; Report.detail = "Frozen original callgraph ownership changed; native force effects are not rolled back."; }
            RemoveHooks(); Report.windows = windows.Take(windowCount).ToArray(); Report.cleanupEndedTicks = Stopwatch.GetTimestamp();
            string reason; Report.status = IsValidFinal(out reason) ? "completed-experimental" : "invalid";
            if (Report.status == "invalid" && Report.detail == null) Report.detail = reason;
        }
        public bool IsValidFinal(out string reason)
        {
            if (!disposed || Report.patchedMethods != 4 || Report.shapeAdmissions != 4 || Report.staticCallsites != 9 || Report.cleanupStatus != "removed-owned-hooks" ||
                Report.transpilerExecutions != Report.shapeAdmissions + Report.cleanupRecompositions)
            { reason = "Installation/original shape/cleanup incomplete."; return false; }
            if (Report.errors != 0 || Report.originalExceptions != 0 || Report.recursionStops != 0 || Report.safetyStops != 0 || Report.verificationMismatches != 0 || Report.droppedWindows != 0 || Report.captureFallbacks != 0)
            { reason = "Fault, unsupported owner/input/result, mismatch or incomplete coverage."; return false; }
            if (Report.captureCompletedRoots <= 0 || Report.captureAdmittedRoots <= 0 || Report.windows.Any(window => !window.completed || window.rootTicks < 0 || window.contextCompleteTicks < window.rootTicks))
            { reason = "No complete captured root coverage."; return false; }
            if (Report.mode == "verify" && (verifyWindows != 2 || Report.windows.Length != 2 || Report.windows.Any(window => !window.originalReturnVerification || window.hits.Sum() <= 0)))
            { reason = "Two original-return bitwise witnesses incomplete."; return false; }
            if (Report.mode == "query" && (Report.captureMemoizedCalls <= 0 || Report.windows.Any(window => window.originalReturnVerification)))
            { reason = "No captured query reuse or mixed verification cost."; return false; }
            reason = null; return true;
        }
    }
}
