using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class ZeroNativeForcePruning : IDisposable
    {
        const string Owner="continuum.zero-native-force-pruning";
        const int MaxDepth=128,Comparisons=4;
        static readonly MethodInfo Integrate=AccessTools.DeclaredMethod(typeof(FlightIntegrator),"Integrate",new[] {typeof(Part)});
        static readonly MethodInfo Force=AccessTools.DeclaredMethod(typeof(Rigidbody),"AddForce",new[] {typeof(Vector3)});
        static readonly MethodInfo Torque=AccessTools.DeclaredMethod(typeof(Rigidbody),"AddTorque",new[] {typeof(Vector3)});
        static readonly MethodInfo[] NativeRoots={Force,Torque,AccessTools.DeclaredMethod(typeof(UnityEngine.Object),"op_Equality",new[] {typeof(UnityEngine.Object),typeof(UnityEngine.Object)}),AccessTools.DeclaredMethod(typeof(UnityEngine.Object),"op_Inequality",new[] {typeof(UnityEngine.Object),typeof(UnityEngine.Object)})};
        static ZeroNativeForcePruning active;
        readonly Harmony harmony=new Harmony(Owner);readonly Vessel vessel;readonly Func<bool> capturing;readonly int thread=Thread.CurrentThread.ManagedThreadId;
        readonly Part[] parts,preorder;readonly int[] preorderSlots;readonly Rigidbody[] ownBodies,servoBodies;readonly Part[][] children;readonly bool[][] attached;
        readonly Dictionary<Part,int> slots=new Dictionary<Part,int>();readonly List<MethodBase> nativeGraph=new List<MethodBase>();
        readonly int[] stack=new int[MaxDepth];readonly Window[] comparisons=new Window[Comparisons];readonly Window scratch=new Window();
        Window current;FlightIntegrator currentIntegrator;int depth,rows,comparisonCount;bool disabled,disposed,installing,cleaning,first=true,firstCandidate=true;
        public readonly ReportData Report;

        [Serializable] public sealed class ReportData
        {
            public string schema="continuum-zero-native-force-pruning/v1",status="starting",cleanupStatus="not-installed",mode,runId,vesselId,topologySha256,detail;
            public string originalAssemblySha256="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27",originalAssemblyMvid="10657063-2fc3-43a7-84fa-d39e75e877bf";
            public string scope="Only the one default AddForce(Vector3) and one default AddTorque(Vector3) callsite in original Integrate. Exact signed-zero arguments may be omitted on bound live bodies only after an independently retained native canary passes. Original explicit-mode acceleration, queued point forces, arithmetic/conversions, field clears, aero and child order remain unchanged; one force fold per root.";
            public string clockScope="Root wall time includes installed hooks/counters/topology admission and candidate receiver/liveness guards. No explicit kinematic/activity property reads; Unity liveness can itself enter native code for invalid objects, and that cost remains charged. Both branches require pre-capture real-root warmup. Exactly two adjacent dynamic-state comparison pairs, not identical-input replay or clean FPS. No per-command clocks.";
            public string graphScope="Pinned original Integrate plus unowned reachable default Unity AddForce/AddTorque wrappers admitted at installation and cleanup under a frozen research lease. No hot Harmony scans and no universal dynamic-mod ownership claim.";
            public string nativeQualificationStatus="not-performed",nativeQualificationGate="Trusted parent must bind an independently retained passing Unity2019 zero/signed-zero/default-mode sleep/momentum canary across dynamic/kinematic/inactive valid bodies. Invalid/destroyed receivers always forward. Source and docs alone are not this proof; the boolean parameter transports qualification and is not a receipt.";
            public string forceDocumentation="https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Rigidbody.AddForce.html",torqueDocumentation="https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Rigidbody.AddTorque.html";
            public bool requested=true,nativeQualified,graphAdmissions,graphInstalled,graphReleased;
            public int processId,boundParts,reachableParts,boundBodies,patchedMethods,shapeAdmissions,cleanupShapeChecks,staticPlainForceSites,staticPlainTorqueSites,staticUnchangedAccelerationSites,staticUnchangedPointSites;
            public int errors,originalExceptions,unsupportedZeroCalls,completedComparisons,controlComparisonRoots,candidateComparisonRoots,warmupOriginalRoots,warmupCandidateRoots,coldCaptureRoots;
            public long clockFrequency=Stopwatch.Frequency,installationTicks,cleanupTicks,allRoots,captureRoots,captureCandidateRoots,queryCompleteTicks;
            public long forceCalls,torqueCalls,zeroForceCalls,zeroTorqueCalls,forwardedForceCalls,forwardedTorqueCalls,skippedForceCalls,skippedTorqueCalls,bodyGuardNativeReads,receiverGuardChecks;
            public long warmupOriginalCompleteTicks,warmupCandidateCompleteTicks,warmupOriginalForwardedForceCalls,warmupOriginalForwardedTorqueCalls,warmupCandidateSkippedForceCalls,warmupCandidateSkippedTorqueCalls;
            public long captureForceCalls,captureTorqueCalls,captureZeroForceCalls,captureZeroTorqueCalls,captureForwardedForceCalls,captureForwardedTorqueCalls,captureSkippedForceCalls,captureSkippedTorqueCalls,captureReceiverGuardChecks;
            public MethodRow[] methods=new MethodRow[0];public BodyBinding[] bodies=new BodyBinding[0];public Window[] windows=new Window[0];public Window firstInvocation=new Window(),firstCandidateInvocation=new Window();
        }
        [Serializable] public sealed class MethodRow
        { public string assembly,assemblySha256,assemblyMvid,declaringType,method,ilSha256;public int metadataToken,instructionCount;public bool patched; }
        [Serializable] public sealed class BodyBinding
        { public int partSlot,bodyId;public uint partId; }
        [Serializable] public sealed class Window
        {
            public int sequence,pair,frame,rowCount,unsupportedZeroCalls;public double fixedTimeSeconds;public string treatment;
            public bool captured,firstInvocation,firstCandidateInvocation,originalReturned,topologyIntact;
            public long startedTicks,endedTicks,completeTicks,forceCalls,torqueCalls,zeroForceCalls,zeroTorqueCalls,forwardedForceCalls,forwardedTorqueCalls,skippedForceCalls,skippedTorqueCalls,bodyGuardNativeReads,receiverGuardChecks;
        }
        public static bool Requested {get{return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_ZERO_NATIVE_FORCE_PRUNING"));}}
        public bool ReadyForCapture {get{return Report.warmupCandidateRoots>0&&Report.warmupCandidateSkippedForceCalls>0&&Report.warmupCandidateSkippedTorqueCalls>0&&(Report.mode!="compare"||Report.warmupOriginalRoots>0&&Report.warmupOriginalForwardedForceCalls>0&&Report.warmupOriginalForwardedTorqueCalls>0);}}
        public bool HasFault {get{return disabled||Report.errors>0;}}
        public bool QualificationFailed {get{return HasFault;}}
        public static ZeroNativeForcePruning Create(string[] args,string runId,Func<bool> capturing,bool nativeQualified=false)
        {
            string mode=Environment.GetEnvironmentVariable("CONTINUUM_ZERO_NATIVE_FORCE_PRUNING");if(string.IsNullOrEmpty(mode))return null;
            if(args==null||string.IsNullOrEmpty(runId)||capturing==null||(mode!="compare"&&mode!="query"))throw new InvalidOperationException("Zero-native-force mode must be compare or query with run/capture owner.");
            if(RetainedOrderedBodyAeroCapture.Requested||!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_STEP_CONTEXT"))||!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_AERO_INTEGRATION_ATTRIBUTION"))||args.Any(a=>a=="--continuum-callback-attribution"||a=="--continuum-live-aero-provider"||a=="--continuum-live-setdrag-provider"||a=="--continuum-setdrag-stress-candidate"))throw new InvalidOperationException("Zero-native-force pruning requires independent original Integrate ownership.");
            return new ZeroNativeForcePruning(mode,runId,capturing,nativeQualified);
        }
        ZeroNativeForcePruning(string mode,string runId,Func<bool> capturing,bool nativeQualified)
        {
            long start=Stopwatch.GetTimestamp();this.capturing=capturing;vessel=FlightGlobals.ActiveVessel;Report=new ReportData {mode=mode,runId=runId,nativeQualified=nativeQualified,nativeQualificationStatus=nativeQualified?"qualified-isolated-zero-command":"not-performed",processId=Process.GetCurrentProcess().Id,vesselId=vessel==null?null:vessel.id.ToString("D")};
            try
            {
                Need(nativeQualified,"Independent native zero-command canary must pass before installation.");Need(active==null&&vessel!=null&&vessel.loaded&&!vessel.packed&&vessel.rootPart!=null&&vessel.parts.Count>0&&vessel.parts.Count<=OrderedBodyAeroPacket.MaximumRows,"One loaded bounded vessel required.");
                StartupPinnedGameIdentity.Require(typeof(FlightIntegrator).Assembly);ConvectionSparseKernel.Pin(Integrate,typeof(FlightIntegrator),100677461);
                Need(ConvectionSparseKernel.Hash(Integrate.GetMethodBody().GetILAsByteArray())=="71f3e8ba1599644c13be502e24d7463524c98188b1f3a075b898c9a8430a0f04","Original Integrate IL differs.");
                parts=vessel.parts.ToArray();ownBodies=new Rigidbody[parts.Length];servoBodies=new Rigidbody[parts.Length];children=new Part[parts.Length][];attached=new bool[parts.Length][];
                var ids=new HashSet<uint>();var bindings=new List<BodyBinding>();
                for(int i=0;i<parts.Length;i++)
                {
                    var p=parts[i];Need(p!=null&&p.flightID!=0&&ids.Add(p.flightID)&&(p.GetType()==typeof(Part)||p.GetType()==typeof(CompoundPart)),"Unknown/repeated part identity.");slots.Add(p,i);ownBodies[i]=p.rb;servoBodies[i]=p.servoRb;children[i]=p.children.ToArray();attached[i]=children[i].Select(c=>c.isAttached).ToArray();
                    if(!ReferenceEquals(p.rb,null)) {Need(p.rb!=null,"Destroyed body at admission.");bindings.Add(new BodyBinding {partSlot=i,partId=p.flightID,bodyId=p.rb.GetInstanceID()});}
                }
                var order=new List<Part>();Walk(vessel.rootPart,order,0);preorder=order.ToArray();preorderSlots=preorder.Select(p=>slots[p]).ToArray();Report.boundParts=parts.Length;Report.reachableParts=preorder.Length;Report.bodies=bindings.ToArray();Report.boundBodies=bindings.Count;Report.topologySha256=TopologyHash();
                AdmitGraph();Report.graphAdmissions=true;for(int i=0;i<Comparisons;i++)comparisons[i]=new Window();
                active=this;installing=true;harmony.Patch(Integrate,prefix:new HarmonyMethod(Method("Before")),transpiler:new HarmonyMethod(Method("Transform")),finalizer:new HarmonyMethod(Method("After")));Report.patchedMethods=1;
                Need(Report.shapeAdmissions==1&&ExactGraph(),"Zero-native-force installation differs.");Report.graphInstalled=true;Report.status=Report.cleanupStatus="installed";
            }
            catch(Exception error) {Fault(error);Report.status="installation-failed";Remove();}
            finally {installing=false;Report.installationTicks=Stopwatch.GetTimestamp()-start;}
        }
        void Walk(Part part,List<Part> order,int level)
        {Need(level<MaxDepth&&slots.ContainsKey(part)&&!order.Contains(part),"Attached topology exceeds bounded tree.");order.Add(part);foreach(var child in part.children)if(child.isAttached)Walk(child,order,level+1);}
        string TopologyHash()
        {
            using(var bytes=new MemoryStream())using(var writer=new BinaryWriter(bytes)) {writer.Write(vessel.id.ToByteArray());for(int i=0;i<parts.Length;i++) {writer.Write(parts[i].flightID);writer.Write(ownBodies[i]==null?0:ownBodies[i].GetInstanceID());writer.Write(servoBodies[i]==null?0:servoBodies[i].GetInstanceID());writer.Write(children[i].Length);for(int j=0;j<children[i].Length;j++) {writer.Write(children[i][j].flightID);writer.Write(attached[i][j]);}}writer.Flush();return ConvectionSparseKernel.Hash(bytes.ToArray());}
        }
        bool TopologyIntact()
        {
            if(FlightGlobals.ActiveVessel!=vessel||!vessel.loaded||vessel.packed||vessel.rootPart!=preorder[0]||vessel.parts.Count!=parts.Length)return false;
            for(int i=0;i<parts.Length;i++) {var p=parts[i];if(p==null||!ReferenceEquals(vessel.parts[i],p)||p.vessel!=vessel||!ReferenceEquals(p.rb,ownBodies[i])||!ReferenceEquals(p.servoRb,servoBodies[i])||p.children.Count!=children[i].Length)return false;for(int j=0;j<children[i].Length;j++)if(!ReferenceEquals(p.children[j],children[i][j])||p.children[j].isAttached!=attached[i][j])return false;}return true;
        }
        void AdmitGraph(bool checkOwnership=true)
        {
            var pending=new Stack<MethodBase>();var seen=new HashSet<MethodBase>();foreach(var method in NativeRoots)pending.Push(method);
            while(pending.Count>0)
            {
                var method=pending.Pop();Need(method!=null,"Original native wrapper absent.");if(!seen.Add(method))continue;Need(seen.Count<=64,"Native wrapper graph exceeds bound.");nativeGraph.Add(method);if(checkOwnership)Need(Harmony.GetPatchInfo(method)?.Owners.Any()!=true,"Observable native force hook already owned: "+method);
                if((method.Module.Assembly!=typeof(Rigidbody).Assembly&&method.Module.Assembly!=typeof(UnityEngine.Object).Assembly)||method.GetMethodBody()==null)continue;
                foreach(var pair in PatchProcessor.ReadMethodBody(method)) {Need(pair.Key!=OpCodes.Calli&&pair.Key!=OpCodes.Ldftn&&pair.Key!=OpCodes.Ldvirtftn,"Unknown indirect native force wrapper.");if(pair.Key==OpCodes.Call||pair.Key==OpCodes.Callvirt) {var callee=pair.Value as MethodBase;Need(callee!=null,"Native wrapper callee absent.");pending.Push(callee);}}
            }
            if(checkOwnership)Need(Harmony.GetPatchInfo(Integrate)?.Owners.Any()!=true,"Integrate already owned.");var all=new List<MethodBase>(nativeGraph);all.Add(Integrate);var hashes=new Dictionary<Assembly,string>();
            Report.methods=all.OrderBy(m=>m.Module.ModuleVersionId).ThenBy(m=>m.MetadataToken).Select(m=> {string hash;if(!hashes.TryGetValue(m.Module.Assembly,out hash)) {hash=HashFile(m.Module.Assembly.Location);hashes.Add(m.Module.Assembly,hash);}var body=m.GetMethodBody();return new MethodRow {assembly=m.Module.Assembly.GetName().Name,assemblySha256=hash,assemblyMvid=m.Module.ModuleVersionId.ToString("D"),declaringType=m.DeclaringType.FullName,method=m.Name,metadataToken=m.MetadataToken,ilSha256=body==null?null:ConvectionSparseKernel.Hash(body.GetILAsByteArray()),patched=Same(m,Integrate)};}).ToArray();
        }
        bool ExactGraph()
        {
            foreach(var method in nativeGraph)if(Harmony.GetPatchInfo(method)?.Owners.Any()==true)return false;var p=Harmony.GetPatchInfo(Integrate);
            return p!=null&&p.Owners.All(o=>o==Owner)&&p.Prefixes.Count==1&&p.Finalizers.Count==1&&p.Transpilers.Count==1&&p.Postfixes.Count==0&&Patch(p.Prefixes[0],"Before")&&Patch(p.Finalizers[0],"After")&&Patch(p.Transpilers[0],"Transform");
        }
        static bool Same(MethodBase a,MethodBase b) {return a!=null&&b!=null&&a.Module.ModuleVersionId==b.Module.ModuleVersionId&&a.MetadataToken==b.MetadataToken&&a.DeclaringType==b.DeclaringType;}
        static MethodInfo Method(string name) {return AccessTools.DeclaredMethod(typeof(ZeroNativeForcePruning),name);}
        static bool Patch(Patch p,string name) {return p.owner==Owner&&p.priority==Priority.Normal&&p.before.Length==0&&p.after.Length==0&&Same(p.PatchMethod,Method(name));}
        static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> source,MethodBase __originalMethod)
        {
            Need(Same(__originalMethod,Integrate),"Unknown zero-native-force caller.");var code=source.Select(c=>new CodeInstruction(c)).ToArray();Need(code.All(c=>c.blocks.Count==0),"Integrate exception shape differs.");int forces=0,torques=0,acceleration=0,points=0;
            foreach(var c in code)
            {
                if(c.opcode!=OpCodes.Call&&c.opcode!=OpCodes.Callvirt)continue;var method=c.operand as MethodInfo;
                if(Same(method,Force)) {c.opcode=OpCodes.Call;c.operand=Method("ForceDefault");forces++;}
                else if(Same(method,Torque)) {c.opcode=OpCodes.Call;c.operand=Method("TorqueDefault");torques++;}
                else if(method!=null&&method.DeclaringType==typeof(Rigidbody)) {if(method.Name=="AddForce"&&method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[] {typeof(Vector3),typeof(ForceMode)}))acceleration++;else if(method.Name=="AddForceAtPosition"&&method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[] {typeof(Vector3),typeof(Vector3)}))points++;else throw new InvalidOperationException("Unexpected unchanged Integrate native call.");}
            }
            Need(forces==1&&torques==1&&acceleration==2&&points==1,"Original force/torque/point/mode callsite shape differs.");var owner=active;
            if(owner!=null) {if(owner.installing&&owner.Report.shapeAdmissions==0) {owner.Report.shapeAdmissions=1;owner.Report.staticPlainForceSites=forces;owner.Report.staticPlainTorqueSites=torques;owner.Report.staticUnchangedAccelerationSites=acceleration;owner.Report.staticUnchangedPointSites=points;owner.Report.methods.First(m=>m.patched).instructionCount=code.Length;}else if(owner.cleaning)owner.Report.cleanupShapeChecks++;else throw new InvalidOperationException("Unexpected zero-native-force graph recomposition.");}return code;
        }
        static void Reset(Window w)
        {
            w.sequence=-1;w.pair=-1;w.frame=0;w.rowCount=0;w.fixedTimeSeconds=0;w.treatment=null;w.captured=false;w.firstInvocation=false;w.firstCandidateInvocation=false;w.originalReturned=false;w.topologyIntact=false;w.unsupportedZeroCalls=0;
            w.startedTicks=w.endedTicks=w.completeTicks=w.forceCalls=w.torqueCalls=w.zeroForceCalls=w.zeroTorqueCalls=w.forwardedForceCalls=w.forwardedTorqueCalls=w.skippedForceCalls=w.skippedTorqueCalls=w.bodyGuardNativeReads=w.receiverGuardChecks=0;
        }
        static void Before(FlightIntegrator __instance,Part part,out int __state)
        {
            __state=0;var o=active;if(o==null||o.disabled)return;
            try
            {
                if(o.current==null)
                {
                    if(part!=o.vessel.rootPart||__instance.Vessel!=o.vessel)return;long start=Stopwatch.GetTimestamp();bool captured=o.capturing();
                    if(captured&&!o.ReadyForCapture) {o.Report.coldCaptureRoots++;throw new InvalidOperationException("Cold admission: both required real-root branches must finish before capture.");}
                    Need(Thread.CurrentThread.ManagedThreadId==o.thread&&__instance.GetType()==typeof(FlightIntegrator)&&o.TopologyIntact()&&!FlightDriver.Pause&&TimeWarp.CurrentRate==1&&Time.timeScale==1,"Original root owner/epoch/time domain changed.");
                    bool compare=captured&&o.Report.mode=="compare"&&o.comparisonCount<Comparisons;var w=compare?o.comparisons[o.comparisonCount]:o.scratch;Reset(w);w.startedTicks=start;w.captured=captured;w.frame=Time.frameCount;w.fixedTimeSeconds=Time.fixedTime;w.firstInvocation=o.first;
                    bool candidate=o.Report.mode=="query"||(o.Report.mode=="compare"&&(captured?(o.comparisonCount>=Comparisons||o.comparisonCount==1||o.comparisonCount==2):(o.ReadyForCapture||(o.Report.warmupOriginalRoots+o.Report.warmupCandidateRoots)%2==1)));w.treatment=candidate?"candidate":"original";w.firstCandidateInvocation=candidate&&o.firstCandidate;
                    if(compare) {w.sequence=o.comparisonCount;w.pair=o.comparisonCount/2;}o.current=w;o.currentIntegrator=__instance;o.rows=0;o.depth=0;
                }
                Need(Thread.CurrentThread.ManagedThreadId==o.thread&&ReferenceEquals(__instance,o.currentIntegrator)&&o.depth<MaxDepth&&o.rows<o.preorder.Length&&ReferenceEquals(part,o.preorder[o.rows]),"Integrate order/stack changed.");o.stack[o.depth++]=o.preorderSlots[o.rows++];__state=o.depth;
            }
            catch(Exception error) {o.Fault(error);}
        }
        static Exception After(Exception __exception,int __state)
        {
            var o=active;if(o==null||__state==0)return __exception;
            try
            {
                Need(o.current!=null&&o.depth==__state,"Integrate finalizer stack drift.");if(__exception!=null)o.Report.originalExceptions++;o.depth--;if(o.depth!=0)return __exception;var w=o.current;w.originalReturned=__exception==null;w.rowCount=o.rows;w.topologyIntact=o.TopologyIntact();
                o.Report.allRoots++;
                if(w.captured) {o.Report.captureRoots++;if(w.treatment=="candidate")o.Report.captureCandidateRoots++;o.Report.captureForceCalls+=w.forceCalls;o.Report.captureTorqueCalls+=w.torqueCalls;o.Report.captureZeroForceCalls+=w.zeroForceCalls;o.Report.captureZeroTorqueCalls+=w.zeroTorqueCalls;o.Report.captureForwardedForceCalls+=w.forwardedForceCalls;o.Report.captureForwardedTorqueCalls+=w.forwardedTorqueCalls;o.Report.captureSkippedForceCalls+=w.skippedForceCalls;o.Report.captureSkippedTorqueCalls+=w.skippedTorqueCalls;o.Report.captureReceiverGuardChecks+=w.receiverGuardChecks;}
                else if(w.treatment=="candidate") {o.Report.warmupCandidateRoots++;o.Report.warmupCandidateSkippedForceCalls+=w.skippedForceCalls;o.Report.warmupCandidateSkippedTorqueCalls+=w.skippedTorqueCalls;}
                else {o.Report.warmupOriginalRoots++;o.Report.warmupOriginalForwardedForceCalls+=w.forwardedForceCalls;o.Report.warmupOriginalForwardedTorqueCalls+=w.forwardedTorqueCalls;}
                o.Report.forceCalls+=w.forceCalls;o.Report.torqueCalls+=w.torqueCalls;o.Report.zeroForceCalls+=w.zeroForceCalls;o.Report.zeroTorqueCalls+=w.zeroTorqueCalls;o.Report.forwardedForceCalls+=w.forwardedForceCalls;o.Report.forwardedTorqueCalls+=w.forwardedTorqueCalls;o.Report.skippedForceCalls+=w.skippedForceCalls;o.Report.skippedTorqueCalls+=w.skippedTorqueCalls;o.Report.bodyGuardNativeReads+=w.bodyGuardNativeReads;o.Report.receiverGuardChecks+=w.receiverGuardChecks;o.Report.unsupportedZeroCalls+=w.unsupportedZeroCalls;
                if(w.firstInvocation) {Copy(w,o.Report.firstInvocation);o.first=false;}if(w.firstCandidateInvocation) {Copy(w,o.Report.firstCandidateInvocation);o.firstCandidate=false;}
                if(w.sequence>=0) {o.comparisonCount++;if(w.treatment=="candidate")o.Report.candidateComparisonRoots++;else o.Report.controlComparisonRoots++;if(w.originalReturned&&w.topologyIntact&&w.rowCount==o.preorder.Length)o.Report.completedComparisons++;}
                Need(w.originalReturned&&w.topologyIntact&&w.rowCount==o.preorder.Length,"Original root return/epoch/traversal incomplete.");w.endedTicks=Stopwatch.GetTimestamp();w.completeTicks=w.endedTicks-w.startedTicks;
                if(w.firstInvocation) {o.Report.firstInvocation.endedTicks=w.endedTicks;o.Report.firstInvocation.completeTicks=w.completeTicks;}
                if(w.firstCandidateInvocation) {o.Report.firstCandidateInvocation.endedTicks=w.endedTicks;o.Report.firstCandidateInvocation.completeTicks=w.completeTicks;}
                if(!w.captured) {if(w.treatment=="candidate")o.Report.warmupCandidateCompleteTicks+=w.completeTicks;else o.Report.warmupOriginalCompleteTicks+=w.completeTicks;}
                if(w.captured&&w.sequence<0&&w.treatment=="candidate")o.Report.queryCompleteTicks+=w.completeTicks;o.current=null;o.currentIntegrator=null;
            }
            catch(Exception error) {o.Fault(error);}
            return __exception;
        }
        static void Copy(Window a,Window b)
        {
            b.sequence=a.sequence;b.pair=a.pair;b.frame=a.frame;b.rowCount=a.rowCount;b.fixedTimeSeconds=a.fixedTimeSeconds;b.treatment=a.treatment;b.captured=a.captured;b.firstInvocation=a.firstInvocation;b.firstCandidateInvocation=a.firstCandidateInvocation;b.originalReturned=a.originalReturned;b.topologyIntact=a.topologyIntact;b.unsupportedZeroCalls=a.unsupportedZeroCalls;
            b.startedTicks=a.startedTicks;b.endedTicks=a.endedTicks;b.completeTicks=a.completeTicks;b.forceCalls=a.forceCalls;b.torqueCalls=a.torqueCalls;b.zeroForceCalls=a.zeroForceCalls;b.zeroTorqueCalls=a.zeroTorqueCalls;b.forwardedForceCalls=a.forwardedForceCalls;b.forwardedTorqueCalls=a.forwardedTorqueCalls;b.skippedForceCalls=a.skippedForceCalls;b.skippedTorqueCalls=a.skippedTorqueCalls;b.bodyGuardNativeReads=a.bodyGuardNativeReads;b.receiverGuardChecks=a.receiverGuardChecks;
        }
        bool BodyEligible(Rigidbody body)
        {
            current.receiverGuardChecks++;return ReferenceEquals(body,ownBodies[stack[depth-1]])&&body!=null;
        }
        static bool Omit(Rigidbody body,Vector3 value,bool torque)
        {
            var o=active;if(o?.current==null||o.disabled)return false;var w=o.current;
            if(torque)w.torqueCalls++;else w.forceCalls++;bool zero=NativeForceZeroPredicate.IsZero(value.x,value.y,value.z);if(zero) {if(torque)w.zeroTorqueCalls++;else w.zeroForceCalls++;}
            bool skip=false;try {if(zero&&w.treatment=="candidate") {skip=o.BodyEligible(body);if(!skip)w.unsupportedZeroCalls++;}}catch(Exception error) {o.Fault(error);}
            if(skip) {if(torque)w.skippedTorqueCalls++;else w.skippedForceCalls++;}else {if(torque)w.forwardedTorqueCalls++;else w.forwardedForceCalls++;}return skip;
        }
        static void ForceDefault(Rigidbody body,Vector3 force) {if(!Omit(body,force,false))body.AddForce(force);}
        static void TorqueDefault(Rigidbody body,Vector3 torque) {if(!Omit(body,torque,true))body.AddTorque(torque);}
        void Fault(Exception error) {disabled=true;Report.errors++;Report.detail=error.ToString();}
        static void Need(bool value,string reason) {if(!value)throw new InvalidOperationException(reason);}
        static string HashFile(string path) {using(var stream=File.OpenRead(path))using(var hash=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        void Remove()
        {
            long start=Stopwatch.GetTimestamp();bool gone=false;cleaning=true;
            try {harmony.UnpatchAll(Owner);gone=Harmony.GetPatchInfo(Integrate)?.Owners.Contains(Owner)!=true;Report.graphReleased=gone&&nativeGraph.All(m=>Harmony.GetPatchInfo(m)?.Owners.Any()!=true);Report.cleanupStatus=Report.graphReleased?"removed-owned-patches":"cleanup-error";if(!Report.graphReleased)Report.errors++;}
            catch(Exception error) {Fault(error);Report.cleanupStatus="cleanup-error";}
            finally {if(gone&&active==this)active=null;cleaning=false;Report.cleanupTicks=Stopwatch.GetTimestamp()-start;}
        }
        public void Dispose()
        {
            if(disposed)return;if(Thread.CurrentThread.ManagedThreadId!=thread) {Fault(new InvalidOperationException("Main-thread cleanup retry required."));throw new InvalidOperationException(Report.detail);}disposed=true;
            try {Need(current==null,"Root remains open at cleanup.");if(Report.graphInstalled)Need(ExactGraph()&&TopologyIntact(),"Frozen graph/epoch changed before cleanup.");}catch(Exception error) {Fault(error);}finally {Remove();}
            Report.windows=new Window[comparisonCount];Array.Copy(comparisons,Report.windows,comparisonCount);Report.status=Report.errors==0&&Report.originalExceptions==0&&Report.unsupportedZeroCalls==0&&Report.captureCandidateRoots>0&&(Report.captureSkippedForceCalls+Report.captureSkippedTorqueCalls)>0?"observed":"incomplete";
        }
        public bool IsValidFinal(out string reason)
        {
            reason=null;if(!disposed||Report.status!="observed"||Report.cleanupStatus!="removed-owned-patches"||!Report.nativeQualified||Report.nativeQualificationStatus!="qualified-isolated-zero-command"||!ReadyForCapture||Report.coldCaptureRoots!=0||!Report.graphAdmissions||!Report.graphInstalled||!Report.graphReleased||Report.shapeAdmissions!=1||Report.patchedMethods!=1||Report.staticPlainForceSites!=1||Report.staticPlainTorqueSites!=1||Report.staticUnchangedAccelerationSites!=2||Report.staticUnchangedPointSites!=1||Report.errors!=0||Report.originalExceptions!=0||Report.unsupportedZeroCalls!=0||Report.bodyGuardNativeReads!=0) {reason="Zero-native-force native qualification/warmup/capture/ownership lifecycle incomplete.";return false;}
            if(Report.mode=="compare"&&(comparisonCount!=Comparisons||Report.completedComparisons!=Comparisons||Report.controlComparisonRoots!=2||Report.candidateComparisonRoots!=2)||Report.mode=="query"&&comparisonCount!=0) {reason="Exactly two original/candidate pairs, or unpaired query coverage, required.";return false;}
            for(int i=0;i<Report.windows.Length;i++) {var w=Report.windows[i];if(w.sequence!=i||w.pair!=i/2||w.treatment!=(i==0||i==3?"original":"candidate")||!w.captured||!w.originalReturned||!w.topologyIntact||w.rowCount!=preorder.Length||w.completeTicks<=0||w.completeTicks!=w.endedTicks-w.startedTicks||w.forceCalls!=w.forwardedForceCalls+w.skippedForceCalls||w.torqueCalls!=w.forwardedTorqueCalls+w.skippedTorqueCalls||w.unsupportedZeroCalls!=0||w.treatment=="original"&&(w.skippedForceCalls+w.skippedTorqueCalls+w.bodyGuardNativeReads)!=0||w.treatment=="candidate"&&w.skippedForceCalls+w.skippedTorqueCalls==0) {reason="Comparison clocks/counters/treatment/coverage differ.";return false;}}return true;
        }
    }
}
