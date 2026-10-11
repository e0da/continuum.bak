using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class CheckpointReloadQualification : MonoBehaviour
    {
        const string Lock = "continuum-checkpoint-reload";
        const string Hook = "continuum.checkpoint-reload-camera";
        static CheckpointReloadQualification owner;
        readonly List<EventRow> events = new List<EventRow>();
        readonly List<string> images = new List<string>();
        readonly Harmony harmony = new Harmony(Hook);
        ReportData report;
        string directory, saveName, savePath;
        bool finished, cameraOwned, lockOwned, sunward, cameraReleaseObserved;
        float began;
        Vessel vessel;
        FlightCamera flight;
        Transform pivot;
        Vector3 pivotPosition, flightPosition;
        Quaternion pivotRotation, flightRotation;
        float distance;
        CameraPose[] cameras;
        SnapshotRow before;
        Game reloadedGame;

        [Serializable] public sealed class EventRow { public string phase; public double universalTime, wallSeconds; public int frame; }
        [Serializable] public sealed class ValueRow { public string key, value; public bool numeric; public double number; }
        [Serializable] public sealed class ResourceRow { public uint partId; public string name; public double amount; }
        [Serializable] public sealed class PartRow { public uint flightId, persistentId, parentFlightId; public string name; public int instanceId; public string[] crew, modules; }
        [Serializable] public sealed class MembershipRow { public string vesselId; public uint[] parts; }
        [Serializable] public sealed class SnapshotRow
        {
            public string vesselId, bodyName; public uint persistentId; public int instanceId;
            public double universalTime; public double[] orbitPosition, orbitVelocity;
            public PartRow[] parts; public ResourceRow[] resources; public ValueRow[] fields;
            public string[] excludedFields;
        }
        [Serializable] public sealed class ReportData
        {
            public string schema = "continuum-checkpoint-reload/v1", status = "running", cleanupStatus = "owned", reason, runId, sourceSha256, savedSha256, savedPath;
            public string scope = "Quiet vacuum checkpoint; stock save and quickload completion; persistent primitive KSPFields only. Not arbitrary module OnSave, full scenario equality, deterministic replay, FPS or visual equivalence qualification.";
            public int processId, faults, cameraCallbacks, expectedParts;
            public bool sourceUnchanged, savedUnchanged, newVesselObject, newPartObjects, identityRestored, resourcesRestored, crewRestored, modulesRestored, fieldsRestored, orbitRestored, timeRestored, departureObserved, scenarioModulesUpdated, postLoadNotified, hooksRemoved, controlLockRemoved, cameraRestored;
            public double positionToleranceMeters = .01, velocityToleranceMetersPerSecond = .00001, resourceTolerance = .000001, fieldAbsoluteTolerance = .000001, fieldRelativeTolerance = .000001, maximumReloadSimulationSeconds = 2;
            public double positionErrorMeters, velocityErrorMetersPerSecond, reloadSimulationSeconds, departureDeltaVMetersPerSecond, electricChargeRateBudget;
            public int screenWidth, screenHeight;
            public bool beforeCameraSunward, afterCameraSunward;
            public double beforeHorizonClearanceMeters, afterHorizonClearanceMeters;
            public double[] beforeSunDirection, afterSunDirection;
            public double[] departureVelocityBefore, departureVelocityAfter;
            public SnapshotRow before, after; public MembershipRow[] memberships; public EventRow[] events; public string[] screenshots;
        }
        sealed class CameraPose { public Camera camera; public Vector3 position; public Quaternion rotation; }
        public static bool Requested(string[] args) { return args.Contains("--continuum-checkpoint-reload"); }
        static string RunId(string[] args)
        {
            const string prefix = "--continuum-research-run=";
            return args.Single(a => a.StartsWith(prefix, StringComparison.Ordinal)).Substring(prefix.Length);
        }
        public void Awake()
        {
            if (!Requested(Environment.GetCommandLineArgs())) { enabled = false; return; }
            if (owner != null) { enabled = false; return; }
            owner = this; DontDestroyOnLoad(gameObject); began = Time.realtimeSinceStartup;
            directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData", "checkpoint-reload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            report = new ReportData { runId = RunId(Environment.GetCommandLineArgs()), processId = System.Diagnostics.Process.GetCurrentProcess().Id, sourceSha256 = ScaleCheckpointLoadState.SourceSha256 };
        }
        IEnumerator Start()
        {
            if (owner != this) yield break;
            while (!PhysicalReady()) { if(finished)yield break; if (Time.realtimeSinceStartup - began > 90) { Fail(new TimeoutException("Initial checkpoint readiness timeout.")); yield break; } yield return null; }
            if (!Try(Admit)) yield break;
            yield return new WaitForFixedUpdate(); yield return new WaitForEndOfFrame();
            if (!Try(() => { Image("before"); Record("before-image"); SaveAndDepart(); })) yield break;
            yield return new WaitForFixedUpdate();
            if (!Try(Reload)) yield break;
            float loadStart = Time.realtimeSinceStartup;
            while (!PhysicalReady() || ReferenceEquals(FlightGlobals.ActiveVessel, vessel))
            {
                if(finished)yield break;
                if (Time.realtimeSinceStartup - loadStart > 90) { Fail(new TimeoutException("Reload physical readiness timeout.")); yield break; }
                yield return null;
            }
            if (!Try(() => { vessel = FlightGlobals.ActiveVessel; Verify(); BeginCamera(); Record("reloaded"); })) yield break;
            yield return new WaitForEndOfFrame();
            if (!Try(() => { Image("after"); Record("after-image"); Cleanup(); report.status = report.faults == 0 ? "complete-experimental" : "invalid"; Export(); })) yield break;
            finished = true; Application.Quit(report.status == "complete-experimental" ? 0 : 2);
        }
        static bool PhysicalReady()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            return ScaleCheckpointLoadState.Ready && HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && v != null && v.loaded && !v.packed && !v.HoldPhysics && v.rootPart != null && v.parts.All(p => p != null && p.started);
        }
        void Admit()
        {
            vessel = FlightGlobals.ActiveVessel;
            const string partsPrefix = "--continuum-scale-parts=";
            string selectedParts = Environment.GetCommandLineArgs().Single(a=>a.StartsWith(partsPrefix,StringComparison.Ordinal)).Substring(partsPrefix.Length);
            int expected;
            if(!int.TryParse(selectedParts,NumberStyles.None,CultureInfo.InvariantCulture,out expected)||expected<1||vessel.parts.Count!=expected)throw new InvalidOperationException("Selected part count differs from reload fixture.");
            report.expectedParts=expected;
            if (!ScaleCheckpointLoadState.SourceUnchanged() || vessel.id != ScaleCheckpointLoadState.VesselId || FlightDriver.Pause || MapView.MapIsEnabled || TimeWarp.CurrentRate != 1 || vessel.landed || vessel.splashed || vessel.altitude <= vessel.mainBody.atmosphereDepth ||
                FlightGlobals.Vessels.Any(v => v != null && v.loaded && !v.packed && v != vessel) || vessel.Autopilot.Enabled || vessel.ActionGroups[KSPActionGroup.SAS] || vessel.ActionGroups[KSPActionGroup.RCS] || vessel.ctrlState.mainThrottle != 0 || vessel.parts.Any(p => p.Modules.OfType<ModuleEngines>().Any(e => e.EngineIgnited)))
                throw new InvalidOperationException("Reload diagnostic requires one quiet physical vacuum vessel and immutable selected source.");
            InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.TIMEWARP, Lock); lockOwned = true;
            BeginCamera(); Record("admitted");
        }
        void SaveAndDepart()
        {
            before = Snapshot(vessel, Planetarium.GetUniversalTime()); report.before = before;
            report.electricChargeRateBudget = vessel.parts.Sum(p => p.Modules.OfType<ModuleCommand>().Sum(m => m.resHandler.inputResources.Where(r => r.name == "ElectricCharge").Sum(r => r.rate)));
            saveName = "continuum-reload-" + Guid.NewGuid().ToString("N");
            savePath = Path.Combine(KSPUtil.ApplicationRootPath, "saves", ScaleCheckpointLoadState.Save, saveName + ".sfs");
            if (File.Exists(savePath)) throw new InvalidOperationException("Disposable checkpoint already exists.");
            Game state = HighLogic.CurrentGame.Updated(GameScenes.FLIGHT);
            report.memberships = Memberships(state);
            string result = GamePersistence.SaveGame(state, saveName, ScaleCheckpointLoadState.Save, SaveMode.ABORT);
            if (result != saveName || !File.Exists(savePath)) throw new InvalidOperationException("Stock save did not publish the owned checkpoint.");
            report.savedPath = savePath; report.savedSha256 = ScaleCheckpointLoadState.HashFile(savePath); Record("saved"); Export();
            Rigidbody rb = vessel.rootPart.Rigidbody;
            if (rb == null || rb.isKinematic) throw new InvalidOperationException("Departure requires the live root rigidbody.");
            Vector3 original = rb.velocity, change = vessel.transform.right * .25f;
            vessel.SetWorldVelocity((Vector3d)(original + change));
            report.departureVelocityBefore=V(original);report.departureVelocityAfter=V(rb.velocity);
            report.departureDeltaVMetersPerSecond = (rb.velocity - original).magnitude;
            report.departureObserved = Math.Abs(report.departureDeltaVMetersPerSecond - .25) <= .00001;
            if (!report.departureObserved) throw new InvalidOperationException("Declared departure was not observed.");
            Record("departed");
        }
        void Reload()
        {
            EndCamera();
            if (lockOwned) { InputLockManager.RemoveControlLock(Lock); lockOwned = false; }
            if (ScaleCheckpointLoadState.HashFile(savePath) != report.savedSha256) throw new InvalidOperationException("Owned checkpoint changed before reload.");
            reloadedGame = GamePersistence.LoadGame(saveName, ScaleCheckpointLoadState.Save, true, true);
            if (reloadedGame == null || !reloadedGame.compatible || reloadedGame.flightState == null || reloadedGame.startScene != GameScenes.FLIGHT || !SameMemberships(report.memberships, Memberships(reloadedGame)))
                throw new InvalidOperationException("Reloaded game is incompatible or whole-game partition differs.");
            Record("reload-requested"); Export();
            GamePersistence.UpdateScenarioModules(reloadedGame); report.scenarioModulesUpdated = true;
            ConfigNode node = ConfigNode.Load(savePath); if (node == null) throw new InvalidOperationException("Owned saved ConfigNode is unavailable.");
            GameEvents.onGameStatePostLoad.Fire(node); report.postLoadNotified = true;
            if(HighLogic.SaveFolder!=ScaleCheckpointLoadState.Save)throw new InvalidOperationException("Selected save folder changed before reload.");
            FlightDriver.StartAndFocusVessel(reloadedGame, reloadedGame.flightState.activeVesselIdx);
        }
        static MembershipRow[] Memberships(Game game)
        {
            return game.flightState.protoVessels.OrderBy(v => v.vesselID).Select(v => new MembershipRow { vesselId = v.vesselID.ToString("D"), parts = v.protoPartSnapshots.Select(p => p.flightID).OrderBy(id => id).ToArray() }).ToArray();
        }
        static bool SameMemberships(MembershipRow[] a, MembershipRow[] b)
        { return a.Length == b.Length && a.Zip(b, (x,y) => x.vesselId == y.vesselId && x.parts.SequenceEqual(y.parts)).All(ok => ok); }
        static double[] V(Vector3d v) { return new[] { v.x, v.y, v.z }; }
        static Vector3d V(double[] v) { return new Vector3d(v[0], v[1], v[2]); }
        static SnapshotRow Snapshot(Vessel v, double epoch)
        {
            Vector3d position, velocity; v.orbit.GetOrbitalStateVectorsAtUT(epoch, out position, out velocity);
            if(V(position).Concat(V(velocity)).Any(x=>double.IsNaN(x)||double.IsInfinity(x))||v.parts.SelectMany(p=>p.Resources.Cast<PartResource>()).Any(r=>double.IsNaN(r.amount)||double.IsInfinity(r.amount)||r.amount<0))throw new InvalidOperationException("Nonfinite orbital or resource snapshot.");
            var fields = new List<ValueRow>(); var excluded = new List<string>();
            foreach (Part part in v.parts.OrderBy(p => p.flightID)) for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                for (Type type = module.GetType(); type != null && typeof(PartModule).IsAssignableFrom(type); type = type.BaseType)
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(f => f.Name, StringComparer.Ordinal))
                    {
                        var attribute = field.GetCustomAttributes(typeof(KSPField), true).Cast<KSPField>().SingleOrDefault();
                        if (attribute == null || !attribute.isPersistant) continue;
                        string key = part.flightID + "/" + i + "/" + type.FullName + "/" + field.Name;
                        object value = field.GetValue(module); Type ft = field.FieldType;
                        if (ft == typeof(string) || ft == typeof(bool) || ft.IsEnum) fields.Add(new ValueRow { key = key, value = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture) });
                        else if (ft == typeof(float) || ft == typeof(double))
                        {
                            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException("Nonfinite persistent module field: " + key);
                            fields.Add(new ValueRow { key = key, numeric = true, number = number, value = Convert.ToString(value, CultureInfo.InvariantCulture) });
                        }
                        else if(ft.IsPrimitive||ft==typeof(decimal))fields.Add(new ValueRow{key=key,value=Convert.ToString(value,CultureInfo.InvariantCulture)});
                        else excluded.Add(key + ":" + ft.FullName);
                    }
            }
            return new SnapshotRow { vesselId = v.id.ToString("D"), bodyName=v.mainBody.bodyName, persistentId = v.persistentId, instanceId = v.GetInstanceID(), universalTime = Planetarium.GetUniversalTime(), orbitPosition = V(position), orbitVelocity = V(velocity), fields = fields.OrderBy(f => f.key, StringComparer.Ordinal).ToArray(), excludedFields = excluded.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                parts = v.parts.OrderBy(p => p.flightID).Select(p => new PartRow { flightId = p.flightID, persistentId = p.persistentId, parentFlightId=p.parent==null?0:p.parent.flightID, instanceId = p.GetInstanceID(), name = p.partInfo.name, crew = p.protoModuleCrew.Select(c => c.name).OrderBy(s => s, StringComparer.Ordinal).ToArray(), modules = p.Modules.Cast<PartModule>().Select(m => m.GetType().AssemblyQualifiedName).ToArray() }).ToArray(),
                resources = v.parts.OrderBy(p => p.flightID).SelectMany(p => p.Resources.Cast<PartResource>().OrderBy(r => r.resourceName, StringComparer.Ordinal).Select(r => new ResourceRow { partId = p.flightID, name = r.resourceName, amount = r.amount })).ToArray() };
        }
        void Verify()
        {
            if (!ReferenceEquals(HighLogic.CurrentGame, reloadedGame) || HighLogic.SaveFolder != ScaleCheckpointLoadState.Save || !PhysicalReady()) throw new InvalidOperationException("Reload live owner differs.");
            SnapshotRow after = Snapshot(vessel, before.universalTime); report.after = after;
            report.reloadSimulationSeconds = after.universalTime - before.universalTime;
            report.timeRestored = report.reloadSimulationSeconds >= 0 && report.reloadSimulationSeconds <= report.maximumReloadSimulationSeconds;
            report.newVesselObject = after.instanceId != before.instanceId;
            report.identityRestored = before.vesselId == after.vesselId && before.bodyName==after.bodyName && before.persistentId == after.persistentId && before.parts.Length == after.parts.Length && before.parts.Zip(after.parts, (a,b) => a.flightId == b.flightId && a.persistentId == b.persistentId && a.parentFlightId==b.parentFlightId && a.name == b.name).All(ok => ok);
            report.newPartObjects = before.parts.Length == after.parts.Length && before.parts.Zip(after.parts, (a,b) => a.instanceId != b.instanceId).All(ok => ok);
            report.crewRestored = before.parts.Length == after.parts.Length && before.parts.Zip(after.parts, (a,b) => a.crew.SequenceEqual(b.crew)).All(ok => ok);
            report.modulesRestored = before.parts.Length == after.parts.Length && before.parts.Zip(after.parts, (a,b) => a.modules.SequenceEqual(b.modules)).All(ok => ok);
            report.fieldsRestored = before.excludedFields.SequenceEqual(after.excludedFields) && before.fields.Length == after.fields.Length && before.fields.Zip(after.fields, (a,b) => a.key == b.key && a.numeric == b.numeric && (a.numeric ? Math.Abs(a.number-b.number) <= report.fieldAbsoluteTolerance + report.fieldRelativeTolerance*Math.Abs(a.number) : a.value == b.value)).All(ok => ok);
            report.resourcesRestored = before.resources.Length == after.resources.Length && before.resources.Zip(after.resources, (a,b) => a.partId == b.partId && a.name == b.name && !double.IsNaN(b.amount) && !double.IsInfinity(b.amount) && b.amount >= 0 && (a.name == "ElectricCharge" ? a.amount-b.amount >= -report.resourceTolerance && a.amount-b.amount <= report.electricChargeRateBudget*Math.Max(0,report.reloadSimulationSeconds)+.0001 : Math.Abs(a.amount-b.amount)<=report.resourceTolerance)).All(ok => ok);
            report.positionErrorMeters = (V(after.orbitPosition)-V(before.orbitPosition)).magnitude; report.velocityErrorMetersPerSecond = (V(after.orbitVelocity)-V(before.orbitVelocity)).magnitude;
            report.orbitRestored = report.positionErrorMeters <= report.positionToleranceMeters && report.velocityErrorMetersPerSecond <= report.velocityToleranceMetersPerSecond;
            if (!report.timeRestored || !report.newVesselObject || !report.identityRestored || !report.newPartObjects || !report.crewRestored || !report.modulesRestored || !report.fieldsRestored || !report.resourcesRestored || !report.orbitRestored) throw new InvalidOperationException("Saved gameplay state did not satisfy frozen reload gates.");
            InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS | ControlTypes.TIMEWARP, Lock); lockOwned = true;
        }
        static readonly MethodInfo[] CameraMethods = { AccessTools.DeclaredMethod(typeof(FlightCamera), "LateUpdate"), AccessTools.DeclaredMethod(typeof(FXCamera), "LateUpdate"), AccessTools.DeclaredMethod(typeof(FXDepthCamera), "LateUpdate") };
        void BeginCamera()
        {
            if (CameraMethods.Any(m => m == null || Harmony.GetPatchInfo(m)?.Owners.Any() == true)) throw new InvalidOperationException("Reload camera rig already owned.");
            flight = FlightCamera.fetch; pivot = flight == null ? null : flight.transform.parent;
            if (pivot == null || flight.cameras == null || flight.cameras.Length == 0) throw new InvalidOperationException("Reload camera rig unavailable.");
            pivotPosition = pivot.localPosition; pivotRotation = pivot.localRotation; flightPosition = flight.transform.localPosition; flightRotation = flight.transform.localRotation; distance = flight.Distance;
            cameras = flight.cameras.Select(c => new CameraPose { camera = c, position = c.transform.localPosition, rotation = c.transform.localRotation }).ToArray();
            Vector3d radial = vessel.GetWorldPos3D()-vessel.mainBody.position;
            Vector3d solar = (Planetarium.fetch.Sun.position-vessel.GetWorldPos3D()).normalized;
            double along = -Vector3d.Dot(radial,solar);
            double clearance = (along>0?(radial+along*solar).magnitude:radial.magnitude)-vessel.mainBody.Radius;
            sunward = clearance>0;
            if(before==null){report.beforeCameraSunward=sunward;report.beforeHorizonClearanceMeters=clearance;report.beforeSunDirection=V(solar);}
            else{report.afterCameraSunward=sunward;report.afterHorizonClearanceMeters=clearance;report.afterSunDirection=V(solar);}
            cameraOwned = true;
            harmony.Patch(CameraMethods[0], prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CheckpointReloadQualification), "CameraPrefix")));
            for (int i=1;i<CameraMethods.Length;i++) harmony.Patch(CameraMethods[i], prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CheckpointReloadQualification), "EffectPrefix")));
            Camera.onPreCull += PreCull; ApplyCamera();
        }
        void ApplyCamera()
        {
            if (vessel == null || flight == null || pivot == null || FlightCamera.fetch != flight) throw new InvalidOperationException("Reload camera live owner lost.");
            Vector3 view = sunward ? -(Vector3)(Planetarium.fetch.Sun.position-vessel.GetWorldPos3D()).normalized : vessel.transform.rotation * new Vector3(.5f,.3f,-1).normalized;
            Vector3 up = Vector3.ProjectOnPlane((Vector3)(vessel.GetWorldPos3D()-vessel.mainBody.position),view).normalized;
            if(up.sqrMagnitude<.9)up=Vector3.ProjectOnPlane(vessel.transform.up,view).normalized;
            if (up.sqrMagnitude < .9) throw new InvalidOperationException("Reload camera basis degenerate.");
            pivot.position = vessel.transform.position; pivot.rotation = Quaternion.LookRotation(view,up);
            flight.SetDistanceImmediate(100); flight.transform.localPosition = Vector3.back*100; flight.transform.localRotation = Quaternion.identity;
        }
        static bool CameraPrefix(FlightCamera __instance) { if(owner==null||!owner.cameraOwned||owner.flight!=__instance)return true; try{owner.ApplyCamera();return false;}catch(Exception e){owner.Fail(e);return true;} }
        static void EffectPrefix() { if(owner!=null&&owner.cameraOwned)try{owner.ApplyCamera();}catch(Exception e){owner.Fail(e);} }
        void PreCull(Camera c) { if(cameraOwned&&cameras.Any(p=>p.camera==c))report.cameraCallbacks++; }
        void EndCamera()
        {
            if (!cameraOwned) return; cameraOwned=false; Camera.onPreCull-=PreCull; harmony.UnpatchAll(Hook);
            bool ok=flight!=null&&pivot!=null;
            if(ok){flight.SetDistanceImmediate(distance);pivot.localPosition=pivotPosition;pivot.localRotation=pivotRotation;flight.transform.localPosition=flightPosition;flight.transform.localRotation=flightRotation;ok&=flight.Distance==distance&&pivot.localPosition==pivotPosition&&pivot.localRotation==pivotRotation&&flight.transform.localPosition==flightPosition&&flight.transform.localRotation==flightRotation;}
            foreach(CameraPose p in cameras){if(p.camera==null){ok=false;continue;}p.camera.transform.localPosition=p.position;p.camera.transform.localRotation=p.rotation;ok&=p.camera.transform.localPosition==p.position&&p.camera.transform.localRotation==p.rotation;}
            report.cameraRestored=cameraReleaseObserved?report.cameraRestored&&ok:ok;cameraReleaseObserved=true;if(!ok)report.faults++;
            flight=null;pivot=null;cameras=null;
        }
        void Image(string name)
        {
            Texture2D image=null; RenderTexture previous=RenderTexture.active;
            try{RenderTexture.active=null;image=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);image.Apply(false,false);File.WriteAllBytes(Path.Combine(directory,name+".png"),image.EncodeToPNG());report.screenWidth=Screen.width;report.screenHeight=Screen.height;images.Add(name+".png");}
            finally{RenderTexture.active=previous;if(image!=null)Destroy(image);}
        }
        void Record(string phase){events.Add(new EventRow{phase=phase,universalTime=Planetarium.GetUniversalTime(),wallSeconds=Time.realtimeSinceStartup-began,frame=Time.frameCount});}
        bool Try(Action action){try{action();return !finished;}catch(Exception e){Fail(e);return false;}}
        void Cleanup()
        {
            EndCamera();if(lockOwned){InputLockManager.RemoveControlLock(Lock);lockOwned=false;}
            report.hooksRemoved=CameraMethods.All(m=>m!=null&&Harmony.GetPatchInfo(m)?.Owners.Contains(Hook)!=true);report.controlLockRemoved=!lockOwned;
            report.cleanupStatus=report.hooksRemoved&&report.controlLockRemoved&&report.cameraRestored?"removed-owned-hooks-locks-restored-camera":"cleanup-error";
            if(report.cleanupStatus=="cleanup-error")report.faults++;
        }
        void Export()
        {
            report.sourceUnchanged=ScaleCheckpointLoadState.SourceUnchanged();report.savedUnchanged=savePath!=null&&File.Exists(savePath)&&ScaleCheckpointLoadState.HashFile(savePath)==report.savedSha256;
            report.events=events.ToArray();report.screenshots=images.ToArray();
            if(!report.sourceUnchanged||(report.savedSha256!=null&&!report.savedUnchanged)){report.status="invalid";report.faults++;}
            File.WriteAllText(Path.Combine(directory,"checkpoint-reload.json"),ReportJson.Encode(report));
        }
        void Fail(Exception error)
        {
            if(finished)return;finished=true;report.status="invalid";report.reason=error.GetType().Name+": "+error.Message;report.faults++;
            try{Cleanup();Export();}catch(Exception cleanup){Debug.LogException(cleanup);}Application.Quit(2);
        }
        void OnDestroy(){if(owner!=this)return;if(!finished&&report!=null)Fail(new InvalidOperationException("Persistent reload coordinator destroyed before completion."));owner=null;}
    }
}
