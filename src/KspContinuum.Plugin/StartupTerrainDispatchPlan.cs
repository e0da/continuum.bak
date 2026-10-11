using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class StartupTerrainDispatchPlan : MonoBehaviour
    {
        const string Owner="continuum.startup-terrain-dispatch-plan";
        static StartupTerrainDispatchPlan active;
        [ThreadStatic] static Plan current;
        static readonly FieldInfo Mods=AccessTools.Field(typeof(PQS),"mods"), Count=AccessTools.Field(typeof(PQS),"modCount"), Override=AccessTools.Field(typeof(PQSMod),"overrideQuadBuildCheck");
        static readonly MethodInfo Height=AccessTools.DeclaredMethod(typeof(PQSMod),"OnVertexBuildHeight",new[] {typeof(PQS.VertexBuildData)}), Vertex=AccessTools.DeclaredMethod(typeof(PQSMod),"OnVertexBuild",new[] {typeof(PQS.VertexBuildData)});
        readonly Dictionary<PQS,Plan> plans=new Dictionary<PQS,Plan>();
        Harmony harmony; bool installed,exported; Report report; int mainThread;
        MethodInfo build,heightMethod,vertexMethod;
        internal sealed class Plan
        {
            public PQS sphere; public PQSMod[] array,members; public Action<PQS,PQS.VertexBuildData,bool> height; public Action<PQS,PQS.VertexBuildData> vertex;
            public int heightCalls,vertexCalls,heightEmpty,vertexEmpty; public bool valid; public MethodInfo[] methods;
        }
        public struct Frame { internal Plan previous,plan; }
        [Serializable] public sealed class Report
        {
            public string schema="continuum-startup-terrain-dispatch-plan/v1",runId,status,cleanupStatus;
            public string proof="Only exact unpatched empty base callbacks omitted; ordered virtual calls retain live stock modifier instances and fields, height flag stores and original vertex method tails. Stock modifier-array mutation during a quad is unsupported and invalidates this experiment. No geometric equivalence claimed from counters.";
            public int processId; public long clockFrequency,plansBuilt,planBuildTicks,planAdmissionTicks,quads,quadsFallback,heightDispatches,vertexDispatches,realHeightCalls,realVertexCalls,emptyHeightCallsOmitted,emptyVertexCallsOmitted,errors; public bool hooksRemoved;
        }
        public void Awake()
        {
            if(Environment.GetEnvironmentVariable("CONTINUUM_STARTUP_TERRAIN_DISPATCH_PLAN")!="1") return;
            DontDestroyOnLoad(gameObject);
            report=new Report {processId=Process.GetCurrentProcess().Id,clockFrequency=Stopwatch.Frequency};
            try
            {
                report.runId=Environment.GetCommandLineArgs().Single(a=>a.StartsWith("--continuum-research-run=",StringComparison.Ordinal)).Substring("--continuum-research-run=".Length);
                if(active!=null||!File.Exists(Path.Combine(KSPUtil.ApplicationRootPath,"continuum-lab.local.json"))) throw new InvalidOperationException("One private laboratory owner required.");
                StartupPinnedGameIdentity.Require(typeof(PQS).Assembly);
                if(Mods==null||Mods.FieldType!=typeof(PQSMod[])||Count==null||Count.FieldType!=typeof(int)||Override==null||Override.FieldType!=typeof(bool)||!Empty(Height)||!Empty(Vertex)) throw new InvalidOperationException("Stock dispatch fields/base IL differ.");
                build=AccessTools.DeclaredMethod(typeof(PQS),"BuildQuad",new[] {typeof(PQ)});
                heightMethod=AccessTools.DeclaredMethod(typeof(PQS),"Mod_OnVertexBuildHeight",new[] {typeof(PQS.VertexBuildData),typeof(bool)});
                vertexMethod=AccessTools.DeclaredMethod(typeof(PQS),"Mod_OnVertexBuild",new[] {typeof(PQS.VertexBuildData)});
                foreach(var method in new[] {heightMethod,vertexMethod}) if(method==null||Harmony.GetPatchInfo(method)?.Owners.Count>0) throw new InvalidOperationException("Vertex seam already patched or missing.");
                if(build==null||build.ReturnType!=typeof(bool)||!BuildOwnersSupported(build)) throw new InvalidOperationException("Quad seam already modified or missing.");
                active=this; mainThread=System.Threading.Thread.CurrentThread.ManagedThreadId; harmony=new Harmony(Owner);
                harmony.Patch(heightMethod,transpiler:new HarmonyMethod(typeof(StartupTerrainDispatchPlan),nameof(Transpile)));
                harmony.Patch(vertexMethod,transpiler:new HarmonyMethod(typeof(StartupTerrainDispatchPlan),nameof(Transpile)));
                harmony.Patch(build,prefix:new HarmonyMethod(typeof(StartupTerrainDispatchPlan),nameof(Begin)),finalizer:new HarmonyMethod(typeof(StartupTerrainDispatchPlan),nameof(End)));
                installed=true; UnityEngine.Debug.Log("[Continuum terrain dispatch] status=installed proof=exact-empty-base-IL-and-original-ordered-live-virtual-calls");
            }
            catch(Exception error) {report.errors++; UnityEngine.Debug.LogError("[Continuum terrain dispatch] rejected "+error); Export("rejected"); Application.Quit(2);}
        }
        internal static bool BuildOwnersSupported(MethodInfo method)
        {
            var p=Harmony.GetPatchInfo(method); if(p==null||p.Owners.Count==0) return true;
            return p.Owners.Count==1&&p.Owners[0]=="continuum.startup-terrain-build-timing"&&p.Prefixes.Count==1&&p.Finalizers.Count==1&&p.Transpilers.Count==1&&p.Postfixes.Count==0&&p.Prefixes[0].PatchMethod.DeclaringType==typeof(StartupTerrainBuildTiming)&&p.Finalizers[0].PatchMethod.DeclaringType==typeof(StartupTerrainBuildTiming)&&p.Transpilers[0].PatchMethod.DeclaringType==typeof(StartupTerrainBuildTiming);
        }
        internal static bool IsOwnedBuildObserver(MethodInfo method)
        {
            var p=Harmony.GetPatchInfo(method);
            return p!=null&&p.Owners.Count==1&&p.Owners[0]==Owner&&p.Prefixes.Count==1&&p.Finalizers.Count==1&&p.Transpilers.Count==0&&p.Postfixes.Count==0&&p.Prefixes[0].PatchMethod.DeclaringType==typeof(StartupTerrainDispatchPlan)&&p.Finalizers[0].PatchMethod.DeclaringType==typeof(StartupTerrainDispatchPlan);
        }
        static bool Empty(MethodInfo method)
        {return method!=null&&method.DeclaringType==typeof(PQSMod)&&method.ReturnType==typeof(void)&&!method.IsStatic&&method.IsVirtual&&Unpatched(method)&&method.GetMethodBody().GetILAsByteArray().SequenceEqual(new byte[] {0x2a});}
        static bool Unpatched(MethodInfo method) {return Harmony.GetPatchInfo(method)?.Owners.Count>0?false:true;}
        static bool Same(Plan p)
        {
            if(!ReferenceEquals(Mods.GetValue(p.sphere),p.array)||(int)Count.GetValue(p.sphere)!=p.members.Length) return false;
            for(int i=0;i<p.members.Length;i++) if(!ReferenceEquals(p.array[i],p.members[i])) return false;
            return true;
        }
        Plan Prepare(PQS sphere)
        {
            if(!Unpatched(Height)||!Unpatched(Vertex)) return null;
            Plan prior; if(plans.TryGetValue(sphere,out prior)&&prior.valid&&Same(prior)&&prior.methods.All(Unpatched)) return prior;
            long start=Stopwatch.GetTimestamp();
            try
            {
                var array=(PQSMod[])Mods.GetValue(sphere); int count=(int)Count.GetValue(sphere);
                if(array==null||count!=array.Length) return null;
                var p=new Plan {sphere=sphere,array=array,members=(PQSMod[])array.Clone()}; var heightReal=new bool[count]; var vertexReal=new bool[count];var methods=new HashSet<MethodInfo>();
                for(int i=0;i<count;i++)
                {
                    if(array[i]==null||array[i].GetType().Assembly!=typeof(PQS).Assembly) return null;
                    var h=array[i].GetType().GetMethod(Height.Name,new[] {typeof(PQS.VertexBuildData)}); var v=array[i].GetType().GetMethod(Vertex.Name,new[] {typeof(PQS.VertexBuildData)});
                    if(h==null||v==null||!Unpatched(h)||!Unpatched(v)||!Equals(h.GetBaseDefinition(),Height)||!Equals(v.GetBaseDefinition(),Vertex)) return null;
                    heightReal[i]=!Equals(h,Height); vertexReal[i]=!Equals(v,Vertex);
                    methods.Add(h);methods.Add(v);
                    if(heightReal[i]) p.heightCalls++; else p.heightEmpty++;
                    if(vertexReal[i]) p.vertexCalls++; else p.vertexEmpty++;
                }
                p.height=(Action<PQS,PQS.VertexBuildData,bool>)Compile(heightReal,true).CreateDelegate(typeof(Action<PQS,PQS.VertexBuildData,bool>));
                p.vertex=(Action<PQS,PQS.VertexBuildData>)Compile(vertexReal,false).CreateDelegate(typeof(Action<PQS,PQS.VertexBuildData>));
                p.methods=methods.ToArray();p.valid=true; plans[sphere]=p; report.plansBuilt++; return p;
            }
            finally {report.planBuildTicks+=Stopwatch.GetTimestamp()-start;}
        }
        static DynamicMethod Compile(bool[] real,bool height)
        {
            var method=new DynamicMethod("ContinuumPreparedTerrainDispatch",typeof(void),height?new[] {typeof(PQS),typeof(PQS.VertexBuildData),typeof(bool)}:new[] {typeof(PQS),typeof(PQS.VertexBuildData)},typeof(PQS),true); var il=method.GetILGenerator();
            for(int i=0;i<real.Length;i++)
            {
                if(height) {LoadMod(il,i); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Stfld,Override);}
                if(real[i]) {LoadMod(il,i); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt,height?Height:Vertex);}
                if(height) {LoadMod(il,i); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld,Override);}
            }
            il.Emit(OpCodes.Ret); return method;
        }
        static void LoadMod(ILGenerator il,int index) {il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld,Mods); il.Emit(OpCodes.Ldc_I4,index); il.Emit(OpCodes.Ldelem_Ref);}
        static void Begin(PQS __instance,out Frame __state)
        {
            __state=new Frame {previous=current}; current=null; if(active==null||!active.installed) return;
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=active.mainThread) {active.report.quadsFallback++;return;}
            long start=Stopwatch.GetTimestamp();
            try {__state.plan=active.Prepare(__instance);current=__state.plan;if(current==null)active.report.quadsFallback++;else active.report.quads++;}
            catch(Exception error) {active.report.errors++;active.report.quadsFallback++;UnityEngine.Debug.LogError("[Continuum terrain dispatch] plan fallback "+error.Message);}
            finally {active.report.planAdmissionTicks+=Stopwatch.GetTimestamp()-start;}
        }
        static Exception End(Frame __state,Exception __exception)
        {
            try {if(active!=null&&__exception!=null)active.report.errors++;if(active!=null&&__state.plan!=null&&!Same(__state.plan)) {__state.plan.valid=false;active.report.errors++;UnityEngine.Debug.LogError("[Continuum terrain dispatch] invalid modifier-array-changed-during-quad");}}
            catch(Exception error) {if(active!=null)active.report.errors++;UnityEngine.Debug.LogError("[Continuum terrain dispatch] final observation failed "+error.Message);}
            finally {current=__state.previous;} return __exception;
        }
        public static bool TryHeight(PQS sphere,PQS.VertexBuildData data,bool flag)
        {
            var p=current;if(p==null||!p.valid||!ReferenceEquals(p.sphere,sphere))return false;
            active.report.heightDispatches++;active.report.realHeightCalls+=p.heightCalls;active.report.emptyHeightCallsOmitted+=p.heightEmpty;p.height(sphere,data,flag);return true;
        }
        public static bool TryVertex(PQS sphere,PQS.VertexBuildData data)
        {
            var p=current;if(p==null||!p.valid||!ReferenceEquals(p.sphere,sphere))return false;
            active.report.vertexDispatches++;active.report.realVertexCalls+=p.vertexCalls;active.report.emptyVertexCallsOmitted+=p.vertexEmpty;p.vertex(sphere,data);return true;
        }
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> input,ILGenerator generator,MethodBase __originalMethod)
        {
            var body=input.Select(x=>new CodeInstruction(x)).ToList(); bool height=__originalMethod.GetParameters().Length==2; var target=height?Height:Vertex;
            int call=body.FindIndex(x=>x.Calls(target)); if(call<0||body.Count(x=>x.Calls(target))!=1) throw new InvalidOperationException("Exact one stock virtual callback required.");
            int end=body.FindIndex(call+1,x=>x.opcode==OpCodes.Blt||x.opcode==OpCodes.Blt_S);
            int start=-1;for(int i=call-1;i>=0;i--) if(body[i].opcode==OpCodes.Ldc_I4_0&&i+2<body.Count&&body[i+1].opcode.Name.StartsWith("stloc",StringComparison.Ordinal)&&(body[i+2].opcode==OpCodes.Br||body[i+2].opcode==OpCodes.Br_S)){start=i;break;}
            if(start<0||end<call||end+1>=body.Count||body.Skip(start).Take(end-start+1).Any(x=>x.blocks.Count!=0)||body[end].operand==null) throw new InvalidOperationException("Stock loop shape differs.");
            var range=body.Skip(start).Take(end-start+1).ToArray();
            if(range.Count(x=>Equals(x.operand,Count))!=1||range.Count(x=>Equals(x.operand,Mods))!=(height?3:1)||range.Count(x=>Equals(x.operand,Override))!=(height?2:0)) throw new InvalidOperationException("Stock loop fields differ.");
            Label after=generator.DefineLabel();body[end+1].labels.Add(after);
            var entry=new List<CodeInstruction> {new CodeInstruction(OpCodes.Ldarg_0),new CodeInstruction(OpCodes.Ldarg_1)};entry[0].labels.AddRange(body[start].labels);body[start].labels.Clear();if(height)entry.Add(new CodeInstruction(OpCodes.Ldarg_2));entry.Add(new CodeInstruction(OpCodes.Call,AccessTools.DeclaredMethod(typeof(StartupTerrainDispatchPlan),height?nameof(TryHeight):nameof(TryVertex))));entry.Add(new CodeInstruction(OpCodes.Brtrue,after));body.InsertRange(start,entry);return body;
        }
        public void Update() {if(HighLogic.LoadedScene==GameScenes.MAINMENU) ObserveForMenu();}
        public static void ObserveForMenu() {if(active!=null)active.Export("observed");}
        void Export(string status)
        {
            if(exported)return;exported=true;installed=false;
            try {harmony?.UnpatchAll(Owner);report.hooksRemoved=new[] {build,heightMethod,vertexMethod}.Where(x=>x!=null).All(x=>Harmony.GetPatchInfo(x)?.Owners.Contains(Owner)!=true);}
            catch(Exception error){report.errors++;UnityEngine.Debug.LogError("[Continuum terrain dispatch] cleanup "+error);}
            report.cleanupStatus=report.hooksRemoved?"removed-owned-hooks":"owned-hooks-remain";report.status=report.errors==0&&report.hooksRemoved?status:"invalid";
            string dir=Path.Combine(KSPUtil.ApplicationRootPath,"GameData","KspContinuum","PluginData");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"startup-terrain-dispatch-"+report.processId+".json"),ReportJson.Encode(report));
            UnityEngine.Debug.Log("[Continuum terrain dispatch] status="+report.status+" quads="+report.quads+" fallback="+report.quadsFallback+" heightOmitted="+report.emptyHeightCallsOmitted+" vertexOmitted="+report.emptyVertexCallsOmitted+" errors="+report.errors);plans.Clear();if(ReferenceEquals(active,this))active=null;
        }
        public void OnApplicationQuit(){if(report!=null)Export("interrupted");}
        public void OnDestroy(){if(report!=null)Export("interrupted");}
    }
}
