using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.MainMenu,true)]
    public sealed class OwnedClockProbeStage : MonoBehaviour
    {
        [Serializable] public sealed class ReportData
        {
            public string schema="continuum-owned-clock-probe-stage/v1",status="requested",failure="",templatePath,templateSha256,craftPath,craftSha256,outputPath,outputSha256,vesselId,assemblySha256,cleanup="pending";
            public int processId,originalCraftParts,retainedParts,actualSnapshotParts,loadedParts;
            public double epoch,radius,orbitalSpeed,solarAlignmentDegrees;
            public double loadedAltitude,loadedSolarAlignmentDegrees;
            public double[] positionZup,velocityZup,worldRotation;
            public string[] retainedPartNames,sourceModules,snapshotModules;
            public bool templateUnchanged,craftUnchanged;
            public bool originalRootSerializationReadback;
            public int rootSerializationMethodToken;
            public uint[] preCreateSnapshotFlightIds,stockAssignedFlightIds,savedFlightIds;
            public bool stockFlightIdsUnique;
            public bool templateScenarioUpgradeInvoked,reloadedScenarioUpgradeInvoked;
            public bool cameraConfigured;
            public int cameraTargetPartId,cameraId,cameraConfiguredFrame;
            public uint cameraTargetFlightId;public double cameraConfiguredUT,requestedCameraPhaseDegrees=20;public long cameraConfigurationTicks,cameraClockFrequency=System.Diagnostics.Stopwatch.Frequency;
            public double[] cameraRequestedPositionWorld;
            public string framingPolicy="Current visible mesh/skinned-mesh root-centered bounds; 60deg FOV, 1.5 radius margin, minimum10m; one-shot camera change, subsequent stock free-camera evolution.";
            public double cameraDistance,cameraFieldOfView,cameraNearClip,framingRadius,cameraPhaseDegrees,cameraBodyCenterAngleDegrees;
            public double[] cameraTargetWorld,cameraPositionWorld,cameraForwardWorld;
            public string cameraTargetKind="root-part-origin",cameraPolicy="stock-free-sunward-20deg/v1";
            public string producer="original LoadShip/internal AssembleForLaunch(preCreate)/ProtoVessel.Save/Game.Save; declared Sunward circular placement";
        }
        readonly ReportData report=new ReportData();
        readonly List<GameObject> temporary=new List<GameObject>();
        bool active,built,loading,ownsReport,cleanupOwnershipKnown=true,frameOnly;
        int cameraPreparedFrame=-1;
        int physicalReadyFrame=-1;
        int readyFrame=-1,disposedFrame;
        float started;
        string folder,templateName,outputName,reportPath;
        Game previous,loaded;
        string previousFolder;
        double previousUt;
        ShipConstruct ship;
        static string Arg(string[] args,string name)
        {string prefix=name+"=";string[] values=args.Where(x=>x.StartsWith(prefix,StringComparison.Ordinal)).ToArray();if(values.Length!=1||values[0].Length==prefix.Length)throw new ArgumentException("Exactly one "+name+" required.");return values[0].Substring(prefix.Length);}
        static string Name(string value)
        {if(value=="."||value==".."||value.IndexOfAny(Path.GetInvalidFileNameChars())>=0||value.IndexOfAny(new[]{'/','\\'})>=0)throw new ArgumentException("Simple save/checkpoint name required.");return value;}
        static string Hash(string path)
        {using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
        static string Absolute(string path)
        {if(!Path.IsPathRooted(path))throw new ArgumentException("Absolute path required.");path=Path.GetFullPath(path);for(string p=path;p!=null;p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse ancestry rejected.");return path;}
        public void Awake()
        {
            var args=Environment.GetCommandLineArgs();frameOnly=Array.IndexOf(args,"--continuum-owned-clock-frame")>=0;if(Array.IndexOf(args,"--continuum-owned-clock-stage")<0&&!frameOnly)return;
            active=true;started=Time.realtimeSinceStartup;DontDestroyOnLoad(gameObject);report.processId=System.Diagnostics.Process.GetCurrentProcess().Id;
            previous=HighLogic.CurrentGame;previousFolder=HighLogic.SaveFolder;previousUt=Planetarium.GetUniversalTime();
            try
            {
                if(frameOnly)
                {
                    if(Array.IndexOf(args,"--continuum-owned-clock-stage")>=0||!ScaleQualificationSelection.Requested(args))throw new ArgumentException("Frame-only mode requires a separately selected stock autoload and no reconstruction flag.");
                    reportPath=Absolute(Arg(args,"--continuum-stage-report"));if(File.Exists(reportPath)||Directory.Exists(reportPath)||!Directory.Exists(Path.GetDirectoryName(reportPath)))throw new IOException("Fresh frame report required.");
                    using(var file=new FileStream(reportPath,FileMode.CreateNew,FileAccess.Write)){}ownsReport=true;report.status="waiting-selected-flight";report.producer="existing stock checkpoint autoload; camera preparation only";report.cleanup="no-temporary-construct";Write();return;
                }
                folder=Name(Arg(args,"--continuum-stage-save"));templateName=Name(Arg(args,"--continuum-stage-template"));outputName=Name(Arg(args,"--continuum-stage-output"));
                report.templatePath=Absolute(Path.Combine(KSPUtil.ApplicationRootPath,"saves",folder,templateName+".sfs"));report.outputPath=Absolute(Path.Combine(KSPUtil.ApplicationRootPath,"saves",folder,outputName+".sfs"));
                report.craftPath=Absolute(Arg(args,"--continuum-stage-craft"));reportPath=Absolute(Arg(args,"--continuum-stage-report"));
                report.templateSha256=Arg(args,"--continuum-stage-template-sha");report.craftSha256=Arg(args,"--continuum-stage-craft-sha");
                if(report.outputPath==report.templatePath||File.Exists(report.outputPath)||Directory.Exists(report.outputPath)||File.Exists(reportPath)||Directory.Exists(reportPath)||reportPath==report.outputPath||!Directory.Exists(Path.GetDirectoryName(reportPath)))throw new IOException("Fresh distinct outputs required.");
                if(Hash(report.templatePath)!=report.templateSha256||Hash(report.craftPath)!=report.craftSha256)throw new InvalidDataException("Stage source SHA differs.");
                using(var file=new FileStream(reportPath,FileMode.CreateNew,FileAccess.Write)){}ownsReport=true;
                GameEvents.onLevelWasLoadedGUIReady.Add(Ready);Write();
            }
            catch(Exception error){Fail(error);}
        }
        void Ready(GameScenes scene){if(active&&!built&&scene==GameScenes.MAINMENU)readyFrame=Time.frameCount;}
        public void Update()
        {
            if(!active)return;
            try
            {
                if(Time.realtimeSinceStartup-started>120)throw new TimeoutException("Owned probe staging exceeded120s.");
                if(frameOnly)
                {
                    if(!ScaleCheckpointLoadState.Ready||!HighLogic.LoadedSceneIsFlight||!FlightGlobals.ready)return;
                    var selected=FlightGlobals.ActiveVessel;if(selected==null||selected.id!=ScaleCheckpointLoadState.VesselId||selected.parts.Count!=9||!selected.loaded||selected.packed)return;
                    if(!ScaleCheckpointLoadState.SourceUnchanged())throw new InvalidDataException("Selected frame source changed.");
                    if(physicalReadyFrame<0){physicalReadyFrame=Time.frameCount;return;}if(Time.frameCount<=physicalReadyFrame+2)return;
                    if(cameraPreparedFrame<0){report.templatePath=ScaleCheckpointLoadState.SourcePath;report.templateSha256=ScaleCheckpointLoadState.SourceSha256;report.vesselId=selected.id.ToString("N");PrepareCamera(selected);cameraPreparedFrame=Time.frameCount;report.status="camera-settling";Write();return;}
                    if(Time.frameCount<=cameraPreparedFrame+2)return;ObserveCamera(selected);report.loadedParts=9;report.templateUnchanged=true;report.status="camera-ready";Write();active=false;return;
                }
                if(!built)
                {
                    if(readyFrame<0||Time.frameCount<=readyFrame)return;
                    if(HighLogic.LoadedScene!=GameScenes.MAINMENU)throw new InvalidOperationException("Stage requires the original menu bootstrap.");
                    Build();built=true;disposedFrame=Time.frameCount;Retire();Write();return;
                }
                if(!loading)
                {
                    if(Time.frameCount<=disposedFrame)return;
                    if(temporary.Any(x=>x!=null))return;
                    report.cleanup="temporary-stock-construct-retired";
                    if(Hash(report.outputPath)!=report.outputSha256)throw new InvalidDataException("Staged save changed before stock load.");
                    loaded=GamePersistence.LoadGame(outputName,folder,true,true);if(loaded==null||!loaded.compatible||loaded.flightState.protoVessels.Count!=1||loaded.flightState.protoVessels[0].protoPartSnapshots.Count!=9)throw new InvalidDataException("Stock readback differs from nine-part stage.");
                    GamePersistence.UpdateScenarioModules(loaded);report.reloadedScenarioUpgradeInvoked=true;
                    HighLogic.CurrentGame=loaded;HighLogic.SaveFolder=folder;loaded.startScene=GameScenes.FLIGHT;loading=true;report.status="loading";Write();loaded.Start();return;
                }
                if(!HighLogic.LoadedSceneIsFlight||!FlightGlobals.ready)return;
                var vessel=FlightGlobals.ActiveVessel;if(vessel==null||vessel.id.ToString("N")!=report.vesselId||vessel.parts.Count!=9||!vessel.loaded||vessel.packed)return;
                if(!ReferenceEquals(HighLogic.CurrentGame,loaded))throw new InvalidOperationException("Stage game was replaced.");
                if(physicalReadyFrame<0){physicalReadyFrame=Time.frameCount;return;}if(Time.frameCount<=physicalReadyFrame+2)return;
                if(cameraPreparedFrame<0)
                {PrepareCamera(vessel);cameraPreparedFrame=Time.frameCount;report.status="camera-settling";Write();return;}
                if(Time.frameCount<=cameraPreparedFrame+2)return;
                ObserveCamera(vessel);
                report.loadedParts=vessel.parts.Count;report.loadedAltitude=vessel.altitude;
                Vector3d currentPosition=vessel.orbit.getRelativePositionAtUT(Planetarium.GetUniversalTime()).normalized,currentSun=(-vessel.mainBody.orbit.getRelativePositionAtUT(Planetarium.GetUniversalTime())).normalized;
                report.loadedSolarAlignmentDegrees=Math.Atan2(Vector3d.Cross(currentPosition,currentSun).magnitude,Vector3d.Dot(currentPosition,currentSun))*180/Math.PI;
                report.status="flight-ready";SourceProof();Write();active=false;GameEvents.onLevelWasLoadedGUIReady.Remove(Ready);
            }
            catch(Exception error){Fail(error);}
        }
        public static ReportData ReframeSunward(Vessel vessel)
        {
            if(System.Threading.SynchronizationContext.Current==null||System.Threading.SynchronizationContext.Current.GetType().Name!="UnitySynchronizationContext")throw new InvalidOperationException("Camera reframe requires the Unity main thread.");
            if(vessel==null||vessel!=FlightGlobals.ActiveVessel||!vessel.loaded||vessel.packed)throw new InvalidOperationException("Camera reframe requires the current loaded physical craft.");
            var result=new ReportData{status="camera-settling",vesselId=vessel.id.ToString("D"),loadedParts=vessel.parts.Count,cleanup="camera-operation-only; no temporary objects or installed hooks",producer="One-shot original stock camera API; no vessel/orbit/light/resource/clock writes. Actual pose and lighting require subsequent rendered-frame observation."};
            using(var process=System.Diagnostics.Process.GetCurrentProcess())result.processId=process.Id;
            PrepareCamera(vessel,result);return result;
        }
        void PrepareCamera(Vessel vessel){PrepareCamera(vessel,report);}
        static void PrepareCamera(Vessel vessel,ReportData report)
        {
            if(vessel==null||vessel.orbit==null||vessel.mainBody==null)throw new InvalidOperationException("Camera reframe vessel/orbit/body is absent.");
            var camera=FlightCamera.fetch;var root=vessel.rootPart;
            if(camera==null||camera.mainCamera==null||camera.GetPivot()==null||root==null||root.partTransform==null||Sun.Instance==null||Sun.Instance.sunLight==null||MapView.MapIsEnabled||CameraManager.Instance==null||CameraManager.Instance.currentCameraMode!=CameraManager.CameraMode.Flight)throw new InvalidOperationException("Original flight camera/root context is unavailable.");
            Vector3 target=root.partTransform.position,sun=-Sun.Instance.sunLight.transform.forward;
            if(sun.sqrMagnitude<.99f)throw new InvalidOperationException("Actual Sun light direction is invalid.");sun.Normalize();
            Vector3 side=Vector3.Cross(sun,(Vector3)vessel.orbit.GetRelativeVel());if(side.sqrMagnitude<1e-8f)side=Vector3.Cross(sun,Vector3.up);if(side.sqrMagnitude<1e-8f)side=Vector3.Cross(sun,Vector3.right);side.Normalize();
            Vector3 offset=Quaternion.AngleAxis(20,side)*sun;
            double radius=.5;
            foreach(var part in vessel.parts)foreach(var renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                if(!(renderer is MeshRenderer)&&!(renderer is SkinnedMeshRenderer))continue;
                if(!renderer.enabled||!renderer.gameObject.activeInHierarchy)continue;
                Bounds bounds=renderer.bounds;for(int corner=0;corner<8;corner++)
                {Vector3 point=bounds.center+Vector3.Scale(bounds.extents,new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1));radius=Math.Max(radius,(point-target).magnitude);}
            }
            if(double.IsNaN(radius)||double.IsInfinity(radius)||radius>1000)throw new InvalidOperationException("Probe framing bounds are invalid.");
            float distance=(float)Math.Max(10,1.5*radius/Math.Sin(30*Math.PI/180)+2);
            FlightCamera.SetTarget(root);FlightCamera.SetModeImmediate(FlightCamera.Modes.FREE);
            camera.FieldOfView=60;camera.SetFoV(60);camera.GetPivot().position=target;
            camera.SetCamCoordsFromPosition(target+offset*distance);
            Quaternion frame=camera.GetCameraFoR(FoRModes.SRF_NORTH);Vector3 local=Quaternion.Inverse(frame)*offset;
            FlightCamera.CamHdg=Mathf.Atan2(-local.z,local.x)-Mathf.PI/2;FlightCamera.CamPitch=Mathf.Atan2(local.y,Mathf.Sqrt(local.x*local.x+local.z*local.z));camera.SetDistanceImmediate(distance);
            camera.mainCamera.nearClipPlane=.1f;
            report.cameraTargetPartId=root.GetInstanceID();report.cameraId=camera.mainCamera.GetInstanceID();report.framingRadius=radius;report.cameraConfiguredFrame=Time.frameCount;
            report.cameraTargetFlightId=root.flightID;report.cameraConfiguredUT=Planetarium.GetUniversalTime();report.cameraConfigurationTicks=System.Diagnostics.Stopwatch.GetTimestamp();report.cameraDistance=distance;report.cameraFieldOfView=60;report.cameraNearClip=.1f;
            Vector3 requested=target+offset*distance;report.cameraRequestedPositionWorld=new[]{(double)requested.x,requested.y,requested.z};report.cameraTargetWorld=new[]{(double)target.x,target.y,target.z};
        }
        void ObserveCamera(Vessel vessel)
        {
            var camera=FlightCamera.fetch;var root=vessel.rootPart;
            if(camera==null||root==null||camera.mainCamera==null||camera.partTarget!=root||camera.mode!=FlightCamera.Modes.FREE)throw new InvalidOperationException("Prepared stock camera target/mode changed.");
            Vector3 target=root.partTransform.position,position=camera.mainCamera.transform.position,forward=camera.mainCamera.transform.forward,offset=(position-target).normalized;
            double eye=Vector3.Dot(target-position,forward);Vector3 viewport=camera.mainCamera.WorldToViewportPoint(target);
            if(eye<=report.framingRadius+camera.mainCamera.nearClipPlane||viewport.z<=0||Math.Abs(viewport.x-.5)>.02||Math.Abs(viewport.y-.5)>.02)throw new InvalidOperationException("Actual prepared camera does not frame the source root.");
            report.cameraConfigured=true;report.cameraDistance=camera.Distance;report.cameraFieldOfView=camera.FieldOfView;report.cameraNearClip=camera.mainCamera.nearClipPlane;report.cameraTargetWorld=new[]{(double)target.x,target.y,target.z};report.cameraPositionWorld=new[]{(double)position.x,position.y,position.z};report.cameraForwardWorld=new[]{(double)forward.x,forward.y,forward.z};
            report.cameraPhaseDegrees=Vector3.Angle(offset,-Sun.Instance.sunLight.transform.forward);report.cameraBodyCenterAngleDegrees=Vector3.Angle(forward,(Vector3)(vessel.mainBody.position-(Vector3d)position));
            if(Math.Abs(report.cameraPhaseDegrees-20)>1||report.cameraBodyCenterAngleDegrees>28)throw new InvalidOperationException("Actual Sun/craft/planet camera geometry differs from prepared framing.");
        }
        void Build()
        {
            var assembly=typeof(Vessel).Assembly;report.assemblySha256=Hash(assembly.Location);
            if(report.assemblySha256!="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27"||assembly.ManifestModule.ModuleVersionId!=new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf"))throw new InvalidOperationException("Original stock assembly differs.");
            var signature=new[]{typeof(ShipConstruct),typeof(string),typeof(string),typeof(string),typeof(Game),typeof(VesselCrewManifest),typeof(bool),typeof(bool),typeof(bool),typeof(bool),typeof(Orbit),typeof(bool),typeof(bool)};
            MethodInfo assemble=typeof(ShipConstruction).GetMethod("AssembleForLaunch",BindingFlags.Static|BindingFlags.NonPublic,null,signature,null);
            if(assemble==null||assemble.ReturnType!=typeof(Vessel)||Harmony.GetPatchInfo(assemble)!=null)throw new InvalidOperationException("Original preCreate assembly seam unavailable/patched.");
            var game=GamePersistence.LoadGame(templateName,folder,true,true);if(game==null||!game.compatible||game.flightState==null||game.Mode!=Game.Modes.SANDBOX)throw new InvalidDataException("Compatible sandbox template required.");
            GamePersistence.UpdateScenarioModules(game);report.templateScenarioUpgradeInvoked=true;
            HighLogic.CurrentGame=game;HighLogic.SaveFolder=folder;report.epoch=game.flightState.universalTime;Planetarium.SetUniversalTime(report.epoch);
            ConfigNode craft=ConfigNode.Load(report.craftPath);var parts=craft.GetNodes("PART");report.originalCraftParts=parts.Length;
            string[] expected={"probeCoreOcto.v2","batteryBankMini","longAntenna","miniFuelTank","microEngine","solarPanels5","solarPanels5","solarPanels5","solarPanels5"};
            if(parts.Length!=18)throw new InvalidDataException("Pinned18-part ComSat source required.");
            var retained=new HashSet<string>();for(int i=0;i<9;i++){string token=parts[i].GetValue("part");if(token==null||token.Substring(0,token.LastIndexOf('_'))!=expected[i]||!retained.Add(token))throw new InvalidDataException("ComSat payload identity/order differs.");}
            foreach(var part in parts.Take(9))
            {
                var discard=new List<ConfigNode.Value>();foreach(ConfigNode.Value value in part.values)
                {
                    if(value.name=="link"&&!retained.Contains(value.value))discard.Add(value);
                    if(value.name=="attN"||value.name=="srfN")
                    {string other=value.value.Substring(value.value.IndexOf(',')+1).Trim();if(!retained.Any(id=>other==id||other.StartsWith(id+"_",StringComparison.Ordinal)))discard.Add(value);}
                }
                foreach(var value in discard)part.values.Remove(value);
            }
            for(int i=9;i<parts.Length;i++)craft.RemoveNode(parts[i]);craft.SetValue("ship","Continuum Owned Clock Probe",true);
            report.retainedParts=9;report.retainedPartNames=expected;report.sourceModules=parts.Take(9).SelectMany(x=>x.GetNodes("MODULE").Select(m=>m.GetValue("name"))).ToArray();
            var existingParts=new HashSet<Part>(Resources.FindObjectsOfTypeAll<Part>());
            ship=new ShipConstruct();bool shipLoaded;
            try{shipLoaded=ship.LoadShip(craft);}
            finally
            {
                foreach(var part in Resources.FindObjectsOfTypeAll<Part>())if(part!=null&&!existingParts.Contains(part))
                {
                    Transform root=part.transform.root;
                    if(root.GetComponent<Part>()==null||existingParts.Contains(root.GetComponent<Part>()))
                    {cleanupOwnershipKnown=false;throw new InvalidOperationException("New craft part unexpectedly belongs to a foreign/nonpart root; cleanup ownership unresolved.");}
                    if(!temporary.Contains(root.gameObject))temporary.Add(root.gameObject);
                }
            }
            if(!shipLoaded||ship.parts.Count!=9)throw new InvalidOperationException("Original stock craft loader rejected payload.");
            CelestialBody body=FlightGlobals.Bodies.Single(x=>x.bodyName=="Kerbin");
            var epochFrame=new Planetarium.CelestialFrame();Planetarium.ZupAtT(report.epoch,body,ref epochFrame);
            Vector3d sun=epochFrame.WorldToLocal(Planetarium.Zup.LocalToWorld(-body.orbit.getRelativePositionAtUT(report.epoch))).normalized;
            Vector3d axis=new Vector3d(0,0,1),tangent=Vector3d.Cross(axis,sun);if(tangent.sqrMagnitude<1e-12)tangent=Vector3d.Cross(new Vector3d(0,1,0),sun);tangent.Normalize();
            report.radius=body.Radius+120000;report.orbitalSpeed=Math.Sqrt(body.gravParameter/report.radius);Vector3d position=sun*report.radius,velocity=tangent*report.orbitalSpeed;
            var orbit=new Orbit();orbit.UpdateFromStateVectors(Planetarium.Zup.WorldToLocal(epochFrame.LocalToWorld(position)),Planetarium.Zup.WorldToLocal(epochFrame.LocalToWorld(velocity)),body,report.epoch);
            var vessel=(Vessel)assemble.Invoke(null,new object[]{ship,"","","Squad/Flags/default",game,new VesselCrewManifest(),true,false,false,true,orbit,true,false});
            if(vessel==null||vessel.parts.Count!=9||vessel.protoVessel==null)throw new InvalidOperationException("Original preCreate vessel construction incomplete.");
            report.preCreateSnapshotFlightIds=vessel.protoVessel.protoPartSnapshots.Select(x=>x.flightID).ToArray();
            report.stockAssignedFlightIds=vessel.parts.Select(x=>x.flightID).ToArray();
            report.stockFlightIdsUnique=report.stockAssignedFlightIds.Length==9&&report.stockAssignedFlightIds.All(x=>x!=0)&&report.stockAssignedFlightIds.Distinct().Count()==9;
            if(!report.stockFlightIdsUnique)throw new InvalidDataException("Original stock launch did not assign nine distinct nonzero flight IDs.");
            var currentSnapshot=new ProtoVessel(vessel,true);
            var node=new ConfigNode("VESSEL");currentSnapshot.Save(node);node.SetValue("sit","ORBITING",true);node.SetValue("landed",false,true);node.SetValue("splashed",false,true);node.SetValue("vesselSpawning",false,true);
            Quaternion rotation=Quaternion.LookRotation((Vector3)epochFrame.LocalToWorld(sun).xzy,(Vector3)epochFrame.LocalToWorld(tangent).xzy);Quaternion local=Quaternion.Inverse(body.bodyTransform.rotation)*rotation;node.SetValue("rot",KSPUtil.WriteQuaternion(local),true);
            node.RemoveNodes("ORBIT");node.AddNode(ProtoVessel.CreateOrbitNode(orbit));
            var proto=new ProtoVessel(node,game);report.vesselId=proto.vesselID.ToString("N");report.actualSnapshotParts=proto.protoPartSnapshots.Count;if(report.actualSnapshotParts!=9)throw new InvalidDataException("Original proto graph differs.");
            report.savedFlightIds=proto.protoPartSnapshots.Select(x=>x.flightID).ToArray();if(!report.savedFlightIds.SequenceEqual(report.stockAssignedFlightIds))throw new InvalidDataException("Original refreshed snapshot did not retain assigned stock flight IDs/order.");
            report.snapshotModules=node.GetNodes("PART").SelectMany(x=>x.GetNodes("MODULE").Select(m=>m.GetValue("name"))).ToArray();
            game.flightState.protoVessels.Clear();game.flightState.protoVessels.Add(proto);game.flightState.activeVesselIdx=0;game.startScene=GameScenes.FLIGHT;game.Title="Continuum Owned Clock Probe";
            var save=new ConfigNode();game.Save(save);
            MethodInfo rootWriter=typeof(ConfigNode).GetMethod("WriteRootNode",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly,null,new[]{typeof(StreamWriter)},null);
            if(rootWriter==null||rootWriter.ReturnType!=typeof(void)||rootWriter.Module.ModuleVersionId!=assembly.ManifestModule.ModuleVersionId||Harmony.GetPatchInfo(rootWriter)!=null)throw new InvalidOperationException("Original ConfigNode root serialization seam unavailable/patched.");
            report.rootSerializationMethodToken=rootWriter.MetadataToken;
            using(var file=new FileStream(report.outputPath,FileMode.CreateNew,FileAccess.Write))using(var writer=new StreamWriter(file,new UTF8Encoding(false)))rootWriter.Invoke(save,new object[]{writer});
            ConfigNode parsed=ConfigNode.Load(report.outputPath),parsedGame=parsed==null?null:parsed.GetNode("GAME"),parsedFlight=parsedGame==null?null:parsedGame.GetNode("FLIGHTSTATE");
            if(parsedGame==null||parsedFlight==null||parsedFlight.GetValue("activeVessel")!="0"||parsedFlight.GetNodes("VESSEL").Length!=1||parsedFlight.GetNode("VESSEL").GetNodes("PART").Length!=9)throw new InvalidDataException("Original save root readback differs from nine-part flight.");
            report.originalRootSerializationReadback=true;report.outputSha256=Hash(report.outputPath);
            report.positionZup=new[]{position.x,position.y,position.z};report.velocityZup=new[]{velocity.x,velocity.y,velocity.z};report.worldRotation=new[]{(double)rotation.x,rotation.y,rotation.z,rotation.w};report.solarAlignmentDegrees=Math.Atan2(Vector3d.Cross(position.normalized,sun).magnitude,Vector3d.Dot(position.normalized,sun))*180/Math.PI;report.status="staged";SourceProof();
        }
        void SourceProof(){report.templateUnchanged=Hash(report.templatePath)==report.templateSha256;report.craftUnchanged=Hash(report.craftPath)==report.craftSha256;if(!report.templateUnchanged||!report.craftUnchanged)throw new InvalidDataException("Stage source mutated.");}
        void Retire(){foreach(var obj in temporary)if(obj!=null)Destroy(obj);if(ship!=null)ship.Clear();}
        void Write(){if(ownsReport)File.WriteAllText(reportPath,JsonUtility.ToJson(report,true));}
        void Fail(Exception error)
        {
            report.status="failed";report.failure=error.ToString();active=false;GameEvents.onLevelWasLoadedGUIReady.Remove(Ready);Retire();report.cleanup=!cleanupOwnershipKnown?"ownership-unresolved":temporary.All(x=>x==null)?"temporary-retired":"temporary-destroy-requested";
            if(!loading&&!frameOnly){HighLogic.CurrentGame=previous;HighLogic.SaveFolder=previousFolder;Planetarium.SetUniversalTime(previousUt);}try{Write();}catch(Exception outputError){Debug.LogException(outputError);}Debug.LogException(error);Application.Quit(2);
        }
        public void OnDestroy(){GameEvents.onLevelWasLoadedGUIReady.Remove(Ready);if(active)Fail(new InvalidOperationException("Stage coordinator destroyed before flight readiness."));}
    }
}
