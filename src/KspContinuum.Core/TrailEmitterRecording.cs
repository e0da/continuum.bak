using System;
using System.IO;
using System.Text;
namespace KspContinuum
{
    [Serializable] public sealed class TrailEmitterRecording
    {
        public string schema="continuum-trail-emitter/v1",runId,vesselId,bodyName,mode,emitterIdentity,sceneSha256,wholeCraftSha256;
        public int processId; public uint partId; public long clockFrequency; public double[] bodyFixedToScene; public TrailEmitterFrame[] frames;
    }
    [Serializable] public sealed class TrailEmitterFrame
    {
        public long sequence,startedTicks,endedTicks; public int unityFrame; public double universalTime,currentThrottle,finalThrust;
        public double[] rootBodyFixed,emitterBodyFixed,rootBodyFixedOrientation;
        public bool engineIgnited,emission;
    }
    public static class TrailEmitterFormat
    {
        public const int MaxFrames=4096; public const long MaxBytes=4*1024*1024;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static void Need(bool b,string s){if(!b)throw new InvalidDataException(s);}
        static bool Finite(double v){return !double.IsNaN(v)&&!double.IsInfinity(v);}
        static void Text(string s){Need(!string.IsNullOrEmpty(s)&&Utf8.GetByteCount(s)<=2048,"Emitter identity absent/oversize.");foreach(char c in s)Need(!char.IsControl(c),"Emitter identity contains controls.");}
        static void Hash(string s){Need(s!=null&&s.Length==64,"Emitter digest absent.");foreach(char c in s)Need(c>='0'&&c<='9'||c>='a'&&c<='f',"Emitter digest malformed.");}
        public static void Vector(double[] a,int count){Need(a!=null&&a.Length==count,"Emitter vector shape.");foreach(double v in a)Need(Finite(v)&&Math.Abs(v)<=1e15,"Emitter nonfinite/unbounded number.");}
        public static void Frame(TrailEmitterFrame f)
        { Need(f!=null&&f.sequence>=0&&f.unityFrame>=0&&f.startedTicks>=0&&f.endedTicks>=f.startedTicks&&Finite(f.universalTime)&&Math.Abs(f.universalTime)<=1e12,"Emitter frame identity/time invalid.");Vector(f.rootBodyFixed,3);Vector(f.emitterBodyFixed,3);foreach(double p in f.rootBodyFixed)Need(Math.Abs(p)<=1e12,"Emitter position exceeds measured-span domain.");foreach(double p in f.emitterBodyFixed)Need(Math.Abs(p)<=1e12,"Emitter position exceeds measured-span domain.");Vector(f.rootBodyFixedOrientation,16);Need(Finite(f.currentThrottle)&&f.currentThrottle>=0&&f.currentThrottle<=1&&Finite(f.finalThrust)&&f.finalThrust>=0,"Emitter engine readback invalid.");Rigid(f.rootBodyFixedOrientation); }
        public static void Rigid(double[] m)
        {Vector(m,16);Need(m[3]==0&&m[7]==0&&m[11]==0&&m[12]==0&&m[13]==0&&m[14]==0&&m[15]==1,"Emitter basis must have no translation.");for(int r=0;r<3;r++)for(int c=0;c<3;c++){double d=0;for(int k=0;k<3;k++)d+=m[k*4+r]*m[k*4+c];Need(Math.Abs(d-(r==c?1:0))<=1e-4,"Emitter basis is not rigid.");}double det=m[0]*(m[5]*m[10]-m[6]*m[9])-m[1]*(m[4]*m[10]-m[6]*m[8])+m[2]*(m[4]*m[9]-m[5]*m[8]);Need(det>0,"Emitter basis reflects handedness.");}
        public static void Validate(TrailEmitterRecording r)
        {Need(r!=null&&r.schema=="continuum-trail-emitter/v1","Emitter schema differs.");Text(r.runId);Text(r.vesselId);Text(r.bodyName);Text(r.emitterIdentity);Hash(r.sceneSha256);Hash(r.wholeCraftSha256);Need(r.processId>0&&r.clockFrequency>0&&r.partId>0&&(r.mode=="trajectory-tracer"||r.mode=="engine-readback"),"Emitter header invalid.");Rigid(r.bodyFixedToScene);Need(r.frames!=null&&r.frames.Length>=2&&r.frames.Length<=MaxFrames,"Emitter frame count invalid.");long seq=-1,ticks=0;double time=double.NegativeInfinity;foreach(var f in r.frames){Frame(f);Need(f.sequence>seq&&f.startedTicks>=ticks&&f.universalTime>=time&&(seq<0||f.universalTime-time<=60),"Emitter frames stale or gap >60s.");Need(f.emission==(r.mode=="trajectory-tracer"||(f.engineIgnited&&f.finalThrust>0&&f.currentThrottle>0)),"Emitter policy/readback mismatch.");seq=f.sequence;ticks=f.endedTicks;time=f.universalTime;}Need(r.frames[r.frames.Length-1].universalTime-r.frames[0].universalTime<=3600,"Emitter duration >3600s.");}
        static void Put(BinaryWriter w,string s){byte[] b=Utf8.GetBytes(s);w.Write(b.Length);w.Write(b);}
        static string Get(BinaryReader r){int n=r.ReadInt32();Need(n>0&&n<=2048,"Emitter string bound.");byte[] b=r.ReadBytes(n);Need(b.Length==n,"Emitter string truncated.");return Utf8.GetString(b);}
        static void Put(BinaryWriter w,double[] a){foreach(double x in a)w.Write(x);}
        static double[] Get(BinaryReader r,int n){var a=new double[n];for(int i=0;i<n;i++)a[i]=r.ReadDouble();return a;}
        public static void Write(string path,TrailEmitterRecording r)
        {Validate(r);using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write))using(var w=new BinaryWriter(f,Utf8)){w.Write(0x31524554);w.Write(1);foreach(string s in new[]{r.runId,r.vesselId,r.bodyName,r.mode,r.emitterIdentity,r.sceneSha256,r.wholeCraftSha256})Put(w,s);w.Write(r.processId);w.Write(r.partId);w.Write(r.clockFrequency);Put(w,r.bodyFixedToScene);w.Write(r.frames.Length);foreach(var a in r.frames){w.Write(a.sequence);w.Write(a.startedTicks);w.Write(a.endedTicks);w.Write(a.unityFrame);w.Write(a.universalTime);w.Write(a.currentThrottle);w.Write(a.finalThrust);w.Write(a.engineIgnited);w.Write(a.emission);Put(w,a.rootBodyFixed);Put(w,a.emitterBodyFixed);Put(w,a.rootBodyFixedOrientation);}Need(f.Length<=MaxBytes,"Emitter encoded size exceeded.");}}
        public static TrailEmitterRecording Read(string path)
        {using(var f=File.OpenRead(path)){Need(f.Length>0&&f.Length<=MaxBytes,"Emitter file bound.");using(var b=new BinaryReader(f,Utf8)){Need(b.ReadInt32()==0x31524554&&b.ReadInt32()==1,"Emitter wire differs.");var r=new TrailEmitterRecording{runId=Get(b),vesselId=Get(b),bodyName=Get(b),mode=Get(b),emitterIdentity=Get(b),sceneSha256=Get(b),wholeCraftSha256=Get(b),processId=b.ReadInt32(),partId=b.ReadUInt32(),clockFrequency=b.ReadInt64(),bodyFixedToScene=Get(b,16)};int n=b.ReadInt32();Need(n>=2&&n<=MaxFrames,"Emitter frame bound.");r.frames=new TrailEmitterFrame[n];for(int i=0;i<n;i++)r.frames[i]=new TrailEmitterFrame{sequence=b.ReadInt64(),startedTicks=b.ReadInt64(),endedTicks=b.ReadInt64(),unityFrame=b.ReadInt32(),universalTime=b.ReadDouble(),currentThrottle=b.ReadDouble(),finalThrust=b.ReadDouble(),engineIgnited=b.ReadBoolean(),emission=b.ReadBoolean(),rootBodyFixed=Get(b,3),emitterBodyFixed=Get(b,3),rootBodyFixedOrientation=Get(b,16)};Need(f.Position==f.Length,"Emitter trailing bytes.");Validate(r);return r;}}}
        public static double[] Position(TrailEmitterRecording r,double[] p){Vector(p,3);var m=r.bodyFixedToScene;return new[]{m[0]*p[0]+m[1]*p[1]+m[2]*p[2],m[4]*p[0]+m[5]*p[1]+m[6]*p[2],m[8]*p[0]+m[9]*p[1]+m[10]*p[2]};}
    }
}
