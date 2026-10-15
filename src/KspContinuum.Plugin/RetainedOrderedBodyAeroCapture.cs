using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class RetainedOrderedBodyAeroCapture : IDisposable
    {
        const string Owner="continuum.retained-ordered-body-aero-capture",StockSha="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const int Roots=2,MaxDepth=128;
        static readonly MethodInfo Integrate=AccessTools.DeclaredMethod(typeof(FlightIntegrator),"Integrate",new[] {typeof(Part)});
        static readonly MethodInfo Aero=AccessTools.DeclaredMethod(typeof(FlightIntegrator),"UpdateAerodynamics",new[] {typeof(Part)});
        static readonly MethodInfo Drag=AccessTools.DeclaredMethod(typeof(FlightIntegrator),"ApplyAeroDrag",new[] {typeof(Part),typeof(Rigidbody),typeof(ForceMode)});
        static readonly MethodInfo Lift=AccessTools.DeclaredMethod(typeof(FlightIntegrator),"ApplyAeroLift",new[] {typeof(Part),typeof(Rigidbody),typeof(ForceMode)});
        static readonly MethodInfo DragValue=AccessTools.DeclaredMethod(typeof(FlightIntegrator),"CalculateDragValue",new[] {typeof(Part)});
        static readonly MethodInfo Evaluate=AccessTools.DeclaredMethod(typeof(FloatCurve),"Evaluate",new[] {typeof(float)});
        static readonly MethodInfo Projection=AccessTools.DeclaredMethod(typeof(Vector3),"ProjectOnPlane",new[] {typeof(Vector3),typeof(Vector3)});
        static readonly MethodInfo[] Targets={Integrate,Aero,Drag,Lift};
        static readonly int[] Tokens={100677461,100677495,100677493,100677494};
        static readonly string[] IlHashes={"71f3e8ba1599644c13be502e24d7463524c98188b1f3a075b898c9a8430a0f04","c7f90df6de22e42796d8076c908102f98fabab7893ba91cf3bd646833573d2c6","0d86e046558359841c1d9c35d4ad03711631bce9b647654962925a689684f6e4","b1aee44213b1cb169c95b98e7b69c8f6524b5615ab8b6d863440231e839c6094"};
        static readonly AccessTools.FieldRef<FlightIntegrator,bool> ApplyDrag=AccessTools.FieldRefAccess<FlightIntegrator,bool>("cacheApplyDrag"),ApplyParent=AccessTools.FieldRefAccess<FlightIntegrator,bool>("cacheApplyDragToNonPhysicsParts"),Acceleration=AccessTools.FieldRefAccess<FlightIntegrator,bool>("cacheDragUsesAcceleration");
        static readonly AccessTools.FieldRef<FlightIntegrator,float> AngularMultiplier=AccessTools.FieldRefAccess<FlightIntegrator,float>("cacheAngularDragMultiplier"),DragMultiplier=AccessTools.FieldRefAccess<FlightIntegrator,float>("cacheDragMultiplier"),LiftMultiplier=AccessTools.FieldRefAccess<FlightIntegrator,float>("cacheBodyLiftMultiplier");
        static RetainedOrderedBodyAeroCapture active;
        readonly Harmony harmony=new Harmony(Owner);readonly Vessel vessel;readonly Func<bool> capturing;readonly int thread=Thread.CurrentThread.ManagedThreadId;
        readonly Part[] parts;readonly Rigidbody[] recipients,ownBodies,servoBodies;readonly ILiftProvider[] providers;readonly DragCubeList[] cubeLists;readonly Part[][] children;readonly bool[][] attached;readonly Part[] preorder;
        readonly Dictionary<Part,int> partSlots=new Dictionary<Part,int>();readonly Dictionary<Rigidbody,int> bodies=new Dictionary<Rigidbody,int>();
        readonly List<MethodBase> graph=new List<MethodBase>();readonly bool[] admitted=new bool[4];readonly RootWork[] work=new RootWork[Roots];readonly int[] stack=new int[MaxDepth];
        readonly Func<FlightIntegrator,Part,double> originalDrag;readonly FloatCurve liftCurve;readonly AnimationCurve liftAnimationCurve;
        RootWork current;FlightIntegrator integrator;int depth;bool disposed,installing,cleaning,disabled;
        public readonly ReportData Report;
        [Serializable] public sealed class ReportData
        {
            public string schema="continuum-retained-ordered-body-aero-capture/v2",status="starting",cleanupStatus="not-installed",runId,vesselId,path,sha256,topologySha256,providerEpoch,detail;
            public string bodySnapshotScope="Per-root first reached command properties, before the original native call. Once per distinct recipient; no absence-of-later-writes, acceleration conversion or aggregate/native equivalence claim.";
            public string[] bodyFloatFields={"mass","position.x","position.y","position.z","rotation.x","rotation.y","rotation.z","rotation.w","localCenterOfMass.x","localCenterOfMass.y","localCenterOfMass.z","worldCenterOfMass.x","worldCenterOfMass.y","worldCenterOfMass.z","inertiaTensor.x","inertiaTensor.y","inertiaTensor.z","inertiaTensorRotation.x","inertiaTensorRotation.y","inertiaTensorRotation.z","inertiaTensorRotation.w","velocity.x","velocity.y","velocity.z","angularVelocity.x","angularVelocity.y","angularVelocity.z"};
            public string originalAssemblySha256=StockSha,originalAssemblyMvid="10657063-2fc3-43a7-84fa-d39e75e877bf";
            public string scope="Two original attached-part Integrate roots. Actual native command operands, original order and exceptions are preserved; no command is suppressed/replayed and no candidate force is published. Retained dry-tail reference excludes queued forces, providers, SetDrag, transforms, lift local-field writes, wings and native integration. Unsupported rows are recorded, not replacement coverage.";
            public string bitEncoding="IEEE754 binary32 uint and binary64 signed-long bit patterns, including NaN and signed zero; no decimal reconstruction.";
            public string epochScope="Frozen private stock managed/patch graph and bound part/body/child/provider/cube/curve identities, checked before and after the two captured roots and at cleanup; no universal mutable-mod ownership claim.";
            public string clockScope="Root inclusive wall interval includes original work and intrusive observation/reference. copyTicks brackets observer recording/bookkeeping only; referenceTicks includes retained computation+comparison. They do not establish FPS or an exclusive native owner cost.";
            public string sidecarHashScope="sha256 is set in the outer report after CreateNew sidecar serialization; sidecar's own sha256 is null.";
            public string[] inputFloatFields={"velocity.x","velocity.y","velocity.z","dragApplicationPoint.x","dragApplicationPoint.y","dragApplicationPoint.z","liftApplicationPoint.x","liftApplicationPoint.y","liftApplicationPoint.z","rotatedScaledLift.x","rotatedScaledLift.y","rotatedScaledLift.z","angularDrag","angularDragMultiplier","dragMultiplier","bodyLiftMultiplier","globalBodyLiftMultiplier","liftMachSample"};
            public string[] inputDoubleFields={"submergedPortion","atmosphericDensity","originalDragValue","pseudoReMultiplier"};
            public string[] inputFlagFields={"ownBody","servoBody","shielded","applyDrag","applyParentDrag","angularDragByIntegrator","accelerationMode","bodyLiftEligible","liftForceEligible","liftGeometryCaptured"};
            public string[] outputFloatFields={"speedSquared","speed","direction.x","direction.y","direction.z","dragScalar","bodyLiftScalar"};
            public string[] extraFloatFields={"initialBodyLiftScalar","bodyLiftLocalPosition.x","bodyLiftLocalPosition.y","bodyLiftLocalPosition.z","bodyLiftLocalVector.x","bodyLiftLocalVector.y","bodyLiftLocalVector.z","liftCurveInput"};
            public string[] extraDoubleFields={"exposedArea","aerodynamicArea","submergedDynamicPressurekPa"};
            public bool requested=true,graphAdmissions,graphInstalled,graphReleased;
            public int processId,boundParts,reachableParts,patchedMethods,shapeAdmissions,cleanupShapeChecks,staticCallsites,capturedRoots,completedRoots,errors,originalExceptions,droppedCommands,referenceMismatches,unsupportedRows,eligibleRows,matchedRows;
            public long clockFrequency=Stopwatch.Frequency,installationTicks,storageTicks,cleanupTicks;
            public MethodRow[] methods=new MethodRow[0];public BodyBinding[] bodyBindings=new BodyBinding[0];public Root[] roots=new Root[0];
        }
        [Serializable] public sealed class MethodRow {public string declaringType,method,assemblyMvid,ilSha256;public int metadataToken,instructionCount,staticCallsites;public bool patched;}
        [Serializable] public sealed class BodyBinding {public int bodyId,partSlot;public uint partId;public string role;}
        [Serializable] public sealed class Root
        {
            public int sequence,frame,rowCount,commandCount,matchedRows,unsupportedRows,eligibleRows,mismatchedRows;public double fixedTimeSeconds;
            public long startedTicks,endedTicks,copyTicks,referenceTicks;public bool originalReturned,commandTapeComplete,topologyIntact;public Row[] rows;public Command[] commands;public BodySnapshot[] bodySnapshots;
        }
        [Serializable] public sealed class BodySnapshot
        {
            public int index,bodyId,firstCommandSequence;public long startedTicks,endedTicks;public uint[] floatBits;
            public bool alive,isKinematic,dynamic,activeSelf,activeInHierarchy,sleeping;
        }
        [Serializable] public sealed class Row
        {
            public int ordinal,partSlot,recipientBodyId,tailCommandCount;public uint partId,inputFlags;public string referenceStatus,referenceReason;
            public bool aeroEntered,aeroReturned,dragCalculated,liftCurveReached,queuedStateCleared,bodyLiftScalarWritten;
            public uint[] inputFloatBits,outputFloatBits,extraFloatBits;public long[] inputDoubleBits,extraDoubleBits;public long dynamicPressureBits;
            public BodyAeroCommandKind[] tailKinds;
        }
        [Serializable] public sealed class Command
        {
            public int sequence,ordinal,recipientBodyId,forceMode,bodySnapshotIndex;public uint partId,angularDragBits;public string phase,kind;public uint[] forceBits,pointBits;public bool originalReturned;
        }
        struct Trace
        {internal int sequence,ordinal,body,mode,kind,snapshot;internal uint part;internal bool tail,returned;internal Vector3 force,point;internal float angular;}
        struct BodyWork
        {
            internal int id,first;internal long start,end;internal float mass;internal Vector3 position,localCom,worldCom,inertia,velocity,angularVelocity;internal Quaternion rotation,inertiaRotation;
            internal bool alive,kinematic,activeSelf,activeHierarchy,sleeping;
        }
        sealed class RowWork
        {
            internal int partSlot,tailCount;internal bool aeroEntered,aeroReturned,dragCalculated,liftReached,cleared,eligible,matched;internal string reason;
            internal RetainedOrderedBodyAeroResult original;internal float initialLift,liftInput;internal Vector3 localPosition,localVector;internal double exposed,area,submergedPressure;
        }
        sealed class RootWork
        {
            internal readonly RetainedOrderedBodyAeroBuffer buffer;internal readonly RowWork[] rows;internal readonly Trace[] tape;internal readonly BodyAeroCommand[] tail;
            internal readonly Root result=new Root();internal int rowCount,commandCount,snapshotCount;internal readonly BodyWork[] snapshots;internal readonly Dictionary<Rigidbody,int> snapshotIndices;
            internal RootWork(int count) {buffer=new RetainedOrderedBodyAeroBuffer(count);rows=new RowWork[count];for(int i=0;i<count;i++)rows[i]=new RowWork();tape=new Trace[checked(count*16)];tail=new BodyAeroCommand[checked(count*8)];snapshots=new BodyWork[checked(count*3)];snapshotIndices=new Dictionary<Rigidbody,int>(checked(count*3));}
        }
        public static bool Requested {get{return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_ORDERED_BODY_AERO_CAPTURE"));}}
        public static RetainedOrderedBodyAeroCapture Create(string[] args,string runId,Func<bool> capturing)
        {
            string path=Environment.GetEnvironmentVariable("CONTINUUM_ORDERED_BODY_AERO_CAPTURE");if(string.IsNullOrEmpty(path))return null;
            if(args==null||string.IsNullOrEmpty(runId)||capturing==null||!Path.IsPathRooted(path)||Path.GetExtension(path)!=".json"||File.Exists(path)||Directory.Exists(path)||!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path)))||FlightGlobals.ActiveVessel==null)throw new InvalidOperationException("New absolute JSON path, bound vessel/run and capture window required.");
            if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_STEP_CONTEXT"))||!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_INTEGRATION_ATTRIBUTION"))||args.Any(a=>a=="--continuum-callback-attribution"||a=="--continuum-live-aero-provider"||a=="--continuum-live-setdrag-provider"||a=="--continuum-setdrag-stress-candidate"))throw new InvalidOperationException("Original command capture excludes competing aero/provider/broad instrumentation.");
            return new RetainedOrderedBodyAeroCapture(Path.GetFullPath(path),runId,capturing);
        }
        RetainedOrderedBodyAeroCapture(string path,string runId,Func<bool> capturing)
        {
            long began=Stopwatch.GetTimestamp();this.capturing=capturing;vessel=FlightGlobals.ActiveVessel;Report=new ReportData {path=path,runId=runId,vesselId=vessel.id.ToString("D"),processId=Process.GetCurrentProcess().Id};
            try
            {
                Need(active==null,"One retained original-command capture owner required.");StartupPinnedGameIdentity.Require(typeof(FlightIntegrator).Assembly);
                Need(vessel.loaded&&!vessel.packed&&vessel.rootPart!=null&&vessel.parts.Count>0&&vessel.parts.Count<=OrderedBodyAeroPacket.MaximumRows,"Loaded bound vessel outside row domain.");
                parts=vessel.parts.ToArray();recipients=new Rigidbody[parts.Length];ownBodies=new Rigidbody[parts.Length];servoBodies=new Rigidbody[parts.Length];providers=new ILiftProvider[parts.Length];cubeLists=new DragCubeList[parts.Length];children=new Part[parts.Length][];attached=new bool[parts.Length][];
                var binding=new List<BodyBinding>();var partIds=new HashSet<uint>();
                for(int i=0;i<parts.Length;i++)
                {
                    Part p=parts[i];Need(p!=null&&p.flightID!=0&&partIds.Add(p.flightID)&&(p.GetType()==typeof(Part)||p.GetType()==typeof(CompoundPart))&&!partSlots.ContainsKey(p),"Unsupported concrete part or repeated owner.");partSlots.Add(p,i);recipients[i]=p.Rigidbody;ownBodies[i]=p.rb;servoBodies[i]=p.servoRb;providers[i]=p.bodyLiftOnlyProvider;cubeLists[i]=p.DragCubes;Need(cubeLists[i]==null||cubeLists[i].GetType()==typeof(DragCubeList),"Unsupported concrete drag-cube owner.");
                    children[i]=p.children.ToArray();attached[i]=children[i].Select(c=>c.isAttached).ToArray();BindBody(p.rb,i,"part-rb",binding);BindBody(p.servoRb,i,"servo-rb",binding);BindBody(recipients[i],i,"aero-recipient",binding);
                }
                var order=new List<Part>();Walk(vessel.rootPart,order,0);preorder=order.ToArray();Report.reachableParts=preorder.Length;Report.boundParts=parts.Length;Report.bodyBindings=binding.ToArray();
                Report.topologySha256=TopologyHash();Report.providerEpoch=StockSha+"/"+Report.topologySha256;
                for(int i=0;i<Targets.Length;i++) {ConvectionSparseKernel.Pin(Targets[i],typeof(FlightIntegrator),Tokens[i]);Need(ConvectionSparseKernel.Hash(Targets[i].GetMethodBody().GetILAsByteArray())==IlHashes[i],"Pinned original aero body differs.");}
                QualifyGraph();Report.graphAdmissions=true;
                liftCurve=PhysicsGlobals.BodyLiftCurve.liftMachCurve;Need(liftCurve!=null,"Original lift Mach curve absent.");liftAnimationCurve=liftCurve.Curve;
                var dm=new DynamicMethod("continuum_original_drag_value",typeof(double),new[] {typeof(FlightIntegrator),typeof(Part)},typeof(RetainedOrderedBodyAeroCapture),true);var il=dm.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Callvirt,DragValue);il.Emit(OpCodes.Ret);originalDrag=(Func<FlightIntegrator,Part,double>)dm.CreateDelegate(typeof(Func<FlightIntegrator,Part,double>));
                long storage=Stopwatch.GetTimestamp();for(int i=0;i<Roots;i++)work[i]=new RootWork(parts.Length);Report.storageTicks=Stopwatch.GetTimestamp()-storage;
                active=this;installing=true;
                for(int i=0;i<Targets.Length;i++) {harmony.Patch(Targets[i],prefix:i<2?new HarmonyMethod(Method(i==0?"BeforeIntegrate":"BeforeAero")):null,transpiler:new HarmonyMethod(Method("Transform")),finalizer:i<2?new HarmonyMethod(Method(i==0?"AfterIntegrate":"AfterAero")):null);Report.patchedMethods++;}
                Need(Report.shapeAdmissions==4&&Report.staticCallsites==14&&ExactGraph(),"Original command callsite installation differs.");Report.graphInstalled=true;Report.status=Report.cleanupStatus="installed";
            }
            catch(Exception e) {Fault(e);Report.status="installation-failed";Remove();}
            finally {installing=false;Report.installationTicks=Stopwatch.GetTimestamp()-began;}
        }
        void BindBody(Rigidbody body,int slot,string role,List<BodyBinding> binding)
        {
            if(ReferenceEquals(body,null))return;int id;if(!bodies.TryGetValue(body,out id)) {id=body.GetInstanceID();Need(id!=0,"Native body identity absent.");bodies.Add(body,id);}
            binding.Add(new BodyBinding {bodyId=id,partSlot=slot,partId=parts[slot].flightID,role=role});
        }
        void Walk(Part p,List<Part> order,int level)
        {Need(level<MaxDepth&&partSlots.ContainsKey(p)&&!order.Contains(p),"Attached traversal outside bounded tree.");order.Add(p);foreach(Part child in p.children)if(child.isAttached)Walk(child,order,level+1);}
        string TopologyHash()
        {
            using(var bytes=new MemoryStream())using(var writer=new BinaryWriter(bytes)) {writer.Write(vessel.id.ToByteArray());foreach(Part p in parts) {writer.Write(p.flightID);int slot=partSlots[p];writer.Write(p.rb==null?0:bodies[p.rb]);writer.Write(p.servoRb==null?0:bodies[p.servoRb]);writer.Write(children[slot].Length);for(int i=0;i<children[slot].Length;i++) {writer.Write(children[slot][i].flightID);writer.Write(attached[slot][i]);}}writer.Flush();return ConvectionSparseKernel.Hash(bytes.ToArray());}
        }
        bool TopologyIntact()
        {
            if(FlightGlobals.ActiveVessel!=vessel||!vessel.loaded||vessel.packed||vessel.parts.Count!=parts.Length||vessel.rootPart!=preorder[0]||PhysicsGlobals.BodyLiftCurve.liftMachCurve!=liftCurve||!ReferenceEquals(liftCurve.Curve,liftAnimationCurve))return false;
            for(int i=0;i<parts.Length;i++) {Part p=parts[i];if(p==null||!ReferenceEquals(vessel.parts[i],p)||p.vessel!=vessel||!ReferenceEquals(p.Rigidbody,recipients[i])||!ReferenceEquals(p.rb,ownBodies[i])||!ReferenceEquals(p.servoRb,servoBodies[i])||!ReferenceEquals(p.bodyLiftOnlyProvider,providers[i])||!ReferenceEquals(p.DragCubes,cubeLists[i])||p.children.Count!=children[i].Length)return false;for(int j=0;j<children[i].Length;j++)if(!ReferenceEquals(p.children[j],children[i][j])||p.children[j].isAttached!=attached[i][j])return false;}
            return true;
        }
        void QualifyGraph(bool checkOwnership=true)
        {
            var seen=new HashSet<MethodBase>();var pending=new Stack<MethodBase>();pending.Push(Integrate);
            while(pending.Count>0)
            {
                var m=pending.Pop();Need(m!=null,"Original graph target absent.");if(!seen.Add(m))continue;Need(seen.Count<=512,"Original graph bound exceeded.");if(checkOwnership)Need(Harmony.GetPatchInfo(m)?.Owners.Any()!=true,"Foreign original aero graph owner: "+m);graph.Add(m);
                if(m.Module.Assembly!=typeof(FlightIntegrator).Assembly)continue;var body=m.GetMethodBody();Need(body!=null,"Reachable game body absent.");
                foreach(var pair in PatchProcessor.ReadMethodBody(m))
                {
                    Need(pair.Key!=OpCodes.Calli&&pair.Key!=OpCodes.Ldftn&&pair.Key!=OpCodes.Ldvirtftn,"Indirect original aero callback unsupported.");
                    if(pair.Key!=OpCodes.Call&&pair.Key!=OpCodes.Callvirt&&pair.Key!=OpCodes.Newobj)continue;var called=pair.Value as MethodBase;Need(called!=null,"Original called metadata absent.");
                    if(called.DeclaringType==typeof(ILiftProvider)) {var getter=AccessTools.PropertyGetter(typeof(ModuleLiftingSurface),"IsLifting");foreach(Part p in parts)if(p.bodyLiftOnlyProvider!=null) {var map=p.bodyLiftOnlyProvider.GetType().GetInterfaceMap(typeof(ILiftProvider));int n=Array.FindIndex(map.InterfaceMethods,x=>Same(x,called));Need(n>=0&&Same(map.TargetMethods[n],getter),"Unknown original lift-provider dispatch.");}pending.Push(getter);continue;}
                    Need(!called.DeclaringType.IsInterface,"Unknown original interface dispatch.");var virtualCall=called as MethodInfo;Need(virtualCall==null||!virtualCall.IsVirtual||virtualCall.IsFinal||called.DeclaringType==typeof(FlightIntegrator)||called.Module.Assembly!=typeof(FlightIntegrator).Assembly,"Unknown original virtual game dispatch.");pending.Push(called);
                }
            }
            Report.methods=graph.OrderBy(m=>m.Module.ModuleVersionId).ThenBy(m=>m.MetadataToken).Select(m=>new MethodRow {declaringType=m.DeclaringType.FullName,method=m.Name,metadataToken=m.MetadataToken,assemblyMvid=m.Module.ModuleVersionId.ToString("D"),ilSha256=m.GetMethodBody()==null?null:ConvectionSparseKernel.Hash(m.GetMethodBody().GetILAsByteArray()),patched=Targets.Any(t=>Same(t,m))}).ToArray();
        }
        bool ExactGraph()
        {
            foreach(var m in graph)
            {
                var p=Harmony.GetPatchInfo(m);int target=Array.FindIndex(Targets,t=>Same(t,m));if(target<0) {if(p?.Owners.Any()==true)return false;continue;}
                if(p==null||p.Owners.Any(o=>o!=Owner)||p.Postfixes.Count!=0||p.Transpilers.Count!=1||p.Prefixes.Count!=(target<2?1:0)||p.Finalizers.Count!=(target<2?1:0)||!Patch(p.Transpilers[0],"Transform"))return false;
                if(target<2&&(!Patch(p.Prefixes[0],target==0?"BeforeIntegrate":"BeforeAero")||!Patch(p.Finalizers[0],target==0?"AfterIntegrate":"AfterAero")))return false;
            }
            return true;
        }
        static bool Patch(Patch p,string name) {return p.owner==Owner&&p.priority==Priority.Normal&&p.before.Length==0&&p.after.Length==0&&Same(p.PatchMethod,Method(name));}
        static MethodInfo Method(string name) {return AccessTools.DeclaredMethod(typeof(RetainedOrderedBodyAeroCapture),name);}
        static bool Same(MethodBase a,MethodBase b) {return a!=null&&b!=null&&a.Module.ModuleVersionId==b.Module.ModuleVersionId&&a.MetadataToken==b.MetadataToken&&a.DeclaringType==b.DeclaringType;}
        static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> source,MethodBase __originalMethod)
        {
            int target=Array.FindIndex(Targets,t=>Same(t,__originalMethod));Need(target>=0,"Unknown capture transform.");var code=source.Select(x=>new CodeInstruction(x)).ToArray();Need(code.All(x=>x.blocks.Count==0),"Original capture exception shape differs.");int sites=0;
            foreach(var c in code)
            {
                if(c.opcode!=OpCodes.Call&&c.opcode!=OpCodes.Callvirt)continue;var called=c.operand as MethodInfo;string wrapper=null;
                if(target==0&&called!=null&&called.DeclaringType==typeof(Rigidbody))
                {
                    var p=called.GetParameters();if(called.Name=="AddForce"&&p.Length>=1&&p[0].ParameterType==typeof(Vector3))wrapper=p.Length==1?"ForceDefault":"ForceWithMode";
                    else if(called.Name=="AddTorque"&&p.Length>=1&&p[0].ParameterType==typeof(Vector3))wrapper=p.Length==1?"TorqueDefault":"TorqueMode";
                    else if(called.Name=="AddForceAtPosition"&&p.Length>=2&&p[0].ParameterType==typeof(Vector3)&&p[1].ParameterType==typeof(Vector3))wrapper=p.Length==2?"QueuedPositionDefault":"QueuedPositionMode";
                }
                else if(target==1)
                {
                    if(called!=null&&called.DeclaringType==typeof(Rigidbody)&&called.Name=="set_angularDrag")wrapper="Angular";
                    else if(Same(called,DragValue))wrapper="CalculateDrag";else if(Same(called,Evaluate))wrapper="LiftMach";
                }
                else if((target==2||target==3)&&called!=null&&called.DeclaringType==typeof(Rigidbody)&&called.Name=="AddForceAtPosition")
                {var p=called.GetParameters();Need(p.Length==3&&p[0].ParameterType==typeof(Vector3)&&p[1].ParameterType==typeof(Vector3)&&p[2].ParameterType==typeof(ForceMode),"Original aero force signature differs.");wrapper=target==2?"DragPosition":"LiftPosition";}
                else if(target==3&&Same(called,Projection))wrapper="ProjectLift";
                if(wrapper!=null) {var replacement=Method(wrapper);Need(replacement!=null,"Capture wrapper absent.");var original=called.GetParameters().Select(x=>x.ParameterType).ToList();if(!called.IsStatic)original.Insert(0,called.DeclaringType);Need(replacement.ReturnType==called.ReturnType&&replacement.GetParameters().Select(x=>x.ParameterType).SequenceEqual(original),"Capture wrapper operand shape differs.");c.opcode=OpCodes.Call;c.operand=replacement;sites++;}
            }
            Need(sites==new[] {5,6,1,2}[target],"Original command/input callsite count differs.");var owner=active;
            if(owner!=null) {if(owner.installing&&!owner.admitted[target]) {owner.admitted[target]=true;owner.Report.shapeAdmissions++;owner.Report.staticCallsites+=sites;var row=owner.Report.methods.First(m=>m.patched&&m.metadataToken==Tokens[target]);row.instructionCount=code.Length;row.staticCallsites=sites;}else if(owner.cleaning)owner.Report.cleanupShapeChecks++;else throw new InvalidOperationException("Unexpected capture transform recomposition.");}
            return code;
        }
        static void BeforeIntegrate(FlightIntegrator __instance,Part part,out int __state)
        {
            __state=0;var o=active;if(o==null||o.disabled)return;long start=Stopwatch.GetTimestamp();
            try
            {
                Need(Thread.CurrentThread.ManagedThreadId==o.thread,"Capture thread changed.");
                if(o.current==null)
                {
                    if(o.Report.capturedRoots==Roots||part!=o.vessel.rootPart||__instance.Vessel!=o.vessel||!o.capturing())return;
                    Need(__instance.GetType()==typeof(FlightIntegrator)&&o.TopologyIntact()&&o.ExactGraph()&&!FlightDriver.Pause&&TimeWarp.CurrentRate==1&&Time.timeScale==1,"Captured original root state/graph differs.");
                    o.current=o.work[o.Report.capturedRoots];o.integrator=__instance;var r=o.current.result;r.sequence=o.Report.capturedRoots++;r.frame=Time.frameCount;r.fixedTimeSeconds=Time.fixedTime;r.startedTicks=start;r.topologyIntact=true;o.current.buffer.Begin(o.Report.runId,o.Report.vesselId,o.Report.topologySha256,o.Report.providerEpoch,r.sequence);
                }
                Need(ReferenceEquals(__instance,o.integrator)&&o.depth<MaxDepth&&o.current.rowCount<o.preorder.Length&&ReferenceEquals(o.preorder[o.current.rowCount],part),"Original attached part order differs.");
                int ordinal=o.current.rowCount++;o.stack[o.depth++]=ordinal;__state=o.depth;o.current.rows[ordinal].partSlot=o.partSlots[part];
                var input=o.current.buffer.InputAt(ordinal);input.ordinal=ordinal;input.partId=part.flightID;input.providerEpoch=o.Report.providerEpoch;
            }
            catch(Exception e) {o.Fault(e);}
            finally {if(o.current!=null)o.current.result.copyTicks+=Stopwatch.GetTimestamp()-start;}
        }
        static Exception AfterIntegrate(Exception __exception,int __state)
        {
            var o=active;if(o==null||__state==0)return __exception;long start=Stopwatch.GetTimestamp();
            try
            {
                Need(o.current!=null&&o.depth==__state,"Original root stack drift.");if(__exception!=null)o.Report.originalExceptions++;o.depth--;
                if(o.depth==0) {var r=o.current.result;r.rowCount=o.current.rowCount;r.commandCount=o.current.commandCount;r.originalReturned=__exception==null;r.topologyIntact=o.TopologyIntact();bool returned=true;for(int i=0;i<r.commandCount;i++)if(!o.current.tape[i].returned)returned=false;r.commandTapeComplete=r.originalReturned&&r.topologyIntact&&r.rowCount==o.preorder.Length&&returned;if(r.commandTapeComplete)o.Report.completedRoots++;r.copyTicks+=Stopwatch.GetTimestamp()-start;r.endedTicks=Stopwatch.GetTimestamp();o.current=null;o.integrator=null;}
            }
            catch(Exception e) {o.Fault(e);}
            finally {if(o.current!=null)o.current.result.copyTicks+=Stopwatch.GetTimestamp()-start;}
            return __exception;
        }
        static void BeforeAero(FlightIntegrator __instance,Part part,out int __state)
        {
            __state=-1;var o=active;if(o?.current==null||o.disabled)return;long start=Stopwatch.GetTimestamp();
            try
            {
                int n=o.stack[o.depth-1];var row=o.current.rows[n];Need(ReferenceEquals(o.parts[row.partSlot],part)&&ReferenceEquals(__instance,o.integrator),"Aero row boundary differs.");__state=n;row.aeroEntered=true;row.cleared=part.forces.Count==0&&Zero(part.force)&&Zero(part.torque);row.initialLift=part.bodyLiftScalar;
                var p=o.current.buffer.InputAt(n);Rigidbody body=part.Rigidbody;p.recipientBodyId=ReferenceEquals(body,null)?0:o.bodies[body];p.ownBody=part.rb!=null;p.servoBody=part.servoRb!=null;p.shielded=part.ShieldedFromAirstream;p.submergedPortion=part.submergedPortion;p.atmosphericDensity=part.atmDensity;p.angularDragByIntegrator=part.angularDragByFI;p.angularDrag=part.angularDrag;p.bodyLiftMultiplier=part.bodyLiftMultiplier;p.applyDrag=ApplyDrag(__instance);p.applyParentDrag=ApplyParent(__instance);p.accelerationMode=Acceleration(__instance);p.angularDragMultiplier=AngularMultiplier(__instance);p.dragMultiplier=DragMultiplier(__instance);p.globalBodyLiftMultiplier=LiftMultiplier(__instance);p.pseudoReMultiplier=__instance.pseudoReDragMult;
            }
            catch(Exception e) {o.Fault(e);}
            finally {if(o.current!=null)o.current.result.copyTicks+=Stopwatch.GetTimestamp()-start;}
        }
        static Exception AfterAero(Exception __exception,Part part,int __state)
        {
            var o=active;if(o?.current==null||__state<0)return __exception;long start=Stopwatch.GetTimestamp();
            try
            {
                var w=o.current;var row=w.rows[__state];var input=w.buffer.InputAt(__state);row.aeroReturned=__exception==null;
                input.velocity=V(part.dragVector);row.original=new RetainedOrderedBodyAeroResult {ordinal=__state,partId=part.flightID,speedSquared=part.dragVectorSqrMag,speed=part.dragVectorMag,direction=V(part.dragVectorDir),dynamicPressure=part.dynamicPressurekPa,dragScalar=part.dragScalar,bodyLiftScalar=part.bodyLiftScalar,bodyLiftScalarWritten=row.liftReached};row.exposed=part.exposedArea;row.area=part.aerodynamicArea;row.submergedPressure=part.submergedDynamicPressurekPa;row.localPosition=part.bodyLiftLocalPosition;row.localVector=part.bodyLiftLocalVector;
                w.result.copyTicks+=Stopwatch.GetTimestamp()-start;long reference=Stopwatch.GetTimestamp();string reason=null;
                if(__exception==null&&row.dragCalculated&&!o.disabled&&w.buffer.TryEvaluate(__state,out reason)) {row.eligible=true;w.result.eligibleRows++;o.Report.eligibleRows++;row.matched=w.buffer.Matches(__state,row.original,w.tail,__state*8,row.tailCount,out reason);if(row.matched) {w.result.matchedRows++;o.Report.matchedRows++;}else {w.result.mismatchedRows++;o.Report.referenceMismatches++;}row.reason=reason;}
                else {row.reason=__exception!=null?"original-exception":!row.dragCalculated?"original-drag-calculation-not-reached":o.disabled?"capture-fault":reason;w.result.unsupportedRows++;o.Report.unsupportedRows++;}
                w.result.referenceTicks+=Stopwatch.GetTimestamp()-reference;
            }
            catch(Exception e) {o.Fault(e);}
            return __exception;
        }
        static int Record(Rigidbody body,int kind,Vector3 force,Vector3 point,ForceMode mode,float angular,bool tail)
        {
            var o=active;if(o?.current==null||o.disabled)return -1;long start=Stopwatch.GetTimestamp();
            try
            {
                var w=o.current;int n=o.stack[o.depth-1],id;Need(o.bodies.TryGetValue(body,out id),"Original command recipient not bound.");if(w.commandCount==w.tape.Length) {o.Report.droppedCommands++;throw new InvalidOperationException("Actual command tape bound exceeded.");}
                int snapshot;
                if(!w.snapshotIndices.TryGetValue(body,out snapshot))
                {
                    Need(body!=null&&w.snapshotCount<w.snapshots.Length,"Reached body snapshot owner/bound differs.");
                    var s=new BodyWork {id=id,first=w.commandCount,start=Stopwatch.GetTimestamp(),alive=true};
                    s.mass=body.mass;s.position=body.position;s.rotation=body.rotation;s.localCom=body.centerOfMass;s.worldCom=body.worldCenterOfMass;s.inertia=body.inertiaTensor;s.inertiaRotation=body.inertiaTensorRotation;s.velocity=body.velocity;s.angularVelocity=body.angularVelocity;
                    s.kinematic=body.isKinematic;s.activeSelf=body.gameObject.activeSelf;s.activeHierarchy=body.gameObject.activeInHierarchy;s.sleeping=body.IsSleeping();s.end=Stopwatch.GetTimestamp();
                    Need(body!=null&&body.GetInstanceID()==id&&BodyAeroVector.Number(s.mass)&&s.mass>0&&Finite(s.position)&&Finite(s.localCom)&&Finite(s.worldCom)&&Finite(s.inertia)&&s.inertia.x>0&&s.inertia.y>0&&s.inertia.z>0&&Finite(s.velocity)&&Finite(s.angularVelocity)&&Unit(s.rotation)&&Unit(s.inertiaRotation),"First-command native body properties outside finite positive principal-inertia domain.");
                    snapshot=w.snapshotCount++;w.snapshots[snapshot]=s;w.snapshotIndices.Add(body,snapshot);
                }
                int slot=w.commandCount++;var input=w.buffer.InputAt(n);w.tape[slot]=new Trace {sequence=slot,ordinal=n,part=input.partId,body=id,kind=kind,force=force,point=point,mode=(int)mode,angular=angular,tail=tail,snapshot=snapshot};
                if(tail)
                {
                    var row=w.rows[n];Need(row.tailCount<8,"Original body-tail command bound exceeded.");var k=kind==3?BodyAeroCommandKind.AngularDrag:kind==4?BodyAeroCommandKind.DragAtPosition:BodyAeroCommandKind.LiftAtPosition;
                    w.tail[n*8+row.tailCount++]=new BodyAeroCommand {ordinal=n,partId=input.partId,recipientBodyId=id,kind=k,forceMode=(int)mode,force=V(force),point=V(point),angularDrag=angular};if(kind==4)input.dragApplicationPoint=V(point);else if(kind==5)input.liftApplicationPoint=V(point);
                }
                return slot;
            }
            catch(Exception e) {o.Fault(e);return -1;}
            finally {if(o.current!=null)o.current.result.copyTicks+=Stopwatch.GetTimestamp()-start;}
        }
        static void Returned(int slot) {var o=active;if(slot>=0&&o?.current!=null)o.current.tape[slot].returned=true;}
        static void ForceDefault(Rigidbody body,Vector3 force) {int n=Record(body,0,force,Vector3.zero,ForceMode.Force,0,false);body.AddForce(force);Returned(n);}
        static void ForceWithMode(Rigidbody body,Vector3 force,UnityEngine.ForceMode mode) {int n=Record(body,0,force,Vector3.zero,mode,0,false);body.AddForce(force,mode);Returned(n);}
        static void TorqueDefault(Rigidbody body,Vector3 force) {int n=Record(body,1,force,Vector3.zero,UnityEngine.ForceMode.Force,0,false);body.AddTorque(force);Returned(n);}
        static void TorqueMode(Rigidbody body,Vector3 force,UnityEngine.ForceMode mode) {int n=Record(body,1,force,Vector3.zero,mode,0,false);body.AddTorque(force,mode);Returned(n);}
        static void QueuedPositionDefault(Rigidbody body,Vector3 force,Vector3 point) {int n=Record(body,2,force,point,UnityEngine.ForceMode.Force,0,false);body.AddForceAtPosition(force,point);Returned(n);}
        static void QueuedPositionMode(Rigidbody body,Vector3 force,Vector3 point,UnityEngine.ForceMode mode) {int n=Record(body,2,force,point,mode,0,false);body.AddForceAtPosition(force,point,mode);Returned(n);}
        static void Angular(Rigidbody body,float value) {int n=Record(body,3,Vector3.zero,Vector3.zero,UnityEngine.ForceMode.Force,value,true);body.angularDrag=value;Returned(n);}
        static void DragPosition(Rigidbody body,Vector3 force,Vector3 point,UnityEngine.ForceMode mode) {int n=Record(body,4,force,point,mode,0,true);body.AddForceAtPosition(force,point,mode);Returned(n);}
        static void LiftPosition(Rigidbody body,Vector3 force,Vector3 point,UnityEngine.ForceMode mode) {int n=Record(body,5,force,point,mode,0,true);body.AddForceAtPosition(force,point,mode);Returned(n);}
        static double CalculateDrag(FlightIntegrator fi,Part part)
        {
            var o=active;double result=o.originalDrag(fi,part);if(o.current!=null&&!o.disabled) {long start=Stopwatch.GetTimestamp();try {int n=o.stack[o.depth-1];o.current.buffer.InputAt(n).originalDragValue=result;o.current.rows[n].dragCalculated=true;}catch(Exception e) {o.Fault(e);}finally {o.current.result.copyTicks+=Stopwatch.GetTimestamp()-start;}}return result;
        }
        static float LiftMach(FloatCurve curve,float input)
        {
            float value=curve.Evaluate(input);var o=active;if(o?.current!=null&&!o.disabled) {long start=Stopwatch.GetTimestamp();try {Need(ReferenceEquals(curve,o.liftCurve),"Reached lift curve wrapper differs.");int n=o.stack[o.depth-1];var row=o.current.buffer.InputAt(n);row.liftMachSample=value;row.bodyLiftEligible=true;var force=o.parts[o.current.rows[n].partSlot].DragCubes.LiftForce;row.liftForceEligible=force!=Vector3.zero&&Finite(force);o.current.rows[n].liftReached=true;o.current.rows[n].liftInput=input;}catch(Exception e) {o.Fault(e);}finally {o.current.result.copyTicks+=Stopwatch.GetTimestamp()-start;}}return value;
        }
        static Vector3 ProjectLift(Vector3 vector,Vector3 normal)
        {
            Vector3 result=Vector3.ProjectOnPlane(vector,normal);var o=active;if(o?.current!=null&&!o.disabled) {long start=Stopwatch.GetTimestamp();try {int n=o.stack[o.depth-1];o.current.buffer.InputAt(n).rotatedScaledLift=V(vector);o.current.buffer.InputAt(n).liftGeometryCaptured=true;}catch(Exception e) {o.Fault(e);}finally {o.current.result.copyTicks+=Stopwatch.GetTimestamp()-start;}}return result;
        }
        static BodyAeroVector V(Vector3 v) {return new BodyAeroVector(v.x,v.y,v.z);}
        static bool Finite(Vector3 v) {return BodyAeroVector.Number(v.x)&&BodyAeroVector.Number(v.y)&&BodyAeroVector.Number(v.z);}
        static bool Unit(Quaternion q) {return BodyAeroVector.Number(q.x)&&BodyAeroVector.Number(q.y)&&BodyAeroVector.Number(q.z)&&BodyAeroVector.Number(q.w)&&Math.Abs((double)q.x*q.x+(double)q.y*q.y+(double)q.z*q.z+(double)q.w*q.w-1)<=0.001;}
        static bool Zero(Vector3d v) {return v.x==0&&v.y==0&&v.z==0;}
        void Fault(Exception e) {disabled=true;Report.errors++;Report.detail=e.ToString();}
        static void Need(bool value,string reason) {if(!value)throw new InvalidOperationException(reason);}
        void Remove()
        {
            long start=Stopwatch.GetTimestamp();bool gone=false;cleaning=true;try {harmony.UnpatchAll(Owner);gone=!Targets.Any(m=>Harmony.GetPatchInfo(m)?.Owners.Contains(Owner)==true);Report.graphReleased=gone&&graph.All(m=>Harmony.GetPatchInfo(m)?.Owners.Any()!=true);Report.cleanupStatus=Report.graphReleased?"removed-owned-patches":"cleanup-error";if(!Report.graphReleased)Report.errors++;}catch(Exception e) {Fault(e);Report.cleanupStatus="cleanup-error";}finally {if(gone&&active==this)active=null;cleaning=false;Report.cleanupTicks=Stopwatch.GetTimestamp()-start;}
        }
        public void Dispose()
        {
            if(disposed)return;if(Thread.CurrentThread.ManagedThreadId!=thread) {Fault(new InvalidOperationException("Capture cleanup thread changed; main-thread disposal retry remains required."));throw new InvalidOperationException(Report.detail);}disposed=true;
            try {if(current!=null) {Report.errors++;Report.detail="Original root still open during cleanup.";}if(Report.graphInstalled&&(!ExactGraph()||!TopologyIntact())) {Report.errors++;Report.detail="Frozen original aero graph/ownership changed before cleanup.";}}catch(Exception e) {Fault(e);}finally {Remove();}
            try {Report.roots=new Root[Report.capturedRoots];for(int i=0;i<Report.roots.Length;i++)Report.roots[i]=Freeze(work[i]);Report.status=Report.completedRoots==Roots&&Report.errors==0&&Report.originalExceptions==0&&Report.referenceMismatches==0?"observed":"incomplete";using(var stream=new FileStream(Report.path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream))writer.Write(ReportJson.Encode(Report));Report.sha256=HashFile(Report.path);}catch(Exception e) {Fault(e);Report.status="export-failed";}
        }
        Root Freeze(RootWork w)
        {
            var r=w.result;r.rows=new Row[w.rowCount];r.commands=new Command[w.commandCount];r.bodySnapshots=new BodySnapshot[w.snapshotCount];
            for(int i=0;i<w.snapshotCount;i++) {var s=w.snapshots[i];r.bodySnapshots[i]=new BodySnapshot {index=i,bodyId=s.id,firstCommandSequence=s.first,startedTicks=s.start,endedTicks=s.end,alive=s.alive,isKinematic=s.kinematic,dynamic=!s.kinematic,activeSelf=s.activeSelf,activeInHierarchy=s.activeHierarchy,sleeping=s.sleeping,floatBits=Bits(s.mass,s.position.x,s.position.y,s.position.z,s.rotation.x,s.rotation.y,s.rotation.z,s.rotation.w,s.localCom.x,s.localCom.y,s.localCom.z,s.worldCom.x,s.worldCom.y,s.worldCom.z,s.inertia.x,s.inertia.y,s.inertia.z,s.inertiaRotation.x,s.inertiaRotation.y,s.inertiaRotation.z,s.inertiaRotation.w,s.velocity.x,s.velocity.y,s.velocity.z,s.angularVelocity.x,s.angularVelocity.y,s.angularVelocity.z)};}
            for(int i=0;i<w.rowCount;i++)
            {
                var a=w.buffer.InputAt(i);var b=w.rows[i];var x=b.original;uint flags=0;bool[] flag={a.ownBody,a.servoBody,a.shielded,a.applyDrag,a.applyParentDrag,a.angularDragByIntegrator,a.accelerationMode,a.bodyLiftEligible,a.liftForceEligible,a.liftGeometryCaptured};for(int n=0;n<flag.Length;n++)if(flag[n])flags|=1u<<n;
                var row=new Row {ordinal=i,partSlot=b.partSlot,partId=a.partId,recipientBodyId=a.recipientBodyId,inputFlags=flags,aeroEntered=b.aeroEntered,aeroReturned=b.aeroReturned,dragCalculated=b.dragCalculated,liftCurveReached=b.liftReached,queuedStateCleared=b.cleared,bodyLiftScalarWritten=x.bodyLiftScalarWritten,tailCommandCount=b.tailCount,referenceStatus=b.eligible?(b.matched?"matched":"mismatch"):"unsupported",referenceReason=b.reason,
                    inputFloatBits=Bits(a.velocity.x,a.velocity.y,a.velocity.z,a.dragApplicationPoint.x,a.dragApplicationPoint.y,a.dragApplicationPoint.z,a.liftApplicationPoint.x,a.liftApplicationPoint.y,a.liftApplicationPoint.z,a.rotatedScaledLift.x,a.rotatedScaledLift.y,a.rotatedScaledLift.z,a.angularDrag,a.angularDragMultiplier,a.dragMultiplier,a.bodyLiftMultiplier,a.globalBodyLiftMultiplier,a.liftMachSample),inputDoubleBits=DoubleBits(a.submergedPortion,a.atmosphericDensity,a.originalDragValue,a.pseudoReMultiplier),outputFloatBits=Bits(x.speedSquared,x.speed,x.direction.x,x.direction.y,x.direction.z,x.dragScalar,x.bodyLiftScalar),dynamicPressureBits=BitConverter.DoubleToInt64Bits(x.dynamicPressure),extraFloatBits=Bits(b.initialLift,b.localPosition.x,b.localPosition.y,b.localPosition.z,b.localVector.x,b.localVector.y,b.localVector.z,b.liftInput),extraDoubleBits=DoubleBits(b.exposed,b.area,b.submergedPressure),tailKinds=new BodyAeroCommandKind[b.tailCount]};
                for(int n=0;n<b.tailCount;n++)row.tailKinds[n]=w.tail[i*8+n].kind;r.rows[i]=row;
            }
            string[] kinds={"AddForce","AddTorque","AddForceAtPosition","set_angularDrag","AddForceAtPosition","AddForceAtPosition"};
            for(int i=0;i<w.commandCount;i++) {var c=w.tape[i];r.commands[i]=new Command {sequence=i,ordinal=c.ordinal,partId=c.part,recipientBodyId=c.body,forceMode=c.mode,bodySnapshotIndex=c.snapshot,phase=!c.tail?"queued-integration":c.kind==3?"aero-angular":c.kind==4?"aero-drag":"aero-lift",kind=kinds[c.kind],forceBits=Bits(c.force.x,c.force.y,c.force.z),pointBits=Bits(c.point.x,c.point.y,c.point.z),angularDragBits=Bits(c.angular)[0],originalReturned=c.returned};}return r;
        }
        [StructLayout(LayoutKind.Explicit)]struct FloatBits {[FieldOffset(0)]public float value;[FieldOffset(0)]public uint bits;}
        static uint[] Bits(params float[] values) {var r=new uint[values.Length];for(int i=0;i<r.Length;i++)r[i]=new FloatBits {value=values[i]}.bits;return r;}
        static long[] DoubleBits(params double[] values) {return values.Select(BitConverter.DoubleToInt64Bits).ToArray();}
        static string HashFile(string path) {using(var s=File.OpenRead(path))using(var h=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
        public bool IsValidFinal(out string reason)
        {
            reason=null;if(!disposed||Report.status!="observed"||Report.cleanupStatus!="removed-owned-patches"||!Report.graphAdmissions||!Report.graphInstalled||!Report.graphReleased||Report.shapeAdmissions!=4||Report.staticCallsites!=14||Report.patchedMethods!=4||Report.capturedRoots!=Roots||Report.completedRoots!=Roots||Report.errors!=0||Report.originalExceptions!=0||Report.droppedCommands!=0||Report.referenceMismatches!=0||Report.matchedRows!=Report.eligibleRows||string.IsNullOrEmpty(Report.sha256)) {reason="Original command capture/retained reference coverage or lifecycle incomplete.";return false;}
            foreach(var r in Report.roots)
            {
                if(!r.originalReturned||!r.commandTapeComplete||!r.topologyIntact||r.endedTicks<=r.startedTicks||r.rowCount!=preorder.Length||r.rows.Any(x=>!x.aeroEntered||!x.aeroReturned||!x.queuedStateCleared)||r.commands.Any(x=>!x.originalReturned)) {reason="Original row/tape/window coverage incomplete.";return false;}
                var seen=new HashSet<int>();long last=r.startedTicks;int first=-1;
                for(int i=0;i<r.bodySnapshots.Length;i++) {var s=r.bodySnapshots[i];if(s.index!=i||!s.alive||!seen.Add(s.bodyId)||s.startedTicks<last||s.endedTicks<s.startedTicks||s.endedTicks>r.endedTicks||s.firstCommandSequence<=first||s.firstCommandSequence>=r.commands.Length||r.commands[s.firstCommandSequence].bodySnapshotIndex!=i||r.commands[s.firstCommandSequence].recipientBodyId!=s.bodyId) {reason="First-command body snapshot coverage incomplete.";return false;}last=s.endedTicks;first=s.firstCommandSequence;}
                foreach(var c in r.commands)if(c.bodySnapshotIndex<0||c.bodySnapshotIndex>=r.bodySnapshots.Length||r.bodySnapshots[c.bodySnapshotIndex].bodyId!=c.recipientBodyId||r.bodySnapshots[c.bodySnapshotIndex].firstCommandSequence>c.sequence) {reason="Reached command body snapshot binding incomplete.";return false;}
            }
            return true;
        }
    }
}
