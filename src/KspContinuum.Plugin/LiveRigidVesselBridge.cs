using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    public sealed class LiveRigidVesselBridge : IDisposable
    {
        const string PatchOwner="continuum.live-rigid-vessel-bridge";
        const string StockSha="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const string MechSha="6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929";
        const int MaxMethods=100000, MaxSelected=8192, MaxInstructions=5000000;
        static LiveRigidVesselBridge active;
        static readonly MethodInfo Integrate=AccessTools.DeclaredMethod(typeof(FlightIntegrator),"Integrate",new[]{typeof(Part)});
        static readonly MethodInfo Transpiler=AccessTools.DeclaredMethod(typeof(LiveRigidVesselBridge),"Transform");
        static readonly Dictionary<MethodInfo,MethodInfo> Routes=MakeRoutes();
        readonly Harmony harmony=new Harmony(PatchOwner);
        readonly int thread=Thread.CurrentThread.ManagedThreadId;
        readonly Dictionary<MethodBase,Plan> plans=new Dictionary<MethodBase,Plan>();
        readonly Dictionary<MethodBase,FrameHooks> frames=new Dictionary<MethodBase,FrameHooks>();
        readonly List<MethodBase> attempted=new List<MethodBase>();
        bool installing,cleaning,disposed;
        public bool Installed {get;private set;}
        public int PatchedMethods {get;private set;}
        public int PatchedSites {get;private set;}
        public int ScannedMethods {get;private set;}
        public int ExcludedGenericMethods {get;private set;}
        public string CleanupStatus {get;private set;}="not-installed";
        public string[] CleanupErrors {get;private set;}=new string[0];
        public CallsiteRow[] SourceCallsites {get;private set;}=new CallsiteRow[0];
        public static string LastFailureCleanupStatus {get;private set;}
        [Serializable] public sealed class CallsiteRow
        {
            public string assembly,assemblySha256,assemblyMvid,declaringType,method,ilSha256;
            public int metadataToken,instructionCount,expectedReplacements,observedReplacements,shapeAdmissions,cleanupShapeChecks,frameRecompositions;
            public string[] replacedCalls,unroutedCalls;
            public bool patched,removed;
        }
        sealed class Plan {internal MethodBase method;internal CallsiteRow row;internal int expected;internal string il;internal string[] calls;}
        sealed class FrameHooks {internal string owner;internal MethodInfo prefix,finalizer;}
        static void Need(bool pass,string why) {if(!pass)throw new InvalidOperationException(why);}
        static string Hash(byte[] bytes) {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static string FileHash(string path) {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        static bool Same(MethodBase a,MethodBase b) {return a!=null&&b!=null&&a.Module.ModuleVersionId==b.Module.ModuleVersionId&&a.MetadataToken==b.MetadataToken&&a.DeclaringType==b.DeclaringType;}
        static Dictionary<MethodInfo,MethodInfo> MakeRoutes()
        {
            var result=new Dictionary<MethodInfo,MethodInfo>();
            Add(result,"get_velocity","Velocity",Type.EmptyTypes);Add(result,"get_angularVelocity","AngularVelocity",Type.EmptyTypes);
            Add(result,"get_worldCenterOfMass","WorldCenter",Type.EmptyTypes);Add(result,"GetPointVelocity","PointVelocity",new[]{typeof(Vector3)});
            Add(result,"get_isKinematic","IsKinematic",Type.EmptyTypes);Add(result,"set_velocity","SetVelocity",new[]{typeof(Vector3)});Add(result,"set_angularVelocity","SetAngularVelocity",new[]{typeof(Vector3)});
            Add(result,"AddForce","Force",new[]{typeof(Vector3)});Add(result,"AddForce","ForceModeCall",new[]{typeof(Vector3),typeof(ForceMode)});
            Add(result,"AddTorque","Torque",new[]{typeof(Vector3)});Add(result,"AddTorque","TorqueModeCall",new[]{typeof(Vector3),typeof(ForceMode)});
            Add(result,"AddForceAtPosition","PointForce",new[]{typeof(Vector3),typeof(Vector3)});Add(result,"AddForceAtPosition","PointForceMode",new[]{typeof(Vector3),typeof(Vector3),typeof(ForceMode)});
            return result;
        }
        static void Add(Dictionary<MethodInfo,MethodInfo> routes,string api,string wrapper,Type[] args)
        {
            MethodInfo original=AccessTools.DeclaredMethod(typeof(Rigidbody),api,args), replacement=AccessTools.DeclaredMethod(typeof(LiveRigidVesselBridge),wrapper);
            Need(original!=null&&!original.IsStatic&&replacement!=null&&replacement.IsStatic,"Live rigid API/wrapper missing: "+api);
            Need(replacement.ReturnType==original.ReturnType&&replacement.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(Rigidbody)}.Concat(args)),"Live rigid wrapper ABI differs: "+api);
            routes.Add(original,replacement);
        }
        static MethodInfo Route(MethodBase caller,MethodInfo called)
        {
            if(called==null)return null;
            MethodInfo wrapper=null;foreach(var pair in Routes)if(Same(pair.Key,called)){wrapper=pair.Value;break;}
            if(wrapper!=null&&wrapper.Name=="ForceModeCall"&&Same(caller,Integrate))return AccessTools.DeclaredMethod(typeof(LiveRigidVesselBridge),"IntegrationAcceleration");
            return wrapper;
        }
        static bool OtherWrite(MethodInfo called)
        {
            if(called==null||called.DeclaringType!=typeof(Rigidbody))return false;
            return called.Name.StartsWith("set_",StringComparison.Ordinal)||called.Name.StartsWith("Add",StringComparison.Ordinal)||called.Name=="MovePosition"||called.Name=="MoveRotation"||called.Name=="Sleep"||called.Name=="WakeUp"||called.Name.StartsWith("Reset",StringComparison.Ordinal);
        }
        public LiveRigidVesselBridge()
        {
            Need(active==null,"Live rigid callsite bridge already owned.");active=this;
            try
            {
                var assemblies=new List<Assembly>{typeof(FlightIntegrator).Assembly};
                Assembly[] mods=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="MechJeb2").ToArray();Need(mods.Length<=1,"Multiple loaded MechJeb2 assemblies.");assemblies.AddRange(mods);
                var rows=new List<CallsiteRow>();int instructions=0;
                foreach(Assembly assembly in assemblies)
                {
                    bool stock=assembly==typeof(FlightIntegrator).Assembly;string sha=FileHash(assembly.Location);
                    Need(sha==(stock?StockSha:MechSha)&&assembly.ManifestModule.ModuleVersionId==new Guid(stock?"10657063-2fc3-43a7-84fa-d39e75e877bf":"30fbe3c4-e77e-4abc-895b-a635d5924a08"),"Live rigid original assembly SHA/MVID differs: "+assembly.GetName().Name);
                    foreach(Type type in assembly.GetTypes().OrderBy(t=>t.FullName,StringComparer.Ordinal))
                    foreach(MethodBase method in type.GetMethods(BindingFlags.DeclaredOnly|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.DeclaredOnly|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static)).OrderBy(m=>m.MetadataToken))
                    {
                        if(method.IsAbstract||method.ContainsGenericParameters||type.ContainsGenericParameters){ExcludedGenericMethods++;continue;}
                        MethodBody body=method.GetMethodBody();if(body==null)continue;Need(++ScannedMethods<=MaxMethods,"Live rigid scan method bound exceeded.");
                        var code=PatchProcessor.ReadMethodBody(method).ToArray();instructions=checked(instructions+code.Length);Need(instructions<=MaxInstructions,"Live rigid scan instruction bound exceeded.");
                        var calls=new List<string>();var others=new List<string>();int accelerations=0;
                        foreach(var c in code)
                        {
                            var called=c.Value as MethodInfo;if(called==null)continue;
                            if(c.Key==OpCodes.Call||c.Key==OpCodes.Callvirt)
                            {var route=Route(method,called);if(route!=null){calls.Add(called+" -> "+route.Name);if(route.Name=="IntegrationAcceleration")accelerations++;}else if(OtherWrite(called))others.Add(called.ToString());}
                            else if((c.Key==OpCodes.Ldftn||c.Key==OpCodes.Ldvirtftn)&&called.DeclaringType==typeof(Rigidbody))others.Add("indirect-function-pointer:"+called);
                        }
                        if(Same(method,Integrate))Need(accelerations==2&&calls.Count==5&&calls.Count(x=>x.EndsWith(" -> Force",StringComparison.Ordinal))==1&&calls.Count(x=>x.EndsWith(" -> Torque",StringComparison.Ordinal))==1&&calls.Count(x=>x.EndsWith(" -> PointForce",StringComparison.Ordinal))==1,"Pinned Integrate own/servo acceleration and queued-force callsite shape differs.");
                        if(calls.Count==0&&others.Count==0)continue;
                        var row=new CallsiteRow {assembly=assembly.GetName().Name,assemblySha256=sha,assemblyMvid=method.Module.ModuleVersionId.ToString("D"),declaringType=type.FullName,method=method.Name,metadataToken=method.MetadataToken,ilSha256=Hash(body.GetILAsByteArray()),instructionCount=code.Length,expectedReplacements=calls.Count,replacedCalls=calls.ToArray(),unroutedCalls=others.ToArray()};rows.Add(row);
                        if(calls.Count>0){Need(plans.Count<MaxSelected,"Live rigid selected method bound exceeded.");Need(Harmony.GetPatchInfo(method)?.Owners.Any()!=true,"Live rigid callsite already patched: "+method);plans.Add(method,new Plan {method=method,row=row,expected=calls.Count,il=row.ilSha256,calls=row.replacedCalls});}
                    }
                    Need(FileHash(assembly.Location)==sha,"Live rigid original assembly file changed during scan.");
                }
                SourceCallsites=rows.ToArray();Need(plans.ContainsKey(Integrate)&&plans.Count>0,"Pinned Integrate was not selected.");
                foreach(MethodInfo api in Routes.Keys)Need(Harmony.GetPatchInfo(api)?.Owners.Any()!=true,"Live rigid native facade already hooked: "+api);
                installing=true;
                foreach(Plan plan in plans.Values.OrderBy(p=>p.method.Module.ModuleVersionId).ThenBy(p=>p.method.MetadataToken))
                {
                    Need(Harmony.GetPatchInfo(plan.method)?.Owners.Any()!=true,"Live rigid caller owner appeared before install.");attempted.Add(plan.method);
                    harmony.Patch(plan.method,transpiler:new HarmonyMethod(Transpiler){priority=Priority.Normal});
                    Need(plan.row.shapeAdmissions==1&&Owned(plan.method),"Live rigid install owner/shape proof failed: "+plan.method);plan.row.patched=true;PatchedMethods++;PatchedSites+=plan.expected;
                }
                installing=false;Installed=true;CleanupStatus="installed";
            }
            catch(Exception error)
            {
                installing=false;Remove();LastFailureCleanupStatus=CleanupStatus;
                throw new InvalidOperationException("Live rigid bridge admission/install failed; cleanup="+CleanupStatus+": "+error.Message,error);
            }
        }
        bool Owned(MethodBase method)
        {
            var p=Harmony.GetPatchInfo(method);FrameHooks f;frames.TryGetValue(method,out f);
            return p!=null&&p.Owners.All(o=>o==PatchOwner||f!=null&&o==f.owner)&&p.Postfixes.Count==0&&p.Transpilers.Count==1&&p.Transpilers[0].owner==PatchOwner&&p.Transpilers[0].priority==Priority.Normal&&p.Transpilers[0].before.Length==0&&p.Transpilers[0].after.Length==0&&Same(p.Transpilers[0].PatchMethod,Transpiler)&&
                p.Prefixes.Count<=(f!=null&&f.prefix!=null?1:0)&&p.Finalizers.Count<=(f!=null&&f.finalizer!=null?1:0)&&
                p.Prefixes.All(x=>f!=null&&x.owner==f.owner&&Same(x.PatchMethod,f.prefix)&&x.priority==Priority.Normal&&x.before.Length==0&&x.after.Length==0)&&p.Finalizers.All(x=>f!=null&&x.owner==f.owner&&Same(x.PatchMethod,f.finalizer)&&x.priority==Priority.Normal&&x.before.Length==0&&x.after.Length==0);
        }
        public bool DeclareFrameHooks(MethodBase target,string ownerId,MethodInfo prefix,MethodInfo finalizer)
        {
            Need(Installed&&!disposed&&Thread.CurrentThread.ManagedThreadId==thread,"Frame hooks must be declared on installed bridge thread.");
            if(!plans.ContainsKey(target))return false;
            Need(!string.IsNullOrEmpty(ownerId)&&ownerId!=PatchOwner&&(prefix!=null||finalizer!=null)&&!frames.ContainsKey(target)&&Owned(target),"Invalid/repeated live rigid frame declaration.");
            foreach(MethodInfo method in new[]{prefix,finalizer})if(method!=null)Need(method.IsStatic&&method.DeclaringType==typeof(LiveRigidVesselOwner),"Frame hook must belong to the exact live rigid owner facade.");
            frames.Add(target,new FrameHooks {owner=ownerId,prefix=prefix,finalizer=finalizer});return true;
        }
        public bool ExactInstalled()
        {
            if(!Installed||disposed||!ReferenceEquals(active,this)||Thread.CurrentThread.ManagedThreadId!=thread)return false;
            foreach(MethodInfo api in Routes.Keys)if(Harmony.GetPatchInfo(api)?.Owners.Any()==true)return false;
            foreach(Plan plan in plans.Values)if(!Owned(plan.method)||Hash(plan.method.GetMethodBody().GetILAsByteArray())!=plan.il)return false;return true;
        }
        public bool HasExactFrameHooks(MethodBase target)
        {
            FrameHooks frame;
            if (!plans.ContainsKey(target) || !frames.TryGetValue(target, out frame) || !Owned(target)) return false;
            Patches graph = Harmony.GetPatchInfo(target);
            return graph.Prefixes.Count == (frame.prefix == null ? 0 : 1) && graph.Finalizers.Count == (frame.finalizer == null ? 0 : 1);
        }
        static IEnumerable<CodeInstruction> Transform(IEnumerable<CodeInstruction> input,MethodBase __originalMethod)
        {
            var owner=active;Plan plan=null;Need(owner!=null&&owner.plans.TryGetValue(__originalMethod,out plan),"Unknown live rigid transform owner/method.");
            Need(owner.installing||owner.cleaning||owner.frames.ContainsKey(__originalMethod)&&owner.Owned(__originalMethod),"Unexpected live rigid caller recomposition.");
            Need(Hash(__originalMethod.GetMethodBody().GetILAsByteArray())==plan.il,"Live rigid original caller IL changed.");
            var code=input.Select(c=>new CodeInstruction(c)).ToArray();var calls=new List<string>();
            foreach(CodeInstruction c in code)
            {
                if(c.opcode!=OpCodes.Call&&c.opcode!=OpCodes.Callvirt)continue;var called=c.operand as MethodInfo;var route=Route(__originalMethod,called);if(route==null)continue;
                calls.Add(called+" -> "+route.Name);c.opcode=OpCodes.Call;c.operand=route;
            }
            Need(calls.Count==plan.expected&&calls.SequenceEqual(plan.calls)&&code.Length==plan.row.instructionCount,"Live rigid exact callsite/instruction shape differs.");
            if(owner.installing){Need(plan.row.shapeAdmissions==0,"Live rigid duplicate install transform.");plan.row.shapeAdmissions++;plan.row.observedReplacements=calls.Count;}else if(owner.cleaning)plan.row.cleanupShapeChecks++;else plan.row.frameRecompositions++;
            return code;
        }
        void Remove()
        {
            cleaning=true;Installed=false;var faults=new List<string>();
            for(int n=attempted.Count-1;n>=0;n--)
            {
                MethodBase method=attempted[n];try {var info=Harmony.GetPatchInfo(method);if(info?.Owners.Contains(PatchOwner)==true&&!Owned(method))faults.Add("unexpected-cleanup-owner-shape:"+method);harmony.Unpatch(method,HarmonyPatchType.Transpiler,PatchOwner);}catch(Exception error){faults.Add(method+":"+error.GetType().Name+":"+error.Message);}
            }
            bool remaining=false;foreach(MethodBase method in attempted){bool present=Harmony.GetPatchInfo(method)?.Owners.Contains(PatchOwner)==true;remaining|=present;plans[method].row.removed=!present;}
            cleaning=false;disposed=!remaining;CleanupErrors=faults.ToArray();CleanupStatus=remaining?"hooks-remain":faults.Count>0?"removed-with-errors":"removed";
            if(!remaining&&ReferenceEquals(active,this))active=null;
        }
        public void Dispose()
        {
            if(disposed)return;Need(Thread.CurrentThread.ManagedThreadId==thread,"Live rigid cleanup must run on installation thread.");Remove();
        }
        static bool Find(Rigidbody body,out LiveRigidVesselOwner owner) {if(ReferenceEquals(body,null)){owner=null;return false;}return LiveRigidVesselOwner.TryGet(body,out owner);}
        static Vector3 Velocity(Rigidbody body) {LiveRigidVesselOwner owner;return Find(body,out owner)?owner.ReadVelocity(body):body.velocity;}
        static Vector3 AngularVelocity(Rigidbody body) {LiveRigidVesselOwner owner;return Find(body,out owner)?owner.ReadAngularVelocity(body):body.angularVelocity;}
        static Vector3 WorldCenter(Rigidbody body) {LiveRigidVesselOwner owner;return Find(body,out owner)?owner.ReadWorldCenter(body):body.worldCenterOfMass;}
        static Vector3 PointVelocity(Rigidbody body,Vector3 point) {LiveRigidVesselOwner owner;return Find(body,out owner)?owner.ReadPointVelocity(body,point):body.GetPointVelocity(point);}
        static bool IsKinematic(Rigidbody body) {LiveRigidVesselOwner owner;return Find(body,out owner)?false:body.isKinematic;}
        static void SetVelocity(Rigidbody body,Vector3 value) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.WriteVelocity(body,value);else body.velocity=value;}
        static void SetAngularVelocity(Rigidbody body,Vector3 value) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.WriteAngularVelocity(body,value);else body.angularVelocity=value;}
        static void Force(Rigidbody body,Vector3 value) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.Record(body,value,Vector3.zero,ForceMode.Force,false,false);else body.AddForce(value);}
        static void ForceModeCall(Rigidbody body,Vector3 value,ForceMode mode) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.Record(body,value,Vector3.zero,mode,false,false);else body.AddForce(value,mode);}
        static void IntegrationAcceleration(Rigidbody body,Vector3 value,ForceMode mode) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.RecordIntegrationAcceleration(body,value,mode);else body.AddForce(value,mode);}
        static void Torque(Rigidbody body,Vector3 value) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.Record(body,value,Vector3.zero,ForceMode.Force,true,false);else body.AddTorque(value);}
        static void TorqueModeCall(Rigidbody body,Vector3 value,ForceMode mode) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.Record(body,value,Vector3.zero,mode,true,false);else body.AddTorque(value,mode);}
        static void PointForce(Rigidbody body,Vector3 value,Vector3 point) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.Record(body,value,point,ForceMode.Force,false,true);else body.AddForceAtPosition(value,point);}
        static void PointForceMode(Rigidbody body,Vector3 value,Vector3 point,ForceMode mode) {LiveRigidVesselOwner owner;if(Find(body,out owner))owner.Record(body,value,point,mode,false,true);else body.AddForceAtPosition(value,point,mode);}
    }
}
