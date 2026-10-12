using System;
namespace KspContinuum
{
    public struct MeasuredTrailPoint { public double x,y,z,time,width,opacity; }
    public struct MeasuredTrailSegment { public MeasuredTrailPoint a,b; public int run; }
    public sealed class MeasuredTrailSegments
    {
        public const int MaxSegments=8192, MaxWindChanges=128;
        struct Wind { public double time,x,y,z; }
        readonly double[] times;
        readonly double[][] positions;
        readonly bool[] emission;
        readonly Wind[] winds=new Wind[MaxWindChanges];
        readonly MeasuredTrailPoint[] points=new MeasuredTrailPoint[16384];
        readonly bool[] keep=new bool[16384];
        readonly int[] stack=new int[32768];
        readonly int[] runs=new int[16384];
        public readonly MeasuredTrailSegment[] Segments=new MeasuredTrailSegment[MaxSegments];
        int windCount;
        public int WindChanges { get { return windCount; } }
        public int Count { get; private set; }
        public int InputSpans { get; private set; }
        public int RetainedPoints { get; private set; }
        public int NearClippedSpans { get; private set; }
        public int GapSpans { get; private set; }
        public const double MaximumSourceSpanSeconds=.25;
        public double MaximumRemovedErrorPixels { get; private set; }
        public MeasuredTrailSegments(TrailEmitterRecording recording)
        {
            TrailEmitterFormat.Validate(recording);
            int n=recording.frames.Length; times=new double[n];positions=new double[n][];emission=new bool[n];
            for(int i=0;i<n;i++) { times[i]=recording.frames[i].universalTime-recording.frames[0].universalTime;positions[i]=TrailEmitterFormat.Position(recording,recording.frames[i].emitterBodyFixed);emission[i]=recording.frames[i].emission; }
        }
        static bool Finite(double x) { return !double.IsNaN(x)&&!double.IsInfinity(x); }
        static void Need(bool b) { if(!b)throw new ArgumentException("Measured segment finite/bounded contract failed."); }
        public void ResetWind() { windCount=0; }
        public void CheckWind(double time,double x,double y,double z)
        { Need(Finite(time)&&time>=0&&time<=times[times.Length-1]&&Finite(x)&&Finite(y)&&Finite(z)&&Math.Abs(x)<=1e6&&Math.Abs(y)<=1e6&&Math.Abs(z)<=1e6&&(windCount==0||time>=winds[windCount-1].time));Need(windCount<MaxWindChanges||(windCount>0&&winds[windCount-1].time==time)); }
        public void SetWind(double time,double x,double y,double z)
        { CheckWind(time,x,y,z);if(windCount>0&&winds[windCount-1].time==time)windCount--;winds[windCount++]=new Wind{time=time,x=x,y=y,z=z}; }
        MeasuredTrailPoint Point(int span,double birth,double now,double width,double diffusion,double decay)
        {
            double fraction=(birth-times[span])/(times[span+1]-times[span]);var a=positions[span];var b=positions[span+1];
            var p=new MeasuredTrailPoint{x=fraction==1?b[0]:a[0]+(b[0]-a[0])*fraction,y=fraction==1?b[1]:a[1]+(b[1]-a[1])*fraction,z=fraction==1?b[2]:a[2]+(b[2]-a[2])*fraction,time=birth,width=Math.Sqrt(width*width+2*diffusion*(now-birth)),opacity=Math.Exp(-decay*(now-birth))};
            for(int i=0;i<windCount;i++) { double start=Math.Max(birth,winds[i].time),end=Math.Min(now,i+1<windCount?winds[i+1].time:now);if(end>start){p.x+=winds[i].x*(end-start);p.y+=winds[i].y*(end-start);p.z+=winds[i].z*(end-start);} }
            return p;
        }
        static double Eye(MeasuredTrailPoint p,double[] camera,double[] forward) {return (p.x-camera[0])*forward[0]+(p.y-camera[1])*forward[1]+(p.z-camera[2])*forward[2];}
        static MeasuredTrailPoint Lerp(MeasuredTrailPoint a,MeasuredTrailPoint b,double u)
        {return new MeasuredTrailPoint{x=a.x+(b.x-a.x)*u,y=a.y+(b.y-a.y)*u,z=a.z+(b.z-a.z)*u,time=a.time+(b.time-a.time)*u,width=a.width+(b.width-a.width)*u,opacity=a.opacity+(b.opacity-a.opacity)*u};}
        static void Project(MeasuredTrailPoint p,double[] camera,double[] right,double[] up,double[] forward,double focal,out double x,out double y)
        {double d=Eye(p,camera,forward);x=((p.x-camera[0])*right[0]+(p.y-camera[1])*right[1]+(p.z-camera[2])*right[2])*focal/d;y=((p.x-camera[0])*up[0]+(p.y-camera[1])*up[1]+(p.z-camera[2])*up[2])*focal/d;}
        public void Build(double now,double lifetime,double width,double diffusion,double decay,double[] camera,double[] right,double[] up,double[] forward,double focal,double near,double errorPixels)
        {
            Need(Finite(now)&&now>=0&&now<=times[times.Length-1]&&Finite(lifetime)&&lifetime>0&&lifetime<=3600&&Finite(width)&&width>=.001&&width<=1000&&Finite(diffusion)&&diffusion>=0&&diffusion<=1000&&Finite(decay)&&decay>=0&&decay<=100&&Finite(focal)&&focal>0&&focal<=1e8&&Finite(near)&&near>0&&near<=1e12&&Finite(errorPixels)&&errorPixels>=0&&errorPixels<=4);
            foreach(var v in new[]{camera,right,up,forward}){Need(v!=null&&v.Length==3);foreach(double x in v)Need(Finite(x)&&Math.Abs(x)<=1e15);}
            foreach(var v in new[]{right,up,forward})Need(Math.Abs(v[0]*v[0]+v[1]*v[1]+v[2]*v[2]-1)<1e-4);
            int count=0,run=0;InputSpans=GapSpans=NearClippedSpans=Count=RetainedPoints=0;MaximumRemovedErrorPixels=0;
            for(int i=0;i+1<times.Length&&times[i]<now;i++)
            {
                double start=Math.Max(times[i],Math.Max(0,now-lifetime)),end=Math.Min(now,times[i+1]);
                if(times[i+1]-times[i]>MaximumSourceSpanSeconds){GapSpans++;run++;continue;}
                if(!emission[i]||end<=start||times[i+1]<=times[i]){run++;continue;}
                double cursor=start;
                while(cursor<end)
                {
                    double stop=end;for(int k=0;k<windCount;k++)if(winds[k].time>cursor&&winds[k].time<stop)stop=winds[k].time;
                    var a=Point(i,cursor,now,width,diffusion,decay);var b=Point(i,stop,now,width,diffusion,decay);double da=Eye(a,camera,forward),db=Eye(b,camera,forward);InputSpans++;
                    if(da<near&&db<near){run++;cursor=stop;continue;}
                    if(da<near){a=Lerp(a,b,(near-da)/(db-da));run++;NearClippedSpans++;}
                    if(db<near){b=Lerp(a,b,(near-Eye(a,camera,forward))/(db-Eye(a,camera,forward)));NearClippedSpans++;}
                    a.width=Math.Sqrt(width*width+2*diffusion*(now-a.time));a.opacity=Math.Exp(-decay*(now-a.time));b.width=Math.Sqrt(width*width+2*diffusion*(now-b.time));b.opacity=Math.Exp(-decay*(now-b.time));
                    Need(count+2<=points.Length&&Finite(a.x)&&Finite(a.y)&&Finite(a.z)&&Finite(b.x)&&Finite(b.y)&&Finite(b.z));
                    bool join=count>0&&runs[count-1]==run&&points[count-1].time==a.time;
                    if(!join){points[count]=a;runs[count++]=run;}
                    points[count]=b;runs[count++]=run;
                    if(db<near)run++;
                    cursor=stop;
                }
            }
            Array.Clear(keep,0,count);
            for(int start=0;start<count;)
            {
                int end=start;while(end+1<count&&runs[end+1]==runs[start])end++;
                keep[start]=keep[end]=true;int top=0;stack[top++]=start;stack[top++]=end;
                while(top>0)
                {
                    int hi=stack[--top],lo=stack[--top];if(hi<=lo+1)continue;
                    double ax,ay,bx,by;Project(points[lo],camera,right,up,forward,focal,out ax,out ay);Project(points[hi],camera,right,up,forward,focal,out bx,out by);
                    double dx=bx-ax,dy=by-ay,length=dx*dx+dy*dy,max=-1;int chosen=-1;
                    for(int k=lo+1;k<hi;k++){double x,y;Project(points[k],camera,right,up,forward,focal,out x,out y);double u=length==0?0:Math.Max(0,Math.Min(1,((x-ax)*dx+(y-ay)*dy)/length));double ex=x-ax-u*dx,ey=y-ay-u*dy,d=Math.Sqrt(ex*ex+ey*ey);Need(Finite(d));if(d>max){max=d;chosen=k;}}
                    if(max>errorPixels){keep[chosen]=true;stack[top++]=lo;stack[top++]=chosen;stack[top++]=chosen;stack[top++]=hi;}else MaximumRemovedErrorPixels=Math.Max(MaximumRemovedErrorPixels,max);
                }
                int previous=start;RetainedPoints++;
                for(int k=start+1;k<=end;k++)if(keep[k]){Need(Count<MaxSegments);Segments[Count++]=new MeasuredTrailSegment{a=points[previous],b=points[k],run=runs[k]};previous=k;RetainedPoints++;}
                start=end+1;
            }
        }
    }
}
