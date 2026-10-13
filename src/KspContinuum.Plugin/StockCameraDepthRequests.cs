using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal sealed class StockCameraDepthRequests : IDisposable
    {
        const string StockSha="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27";
        const string StockMvid="10657063-2fc3-43a7-84fa-d39e75e877bf";
        [Serializable] internal sealed class ReportData
        {
            public string schema="continuum-stock-camera-depth-requests/v1",assemblySha256,assemblyMvid,status="preparing",cleanupStatus,runId;
            public int processId,faults;
            public long clockFrequency=Stopwatch.Frequency;
            public bool hooksRemoved,exactGraphAtRelease;
            public Source[] sources=new Source[0];
            public Event[] events=new Event[0];
        }
        [Serializable] internal sealed class Source
        {
            public string declaringType,method,ilSha256;
            public int metadataToken,expectedSites,observedReplacements,shapeAdmissions;
        }
        [Serializable] internal struct Event
        {
            public int cameraId,before,requested,after,frame,sourceIndex,slot;
            public long ticks;
        }
        sealed class Plan {internal MethodInfo method;internal Source row;internal int index,instructions;internal int[] slots;}
        static StockCameraDepthRequests active;
        static readonly MethodInfo Setter=AccessTools.PropertySetter(typeof(Camera),"depthTextureMode");
        static readonly MethodInfo Transform=AccessTools.DeclaredMethod(typeof(StockCameraDepthRequests),nameof(Transpile));
        readonly Camera near,far;
        readonly Action<Camera,int,int> accept;
        readonly int thread=Thread.CurrentThread.ManagedThreadId;
        readonly string owner="continuum.stock-camera-depth."+Guid.NewGuid().ToString("N");
        readonly List<Plan> plans=new List<Plan>(2),attempted=new List<Plan>(2);
        readonly List<Event> events=new List<Event>(4096);
        Harmony harmony;
        bool installing,cleaning,installed,disposed;
        public readonly ReportData Report;
        public StockCameraDepthRequests(Camera near,Camera far,string runId,int pid,Action<Camera,int,int> accept)
        {this.near=near;this.far=far;this.accept=accept;Report=new ReportData{runId=runId,processId=pid};}
        static void Need(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);}
        static bool Same(MethodBase a,MethodBase b){return a!=null&&b!=null&&a.Module.ModuleVersionId==b.Module.ModuleVersionId&&a.MetadataToken==b.MetadataToken&&a.DeclaringType==b.DeclaringType;}
        static string Hash(byte[] data){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
        static string FileHash(string path){using(var s=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
        void Fault(){Report.faults++;Report.status="invalid";}
        public void Install()
        {
            try
            {
                Need(active==null&&near!=null&&far!=null&&accept!=null&&Setter!=null,"Depth witness owner/ABI absent.");
                Assembly stock=typeof(Vessel).Assembly;Report.assemblySha256=FileHash(stock.Location);Report.assemblyMvid=stock.ManifestModule.ModuleVersionId.ToString("D");
                Need(Report.assemblySha256==StockSha&&Report.assemblyMvid==StockMvid,"Depth witness stock identity differs.");
                Need(Harmony.GetPatchInfo(Setter)?.Owners.Any()!=true,"Native depth setter has foreign hooks.");
                Add(AccessTools.DeclaredMethod(typeof(VolumetricObjectBase),"OnEnable",Type.EmptyTypes),2,0,100664670,"17664c39448bbac0854d968bc8bfcfa6a1387b4a3d43a8072fb95ff40edd22a9");
                Add(AccessTools.DeclaredMethod(typeof(DrawDepth),"CheckSupport",new[]{typeof(bool)}),1,1,100664652,"c369c439e83c106edcdc3c7011d79df089284c8f96f838d5a17baf8919c26c73");
                Report.sources=plans.Select(p=>p.row).ToArray();harmony=new Harmony(owner);active=this;installing=true;
                foreach(Plan plan in plans)
                {
                    Need(Harmony.GetPatchInfo(plan.method)?.Owners.Any()!=true,"Depth source acquired foreign owner.");attempted.Add(plan);
                    harmony.Patch(plan.method,transpiler:new HarmonyMethod(Transform){priority=Priority.Normal});
                    Need(plan.row.shapeAdmissions==1&&Owned(plan),"Depth witness installation shape differs.");
                }
                installing=false;installed=true;Report.status="observing";
            }
            catch {installing=false;Fault();Dispose();throw;}
        }
        void Add(MethodInfo method,int expected,int index,int token,string il)
        {
            Need(method!=null&&method.Module.ModuleVersionId.ToString("D")==StockMvid&&method.GetMethodBody()!=null&&Harmony.GetPatchInfo(method)?.Owners.Any()!=true,"Depth source absent/patched.");
            Need(method.MetadataToken==token&&Hash(method.GetMethodBody().GetILAsByteArray())==il,"Pinned depth source token/IL differs.");
            var code=PatchProcessor.ReadMethodBody(method).ToArray();Need(code.Length<=4096,"Depth source instruction bound exceeded.");
            var slots=new List<int>();for(int n=0;n<code.Length;n++)if(code[n].Key==OpCodes.Callvirt&&Same(code[n].Value as MethodInfo,Setter))slots.Add(n);
            Need(slots.Count==expected,"Original depth setter callsite shape differs.");
            plans.Add(new Plan{method=method,index=index,instructions=code.Length,slots=slots.ToArray(),row=new Source{declaringType=method.DeclaringType.FullName,method=method.Name,metadataToken=method.MetadataToken,ilSha256=Hash(method.GetMethodBody().GetILAsByteArray()),expectedSites=expected}});
        }
        bool Owned(Plan plan)
        {
            var p=Harmony.GetPatchInfo(plan.method);
            return p!=null&&p.Owners.Count()==1&&p.Owners.Contains(owner)&&p.Prefixes.Count==0&&p.Postfixes.Count==0&&p.Finalizers.Count==0&&p.Transpilers.Count==1&&p.Transpilers[0].owner==owner&&p.Transpilers[0].priority==Priority.Normal&&p.Transpilers[0].before.Length==0&&p.Transpilers[0].after.Length==0&&Same(p.Transpilers[0].PatchMethod,Transform);
        }
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
        {
            var w=active;Need(w!=null&&(w.installing||w.cleaning),"Unexpected depth source recomposition.");
            Plan p=w.plans.FirstOrDefault(x=>Same(x.method,__originalMethod));Need(p!=null&&Hash(p.method.GetMethodBody().GetILAsByteArray())==p.row.ilSha256,"Depth source IL changed.");
            var code=instructions.Select(c=>new CodeInstruction(c)).ToArray();Need(code.Length==p.instructions,"Depth source instruction count changed.");int found=0;
            for(int n=0;n<code.Length;n++)if(code[n].opcode==OpCodes.Callvirt&&Same(code[n].operand as MethodInfo,Setter))
            {
                Need(found<p.slots.Length&&n==p.slots[found],"Depth source callsite order changed.");
                string wrapper=p.index==0?(found==0?nameof(VolumeCurrent):nameof(VolumeMain)):nameof(DrawDepthCamera);
                code[n].opcode=OpCodes.Call;code[n].operand=AccessTools.DeclaredMethod(typeof(StockCameraDepthRequests),wrapper);found++;
            }
            Need(found==p.row.expectedSites,"Depth source replacement count differs.");
            if(w.installing){Need(p.row.shapeAdmissions==0,"Repeated depth install admission.");p.row.shapeAdmissions++;p.row.observedReplacements=found;}
            return code;
        }
        static void VolumeCurrent(Camera camera,DepthTextureMode requested){Set(camera,requested,0,0);}
        static void VolumeMain(Camera camera,DepthTextureMode requested){Set(camera,requested,0,1);}
        static void DrawDepthCamera(Camera camera,DepthTextureMode requested){Set(camera,requested,1,0);}
        static void Set(Camera camera,DepthTextureMode requested,int source,int slot)
        {
            var w=active;bool owned=w!=null&&(ReferenceEquals(camera,w.near)||ReferenceEquals(camera,w.far));int before=0;bool read=false;
            if(owned)try{before=(int)camera.depthTextureMode;read=true;}catch{w.Fault();}
            camera.depthTextureMode=requested;
            if(owned&&read)try{w.Observe(camera,before,(int)requested,(int)camera.depthTextureMode,source,slot);}catch{w.Fault();}
        }
        void Observe(Camera camera,int before,int requested,int after,int source,int slot)
        {
            Need(thread==Thread.CurrentThread.ManagedThreadId&&events.Count<4096,"Depth witness thread/event bound differs.");
            events.Add(new Event{cameraId=camera.GetInstanceID(),before=before,requested=requested,after=after,frame=Time.frameCount,ticks=Stopwatch.GetTimestamp(),sourceIndex=source,slot=slot});
            Need(requested==(before|(int)DepthTextureMode.Depth)&&after==requested,"Depth request was not additive original Depth demand.");
            accept(camera,before,after);
        }
        public void Audit(){Need(!disposed&&ReferenceEquals(active,this)&&thread==Thread.CurrentThread.ManagedThreadId&&Report.faults==0,"Stock depth witness fault/lifetime changed.");}
        public void Dispose()
        {
            if(disposed)return;
            Need(thread==Thread.CurrentThread.ManagedThreadId,"Depth witness cleanup thread differs.");
            bool exact=installed&&Harmony.GetPatchInfo(Setter)?.Owners.Any()!=true&&Report.assemblySha256==StockSha&&Report.assemblyMvid==StockMvid;
            foreach(Plan p in attempted)exact &= Owned(p)&&Hash(p.method.GetMethodBody().GetILAsByteArray())==p.row.ilSha256;
            Report.exactGraphAtRelease=exact;if(!exact&&attempted.Count>0)Fault();cleaning=true;
            foreach(Plan p in attempted)try{harmony.Unpatch(p.method,HarmonyPatchType.Transpiler,owner);}catch{Fault();}
            cleaning=false;Report.hooksRemoved=attempted.All(p=>Harmony.GetPatchInfo(p.method)?.Owners.Contains(owner)!=true);
            if(!Report.hooksRemoved)Fault();
            Report.events=events.ToArray();Report.cleanupStatus=Report.hooksRemoved?"removed-owned-depth-callsite-hooks":"owned-hooks-remain";
            if(Report.hooksRemoved){if(ReferenceEquals(active,this))active=null;disposed=true;}
            Report.status=Report.faults==0&&exact&&Report.hooksRemoved?"complete":"invalid";
        }
    }
}
