using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
namespace KspContinuum
{
    public sealed class TrailEmitterCapture : IDisposable
    {
        [Serializable] public sealed class CaptureReport
        {
            public string schema="continuum-trail-emitter-capture/v1",status="admitting",runId,vesselId,mode,bodyName,emitterIdentity,partSelector,recordingPath,recordingSha256,sceneSha256,wholeCraftSha256,reason,cleanupStatus;
            public string scope="Actual body-fixed part/emitter trajectory and engine scalar readbacks at Probe observation; artistic interpolated particle births, not captured plume/weather or synchronized physics publications.";
            public string clockScope="Per-frame startedTicks/endedTicks bracket main-thread admission and scalar/transform reads on Stopwatch at clockFrequency; validation/append charged in captureTicks after endedTicks. UT is observed simulation time, not inferred from wall clock.";
            public int processId,frames; public uint partId; public long captureTicks,clockFrequency=Stopwatch.Frequency;
        }
        public CaptureReport Report {get;private set;}=new CaptureReport();
        public static bool Requested {get{return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTINUUM_TRAIL_EMITTER_RECORDING"));}}
        public string FailureReason {get{return Report.status=="invalid"||Report.status=="persistence-failed"?Report.reason:null;}}
        readonly Vessel vessel; readonly CelestialBody body; readonly Part part; readonly Part[] parts; readonly Part[] parents;
        readonly ModuleEngines engine; readonly Transform emitter; readonly int engineIndex=-1;
        readonly string output; readonly TrailEmitterRecording recording; readonly List<TrailEmitterFrame> frames=new List<TrailEmitterFrame>();
        readonly int thread=System.Threading.Thread.CurrentThread.ManagedThreadId; bool finished,disposed;
        public static TrailEmitterCapture Create(Vessel vessel,string runId)
        {string path=Environment.GetEnvironmentVariable("CONTINUUM_TRAIL_EMITTER_RECORDING");return string.IsNullOrEmpty(path)?null:new TrailEmitterCapture(vessel,runId,path);}
        static void Need(bool b,string s){if(!b)throw new InvalidDataException(s);}
        static double[] D(Vector3d v){return new[]{v.x,v.y,v.z};}
        static double[] Mul(double[] a,double[] b){var c=new double[16];for(int r=0;r<3;r++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)c[r*4+j]+=a[r*4+k]*b[k*4+j];c[15]=1;return c;}
        static double[] Inverse(double[] m)
        {double a=m[0],b=m[1],c=m[2],d=m[4],e=m[5],f=m[6],g=m[8],h=m[9],i=m[10],det=a*(e*i-f*h)-b*(d*i-f*g)+c*(d*h-e*g);Need(!double.IsNaN(det)&&!double.IsInfinity(det)&&Math.Abs(det)>1e-12,"Scaled body basis singular.");return new[]{(e*i-f*h)/det,(c*h-b*i)/det,(b*f-c*e)/det,0,(f*g-d*i)/det,(a*i-c*g)/det,(c*d-a*f)/det,0,(d*h-e*g)/det,(b*g-a*h)/det,(a*e-b*d)/det,0,0,0,0,1};}
        TrailEmitterCapture(Vessel source,string runId,string path)
        {
            Need(Path.IsPathRooted(path),"Absolute emitter recording path required.");output=Path.GetFullPath(path);Need(!File.Exists(output)&&!Directory.Exists(output)&&!File.Exists(output+".partial")&&!File.Exists(output+".json"),"Emitter output exists.");Directory.CreateDirectory(Path.GetDirectoryName(output));Report.recordingPath=output;Report.runId=runId;Report.processId=Process.GetCurrentProcess().Id;
            try
            {
                Need(source!=null&&source.loaded&&!source.packed&&HighLogic.LoadedSceneIsFlight,"Emitter needs actual loaded physical flight.");vessel=source;body=source.mainBody;Need(body!=null&&body.scaledBody!=null,"Emitter body unavailable.");Report.vesselId=vessel.id.ToString("D");Report.bodyName=body.bodyName;
                Report.partSelector=Environment.GetEnvironmentVariable("CONTINUUM_TRAIL_EMITTER_PART_ID");uint id;
                if(Report.partSelector=="root") {Need(vessel.rootPart!=null,"Emitter root unavailable.");id=vessel.rootPart.flightID;}
                else Need(uint.TryParse(Report.partSelector,out id)&&id>0,"Explicit emitter part selector root/nonzero flightID required.");
                Need(id>0,"Emitter resolved zero part ID.");Report.partId=id;parts=vessel.parts.ToArray();parents=parts.Select(p=>p.parent).ToArray();part=parts.SingleOrDefault(p=>p.flightID==id);Need(part!=null,"Emitter part not in selected vessel.");
                Report.mode=Environment.GetEnvironmentVariable("CONTINUUM_TRAIL_EMITTER_MODE")??"trajectory-tracer";Need(Report.mode=="trajectory-tracer"||Report.mode=="engine-readback","Emitter mode differs.");emitter=part.transform;
                if(Report.mode=="engine-readback") {engine=part.Modules.OfType<ModuleEngines>().FirstOrDefault();Need(engine!=null&&engine.thrustTransforms!=null&&engine.thrustTransforms.Count>0&&engine.thrustTransforms[0]!=null,"Actual engine thrust transform unavailable.");engineIndex=Array.IndexOf(part.Modules.Cast<PartModule>().ToArray(),engine);emitter=engine.thrustTransforms[0];}
                Report.emitterIdentity=id+"/"+(engine==null?"part-origin":"module/"+engineIndex+"/thrust-transform/0/"+emitter.name);
                string directory=Environment.GetEnvironmentVariable("CONTINUUM_MULTISCALE_SCENE");Need(!string.IsNullOrEmpty(directory)&&Path.IsPathRooted(directory),"Emitter requires fresh captured scene.");string scenePath=Path.Combine(directory,"manifest.json"),wholePath=Path.Combine(directory,"whole-craft","manifest.json");Need(new FileInfo(scenePath).Length<=16*1024*1024,"Scene bound.");Report.sceneSha256=ScaleCheckpointLoadState.HashFile(scenePath);Report.wholeCraftSha256=ScaleCheckpointLoadState.HashFile(wholePath);
                var scene=JsonUtility.FromJson<MultiscaleSceneManifest>(File.ReadAllText(scenePath));var whole=WholeCraftSceneCapture.TakeRuntimeManifest(vessel,wholePath,Report.wholeCraftSha256,runId);
                Need(scene!=null&&scene.runId==runId&&scene.vesselId==Report.vesselId&&scene.bodyName==body.bodyName&&scene.wholeCraftSha256==Report.wholeCraftSha256&&scene.wholeCraft=="whole-craft/manifest.json"&&whole.draws.Any(d=>d.partId==id),"Emitter cold scene identity/membership differs.");Need(ScaleCheckpointLoadState.HashFile(scenePath)==Report.sceneSha256,"Scene changed during emitter admission.");
                Matrix4x4 scaled=body.scaledBody.transform.localToWorldMatrix;var current=new double[16];for(int r=0;r<3;r++)for(int c=0;c<3;c++)current[r*4+c]=(double)scaled[r,c]*ScaledSpace.ScaleFactor;current[15]=1;
                var fixedBasis=new double[16];for(int c=0;c<3;c++){Vector3d axis=c==0?new Vector3d(1,0,0):c==1?new Vector3d(0,1,0):new Vector3d(0,0,1);Vector3d v=body.BodyFrame.LocalToWorld(axis.xzy).xzy;fixedBasis[c]=v.x;fixedBasis[4+c]=v.y;fixedBasis[8+c]=v.z;}fixedBasis[15]=1;
                var captured=(double[])scene.bodyLocalToMeters.Clone();captured[3]=captured[7]=captured[11]=0;
                recording=new TrailEmitterRecording{runId=runId,processId=Report.processId,clockFrequency=Stopwatch.Frequency,vesselId=Report.vesselId,bodyName=body.bodyName,partId=id,mode=Report.mode,emitterIdentity=Report.emitterIdentity,sceneSha256=Report.sceneSha256,wholeCraftSha256=Report.wholeCraftSha256,bodyFixedToScene=Mul(Mul(captured,Inverse(current)),fixedBasis)};TrailEmitterFormat.Rigid(recording.bodyFixedToScene);Report.status="admitted";
            }
            catch(Exception e){Report.status="invalid";Report.reason=e.ToString();}
        }
        public void CaptureFrame()
        {
            long start=Stopwatch.GetTimestamp();try
            {
                Need(!disposed&&!finished&&FailureReason==null&&thread==System.Threading.Thread.CurrentThread.ManagedThreadId,"Emitter capture owner unavailable.");Need(vessel==FlightGlobals.ActiveVessel&&vessel.loaded&&!vessel.packed&&ReferenceEquals(vessel.mainBody,body)&&vessel.parts.Count==parts.Length,"Emitter vessel/body/topology changed.");for(int i=0;i<parts.Length;i++)Need(parts[i]!=null&&ReferenceEquals(vessel.parts[i],parts[i])&&ReferenceEquals(parts[i].parent,parents[i]),"Emitter ordered part topology changed.");Need(part!=null&&emitter!=null&&frames.Count<TrailEmitterFormat.MaxFrames,"Emitter source/capacity unavailable.");if(engine!=null)Need(engineIndex<part.Modules.Count&&ReferenceEquals(part.Modules[engineIndex],engine)&&engine.thrustTransforms.Count>0&&ReferenceEquals(engine.thrustTransforms[0],emitter),"Emitter engine identity changed.");
                if(Report.partSelector=="root")Need(ReferenceEquals(vessel.rootPart,part),"Emitter root identity changed.");
                Vector3d com=vessel.GetWorldPos3D(),root=(com-body.position)+((Vector3d)vessel.transform.position-com),point=(com-body.position)+((Vector3d)emitter.position-com);
                var orient=new double[16];for(int c=0;c<3;c++){Vector3d axis=c==0?(Vector3d)vessel.transform.right:c==1?(Vector3d)vessel.transform.up:(Vector3d)vessel.transform.forward;Vector3d v=body.GetRelSurfaceDirection(axis);orient[c]=v.x;orient[4+c]=v.y;orient[8+c]=v.z;}orient[15]=1;
                var f=new TrailEmitterFrame{sequence=frames.Count,startedTicks=start,unityFrame=Time.frameCount,universalTime=Planetarium.GetUniversalTime(),rootBodyFixed=D(body.BodyFrame.WorldToLocal(root.xzy).xzy),emitterBodyFixed=D(body.BodyFrame.WorldToLocal(point.xzy).xzy),rootBodyFixedOrientation=orient,currentThrottle=engine==null?0:engine.currentThrottle,finalThrust=engine==null?0:engine.finalThrust,engineIgnited=engine!=null&&engine.EngineIgnited};f.emission=engine==null||(f.engineIgnited&&f.finalThrust>0&&f.currentThrottle>0);f.endedTicks=Stopwatch.GetTimestamp();TrailEmitterFormat.Frame(f);if(frames.Count>0)Need(f.universalTime>=frames[frames.Count-1].universalTime&&f.universalTime-frames[frames.Count-1].universalTime<=60,"Emitter time stale/gap.");frames.Add(f);Report.frames=frames.Count;
            }
            catch(Exception e){Report.status="invalid";if(Report.reason==null)Report.reason=e.ToString();throw;}finally{Report.captureTicks+=Stopwatch.GetTimestamp()-start;}
        }
        public void Finish(bool complete,string reason)
        {
            if(finished)return;finished=true;
            try {if(!complete||FailureReason!=null){Report.status="invalid";if(Report.reason==null)Report.reason=reason??"Interrupted emitter capture.";}else{recording.frames=frames.ToArray();TrailEmitterFormat.Write(output+".partial",recording);var check=TrailEmitterFormat.Read(output+".partial");Need(check.frames.Length==frames.Count,"Emitter re-read frame count differs.");File.Move(output+".partial",output);Report.recordingSha256=ScaleCheckpointLoadState.HashFile(output);Report.status="complete";}}
            catch(Exception e){Report.status="persistence-failed";Report.reason=e.ToString();}
            finally{File.WriteAllText(output+".json",JsonUtility.ToJson(Report,true));}
        }
        public void Dispose(){if(disposed)return;if(!finished)Finish(false,"Emitter owner disposed before completion.");disposed=true;Report.cleanupStatus="readonly-owner-released-no-hooks-or-physics-writes";File.WriteAllText(output+".json",JsonUtility.ToJson(Report,true));}
    }
}
