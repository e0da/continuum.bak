using System;
using System.Globalization;
using KspContinuum;
using UnityEngine;

public sealed class OwnedFlightOverlay : IDisposable
{
    [Serializable] public sealed class StatusData
    {
        public string schema="continuum-owned-flight-overlay/v1",depthPolicy="unoccluded scientific overlay; not depth evidence",parent,trajectoryStatus;
        public long regionId;public int trajectoryPoints,trajectoryStride,drawnSegments,gridLines;
        public double spacingMeters,overlayNear,overlayFar;
        public bool grid,sourceStateMatches;
        public string validity="Prediction state/context only checked here; host owns schedule/implementation validity";
    }
    public readonly StatusData Status=new StatusData();
    const int RingSegments=64,MaximumTrajectorySegments=2048;
    readonly double[] cosine=new double[RingSegments+1],sine=new double[RingSegments+1];
    Texture2D pixel;GUIStyle label;bool disposed;
    public OwnedFlightOverlay(){for(int i=0;i<=RingSegments;i++){double angle=i*2*Math.PI/RingSegments;cosine[i]=Math.Cos(angle);sine[i]=Math.Sin(angle);}}
    static ScientificVector3 Point(Vec p){return new ScientificVector3(p.X,p.Y,p.Z);}
    static ScientificVector3 Add(ScientificVector3 a,ScientificVector3 b){return new ScientificVector3(a.x+b.x,a.y+b.y,a.z+b.z);}
    static ScientificVector3 Sub(ScientificVector3 a,ScientificVector3 b){return new ScientificVector3(a.x-b.x,a.y-b.y,a.z-b.z);}
    static ScientificVector3 Scale(ScientificVector3 p,double s){return new ScientificVector3(p.x*s,p.y*s,p.z*s);}
    static double Dot(ScientificVector3 a,ScientificVector3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}
    public void Draw(Camera camera,Vec cameraOriginBodyMeters,OwnedFlightPresentationLease frame,OwnedFlightPredictionResult prediction,bool grid,string parent="root")
    {
        if(disposed)throw new ObjectDisposedException("OwnedFlightOverlay");if(camera==null||frame==null)return;
        if(parent!="root"&&parent!="com")throw new ArgumentException("Overlay parent must be root or com.");
        if(Event.current!=null&&Event.current.type!=EventType.Repaint)return;
        if(pixel==null){pixel=new Texture2D(1,1,TextureFormat.RGBA32,false,true);pixel.SetPixel(0,0,Color.white);pixel.Apply(false,true);label=new GUIStyle(GUI.skin.label){fontSize=13};label.normal.textColor=Color.white;}
        Vector3 right=camera.transform.right,up=camera.transform.up,forward=camera.transform.forward;
        double cameraRadius=Math.Sqrt(cameraOriginBodyMeters.X*cameraOriginBodyMeters.X+cameraOriginBodyMeters.Y*cameraOriginBodyMeters.Y+cameraOriginBodyMeters.Z*cameraOriginBodyMeters.Z);
        Status.overlayNear=Math.Max(.001,camera.nearClipPlane);Status.overlayFar=Math.Max(camera.farClipPlane,cameraRadius+frame.Context.Radius*4);
        var view=new ScientificView(Point(cameraOriginBodyMeters),new ScientificVector3(right.x,right.y,right.z),new ScientificVector3(up.x,up.y,up.z),new ScientificVector3(forward.x,forward.y,forward.z),camera.pixelWidth,camera.pixelHeight,camera.orthographic?ScientificProjectionKind.Orthographic:ScientificProjectionKind.Perspective,camera.fieldOfView,camera.orthographicSize*2,Status.overlayNear,Status.overlayFar);
        Status.drawnSegments=Status.gridLines=0;Status.grid=grid;Status.parent=parent;
        Color prior=GUI.color;Matrix4x4 matrix=GUI.matrix;
        try
        {
            if(prediction!=null)
            {
                Status.trajectoryPoints=prediction.Count;Status.trajectoryStride=Math.Max(1,(prediction.Count-1+MaximumTrajectorySegments-1)/MaximumTrajectorySegments);
                Status.sourceStateMatches=prediction.SourceUnchanged&&prediction.ContextSha256==frame.Context.SourceSha256&&prediction.SourceStamp.Matches(frame.Read.Stamp);
                Status.trajectoryStatus=prediction.Status+(Status.sourceStateMatches?" [state/context match]":" [stale/source mismatch]");
                GUI.color=prediction.Status!="completed"?new Color(1,.3f,.25f):Status.sourceStateMatches?new Color(.2f,.95f,1):new Color(1,.65f,.15f);
                if(prediction.Count>8193)throw new ArgumentException("Prediction overlay exceeds bounded point count.");
                for(int i=0;i<prediction.Count-1;i+=Status.trajectoryStride){int end=Math.Min(prediction.Count-1,i+Status.trajectoryStride);Line(Point(prediction.GetPoint(i).CenterPosition),Point(prediction.GetPoint(end).CenterPosition),view,2);}
                if(prediction.Count>0){var end=prediction.GetPoint(prediction.Count-1);Text(Point(end.CenterPosition),view,"prediction "+Status.trajectoryStatus);}
            }
            else{Status.trajectoryPoints=0;Status.trajectoryStride=1;Status.trajectoryStatus="none";Status.sourceStateMatches=false;}
            if(grid)
            {
                var read=frame.Read;
                {
                    var region=read.GetRegion(0);Status.regionId=region.Id;var motion=region.Motion;
                    Vec focus=frame.Origin+motion.RootPosition;if(parent=="com")focus+=motion.Orientation.Rotate(region.Properties.CenterOfMass);
                    ScientificVector3 origin=Point(focus),u=Point(motion.Orientation.Rotate(new Vec(1,0,0))),v=Point(motion.Orientation.Rotate(new Vec(0,1,0))),axis=Point(motion.Orientation.Rotate(new Vec(0,0,1)));
                    double depth=Dot(Sub(origin,view.origin),view.forward);if(depth>0)
                    {
                        double metresPer100Pixels=100/view.PixelsPerMeter(depth),spacing=1;
                        while(spacing<metresPer100Pixels&&spacing<1e9)spacing*=10;
                        while(spacing>metresPer100Pixels*10&&spacing>1e-6)spacing*=.1;
                        Status.spacingMeters=spacing;double extent=spacing*5;GUI.color=new Color(.2f,.8f,1,.35f);
                        for(int i=-5;i<=5;i++){double offset=i*spacing;Line(Add(origin,Add(Scale(u,offset),Scale(v,-extent))),Add(origin,Add(Scale(u,offset),Scale(v,extent))),view,1);Line(Add(origin,Add(Scale(v,offset),Scale(u,-extent))),Add(origin,Add(Scale(v,offset),Scale(u,extent))),view,1);Status.gridLines+=2;}
                        GUI.color=new Color(.3f,.9f,1,.7f);for(int ring=0;ring<2;ring++)
                        {double radius=spacing*(ring==0?1:10);var previous=Add(origin,Scale(u,radius));for(int i=1;i<=RingSegments;i++){var next=Add(origin,Add(Scale(u,radius*cosine[i]),Scale(v,radius*sine[i])));Line(previous,next,view,1);previous=next;}Text(Add(origin,Scale(u,radius)),view,radius.ToString("G4",CultureInfo.InvariantCulture)+" m");}
                        GUI.color=Color.red;Line(origin,Add(origin,Scale(u,spacing*2)),view,2);GUI.color=Color.green;Line(origin,Add(origin,Scale(v,spacing*2)),view,2);GUI.color=Color.blue;Line(origin,Add(origin,Scale(axis,spacing*2)),view,2);
                        GUI.color=Color.white;Text(origin,view,parent+" / ship frame / region "+region.Id+"; grid "+spacing.ToString("G4",CultureInfo.InvariantCulture)+" m");
                    }
                }
            }
        }
        finally{GUI.color=prior;GUI.matrix=matrix;}
    }
    void Text(ScientificVector3 p,ScientificView view,string text)
    {double x,y,z;if(view.Project(p,out x,out y,out z))GUI.Label(new Rect((float)x+5,(float)y+5,560,24),text,label);}
    void Line(ScientificVector3 a,ScientificVector3 b,ScientificView view,float width)
    {
        double za=Dot(Sub(a,view.origin),view.forward),zb=Dot(Sub(b,view.origin),view.forward),lo=0,hi=1;
        if(!ClipDepth(za,zb,view.near,true,ref lo,ref hi)||!ClipDepth(za,zb,view.far,false,ref lo,ref hi))return;
        var delta=Sub(b,a);double ax,ay,az,bx,by,bz;if(!view.ProjectUnclipped(Add(a,Scale(delta,lo)),out ax,out ay,out az)||!view.ProjectUnclipped(Add(a,Scale(delta,hi)),out bx,out by,out bz))return;
        double dx=bx-ax,dy=by-ay;lo=0;hi=1;if(!Clip2(-dx,ax,ref lo,ref hi)||!Clip2(dx,view.width-ax,ref lo,ref hi)||!Clip2(-dy,ay,ref lo,ref hi)||!Clip2(dy,view.height-ay,ref lo,ref hi))return;
        float x=(float)(ax+dx*lo),y=(float)(ay+dy*lo),sx=(float)(dx*(hi-lo)),sy=(float)(dy*(hi-lo));float length=Mathf.Sqrt(sx*sx+sy*sy);if(length<=0)return;
        var previous=GUI.matrix;try{GUIUtility.RotateAroundPivot(Mathf.Atan2(sy,sx)*Mathf.Rad2Deg,new Vector2(x,y));GUI.DrawTexture(new Rect(x,y-width*.5f,length,width),pixel);}finally{GUI.matrix=previous;}Status.drawnSegments++;
    }
    static bool ClipDepth(double a,double b,double boundary,bool minimum,ref double lo,ref double hi)
    {double d=b-a;if(d==0)return minimum?a>=boundary:a<=boundary;double t=(boundary-a)/d;if((d>0)==minimum)lo=Math.Max(lo,t);else hi=Math.Min(hi,t);return lo<=hi;}
    static bool Clip2(double p,double q,ref double lo,ref double hi)
    {if(p==0)return q>=0;double t=q/p;if(p<0)lo=Math.Max(lo,t);else hi=Math.Min(hi,t);return lo<=hi;}
    public void Dispose(){if(disposed)return;disposed=true;if(pixel!=null)UnityEngine.Object.Destroy(pixel);}
}
