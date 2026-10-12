using System;
using System.Collections.Generic;
using System.Globalization;
using KspContinuum;
using UnityEngine;

public sealed class ScientificOverlay : IDisposable
{
    [Serializable] public sealed class AnnotationData
    {
        public string id,kind,label,unit,validity,plane,positionPolicy;
        public double value,radius,spacing;
        public int cells;
        public bool valid;
        public double[] a,b,axisU,axisV;
    }
    [Serializable] public sealed class StatusData
    {
        public string schema="continuum-scientific-overlay/v1",depthPolicy="overlay-first-unoccluded",status="installed",lastError="",gridPlane="view",ringPolicy="64 world-plane chords; no projected circle-error guarantee",annotationScope="Authored diagnostics only; no simulation, physical culling or reentry prediction";
        public int annotations,capacity=256,viewportWidth,viewportHeight,drawnMarkers,drawnSegments,drawnLabels,rejectedCommands;
        public long generation,drawCalls;
        public bool gridEnabled;
        public double gridSpacingMeters,gridExtentMeters,gridRequestedExtentMeters;
        public double[] focusPosition=new double[3];
        public string gridParentLabel="camera focus; reference axes unavailable";
        public bool referenceInstalled;
        public double[] referencePosition=new double[3],referenceRight=new double[3],referenceUp=new double[3],referenceForward=new double[3];
        public AnnotationData[] rows=new AnnotationData[0];
    }
    public readonly StatusData Status=new StatusData();
    readonly AnnotationStore store=new AnnotationStore(256);
    readonly Dictionary<string,string> planes=new Dictionary<string,string>(StringComparer.Ordinal);
    readonly Dictionary<string,string> labels=new Dictionary<string,string>(StringComparer.Ordinal);
    ScientificVector3 focus;
    ScientificVector3 referenceOrigin,referenceRight,referenceUp,referenceForward;
    GUIStyle style;
    bool disposed;
    const int RingSegments=64;

    public void SetFocus(ScientificVector3 position)
    {
        Finite(position.x);Finite(position.y);Finite(position.z);if(Math.Abs(position.x)>1e15||Math.Abs(position.y)>1e15||Math.Abs(position.z)>1e15)throw new ArgumentException("Focus exceeds coordinate bounds.");focus=position;Status.focusPosition[0]=position.x;Status.focusPosition[1]=position.y;Status.focusPosition[2]=position.z;
    }
    public void SetReference(ScientificVector3 position,ScientificVector3 right,ScientificVector3 up,ScientificVector3 forward,string label)
    {
        if(string.IsNullOrEmpty(label)||label.Length>160||Control(label))throw new ArgumentException("Reference label is invalid.");
        // Validate the supplied basis without repairing a reflected or sheared frame.
        ScientificView.CheckVector(position);ScientificView.CheckAxes(right,up);ScientificView.CheckAxes(right,forward);ScientificView.CheckAxes(up,forward);
        var cross=new ScientificVector3(right.y*up.z-right.z*up.y,right.z*up.x-right.x*up.z,right.x*up.y-right.y*up.x);
        if(Dot(cross,forward)<1-1e-6)throw new ArgumentException("Reference axes must be right handed.");
        referenceOrigin=position;referenceRight=right;referenceUp=up;referenceForward=forward;
        Copy(position,Status.referencePosition);Copy(right,Status.referenceRight);Copy(up,Status.referenceUp);Copy(forward,Status.referenceForward);
        Status.gridParentLabel=label;Status.referenceInstalled=true;
    }
    static void Copy(ScientificVector3 p,double[] values) {values[0]=p.x;values[1]=p.y;values[2]=p.z;}
    public void Publish(ScientificAnnotation annotation)
    {
        if(disposed)throw new InvalidOperationException("Overlay is disposed.");
        if(annotation==null)throw new ArgumentNullException("annotation");Id(annotation.id);
        if(annotation.label.Length>160||annotation.unit.Length>24||(annotation.kind==ScientificAnnotationKind.Grid&&annotation.cells>64))throw new ArgumentException("Published annotation exceeds overlay bounds.");
        string text=annotation.label;if(annotation.kind==ScientificAnnotationKind.Value||annotation.kind==ScientificAnnotationKind.Measurement)text+=" "+annotation.value.ToString("G6",CultureInfo.InvariantCulture)+" "+annotation.unit+(annotation.valid?"":" [stale]");
        store.Upsert(annotation);planes.Remove(annotation.id);labels[annotation.id]=text;Refresh();
    }
    public bool Handle(string[] words)
    {
        if(words==null||words.Length==0||words[0]!="cad")return false;
        try
        {
            if(disposed)throw new InvalidOperationException("Overlay is disposed.");
            if(words.Length<2)throw new ArgumentException("cad requires an operation.");
            string op=words[1];
            if(op=="status") {Exact(words,2);Refresh();return true;}
            if(op=="clear") {Exact(words,2);store.Clear();planes.Clear();labels.Clear();Status.gridEnabled=false;Refresh();return true;}
            if(op=="remove") {Exact(words,3);Id(words[2]);store.Remove(words[2]);planes.Remove(words[2]);labels.Remove(words[2]);Refresh();return true;}
            if(op=="grid")
            {
                if(words.Length==3&&words[2]=="off") {store.Remove("~grid");Status.gridEnabled=false;Refresh();return true;}
                Exact(words,6);if(words[2]!="on")throw new ArgumentException("grid requires on or off.");
                double spacing=Number(words[3]),extent=Number(words[4]);Plane(words[5]);RequireReference(words[5]);
                if(spacing<=0||extent<=0||extent/spacing>64)throw new ArgumentException("Grid requires positive metres and at most 64 cells each side.");
                int cells=(int)Math.Floor(extent/spacing);if(cells<1)throw new ArgumentException("Grid extent must include at least one cell.");
                store.Upsert(ScientificAnnotation.Grid("~grid",new ScientificVector3(0,0,0),new ScientificVector3(1,0,0),new ScientificVector3(0,1,0),spacing,cells,""));
                Status.gridEnabled=true;Status.gridSpacingMeters=spacing;Status.gridRequestedExtentMeters=extent;Status.gridExtentMeters=cells*spacing;Status.gridPlane=words[5];Refresh();return true;
            }
            if(words.Length<3)throw new ArgumentException("Annotation requires stable ID.");
            string id=words[2];Id(id);ScientificAnnotation annotation;string plane=null,text;
            if(op=="marker") {Minimum(words,6);text=Label(words,6,id);annotation=ScientificAnnotation.Marker(id,Point(words,3),text);}
            else if(op=="measure") {Minimum(words,9);text=Label(words,9,id);annotation=ScientificAnnotation.Measurement(id,Point(words,3),Point(words,6),text);text+=" "+annotation.value.ToString("G6",CultureInfo.InvariantCulture)+" m";}
            else if(op=="ring")
            {
                Minimum(words,8);double radius=Number(words[6]);if(radius<=0||radius>1e12)throw new ArgumentException("Ring radius outside supported metre bounds.");
                plane=words[7];Plane(plane);RequireReference(plane);text=Label(words,8,id);ScientificVector3 u,v;Axes(plane,null,out u,out v);
                annotation=ScientificAnnotation.Ring(id,Point(words,3),u,v,radius,text);
            }
            else if(op=="value")
            {
                Minimum(words,9);double value=Number(words[6]);string unit=words[7];if(unit.Length>24||Control(unit))throw new ArgumentException("Unit is invalid.");
                if(words[8]!="valid"&&words[8]!="stale")throw new ArgumentException("Validity must be valid or stale.");
                text=Label(words,9,id);annotation=ScientificAnnotation.Readout(id,Point(words,3),text,value,unit,words[8]=="valid");
                text+=" "+value.ToString("G6",CultureInfo.InvariantCulture)+" "+unit+(annotation.valid?"":" [stale]");
            }
            else throw new ArgumentException("Unknown cad operation.");
            store.Upsert(annotation);
            if(plane==null)planes.Remove(id);else planes[id]=plane;
            labels[id]=text;Status.lastError="";Refresh();return true;
        }
        catch(Exception e) {Status.rejectedCommands++;Status.lastError=e.Message;throw;}
    }
    void Refresh()
    {
        Status.annotations=store.Count;Status.generation=store.Generation;
        var rows=new AnnotationData[store.Count];
        for(int i=0;i<rows.Length;i++)
        {
            ScientificAnnotation item=store.At(i);string plane;bool selectedPlane=planes.TryGetValue(item.id,out plane);
            if(item.id=="~grid")plane=Status.gridPlane;
            ScientificVector3 u=item.axisU,v=item.axisV;if(plane!=null&&plane!="view")Axes(plane,null,out u,out v);
            rows[i]=new AnnotationData {id=item.id,kind=item.kind.ToString(),label=item.label,unit=item.unit,value=item.value,valid=item.valid,validity=item.valid?"valid":"stale",a=Coordinates(item.a),b=Coordinates(item.b),axisU=Coordinates(u),axisV=Coordinates(v),radius=item.radius,spacing=item.spacing,cells=item.cells,plane=plane??"explicit-axes",positionPolicy=item.id=="~grid"?"reference-relative; origin is status.referencePosition when installed, otherwise focusPosition; ship axes remain fixed while camera rotates":selectedPlane&&plane=="view"?"world-center; axes follow current scientific view right/up":"absolute-world; explicit or supplied reference plane axes"};
        }
        Status.rows=rows;
    }
    static double[] Coordinates(ScientificVector3 p) {return new[] {p.x,p.y,p.z};}
    public void Draw(ScientificView view)
    {
        if(disposed||view==null||Event.current==null||Event.current.type!=EventType.Repaint)return;
        if(style==null) {style=new GUIStyle(GUI.skin.label);style.fontSize=13;style.normal.textColor=Color.white;}
        Status.drawCalls++;Status.viewportWidth=view.width;Status.viewportHeight=view.height;Status.drawnMarkers=Status.drawnSegments=Status.drawnLabels=0;
        Color prior=GUI.color;Matrix4x4 matrix=GUI.matrix;
        try
        {
            GUI.matrix=Matrix4x4.identity;
            for(int i=0;i<store.Count;i++)
            {
                ScientificAnnotation item=store.At(i);GUI.color=item.kind==ScientificAnnotationKind.Value&&!item.valid?new Color(1,.65f,.2f,1):new Color(.2f,.95f,1,1);
                if(item.kind==ScientificAnnotationKind.Marker||item.kind==ScientificAnnotationKind.Value)
                {
                    double x,y,z;if(view.Project(item.a,out x,out y,out z)) {Stroke(x-4,y,x+4,y,1);Stroke(x,y-4,x,y+4,1);Status.drawnMarkers++;Text(item.id,item.a,view);}
                }
                else if(item.kind==ScientificAnnotationKind.Measurement||item.kind==ScientificAnnotationKind.Line)
                {WorldLine(item.a,item.b,view);Text(item.id,Scale(Add(item.a,item.b),.5),view);}
                else if(item.kind==ScientificAnnotationKind.Ring)
                {
                    string plane;ScientificVector3 u=item.axisU,v=item.axisV;if(planes.TryGetValue(item.id,out plane))Axes(plane,view,out u,out v);
                    ScientificVector3 previous=Add(item.a,Scale(u,item.radius));
                    for(int j=1;j<=RingSegments;j++) {double angle=j*2*Math.PI/RingSegments;ScientificVector3 next=Add(item.a,Add(Scale(u,item.radius*Math.Cos(angle)),Scale(v,item.radius*Math.Sin(angle))));WorldLine(previous,next,view);previous=next;}
                    Text(item.id,item.a,view);
                }
                else if(item.kind==ScientificAnnotationKind.Grid)
                {
                    ScientificVector3 origin=item.a,u=item.axisU,v=item.axisV;if(item.id=="~grid") {origin=Status.referenceInstalled?referenceOrigin:focus;Axes(Status.gridPlane,view,out u,out v);}double extent=item.cells*item.spacing;GUI.color=new Color(.35f,.65f,.85f,.4f);
                    for(int j=-item.cells;j<=item.cells;j++) {double offset=j*item.spacing;WorldLine(Add(origin,Add(Scale(u,offset),Scale(v,-extent))),Add(origin,Add(Scale(u,offset),Scale(v,extent))),view);WorldLine(Add(origin,Add(Scale(v,offset),Scale(u,-extent))),Add(origin,Add(Scale(v,offset),Scale(u,extent))),view);}
                }
            }
            if(Status.gridEnabled) {GUI.color=Color.white;GUI.Label(new Rect(450,174,Math.Max(300,view.width-470),24),"Grid: "+Status.gridParentLabel+" / "+Status.gridPlane+" / "+Status.gridSpacingMeters.ToString("G4",CultureInfo.InvariantCulture)+" m",style);}
        }
        finally {GUI.color=prior;GUI.matrix=matrix;}
    }
    void Text(string id,ScientificVector3 p,ScientificView view)
    {
        string text;if(!labels.TryGetValue(id,out text))return;double x,y,z;if(!view.Project(p,out x,out y,out z))return;
        GUI.Label(new Rect((float)x+7,(float)y-10,360,24),text,style);Status.drawnLabels++;
    }
    void WorldLine(ScientificVector3 a,ScientificVector3 b,ScientificView view)
    {
        double za=Dot(Sub(a,view.origin),view.forward),zb=Dot(Sub(b,view.origin),view.forward),lo=0,hi=1;
        if(!ClipDepth(za,zb,view.near,true,ref lo,ref hi)||!ClipDepth(za,zb,view.far,false,ref lo,ref hi))return;
        ScientificVector3 delta=Sub(b,a);ScientificVector3 aa=Add(a,Scale(delta,lo)),bb=Add(a,Scale(delta,hi));
        double ax,ay,az,bx,by,bz;if(!view.ProjectUnclipped(aa,out ax,out ay,out az)||!view.ProjectUnclipped(bb,out bx,out by,out bz))return;
        double dx=bx-ax,dy=by-ay;lo=0;hi=1;
        if(!Clip2(-dx,ax,ref lo,ref hi)||!Clip2(dx,view.width-ax,ref lo,ref hi)||!Clip2(-dy,ay,ref lo,ref hi)||!Clip2(dy,view.height-ay,ref lo,ref hi))return;
        Stroke(ax+dx*lo,ay+dy*lo,ax+dx*hi,ay+dy*hi,1.5f);Status.drawnSegments++;
    }
    static bool ClipDepth(double a,double b,double boundary,bool minimum,ref double lo,ref double hi)
    {
        double d=b-a;if(d==0)return minimum?a>=boundary:a<=boundary;
        double t=(boundary-a)/d;if((d>0)==minimum)lo=Math.Max(lo,t);else hi=Math.Min(hi,t);return lo<=hi;
    }
    static bool Clip2(double p,double q,ref double lo,ref double hi)
    {if(p==0)return q>=0;double t=q/p;if(p<0)lo=Math.Max(lo,t);else hi=Math.Min(hi,t);return lo<=hi;}
    static void Stroke(double ax,double ay,double bx,double by,float width)
    {
        float x=(float)ax,y=(float)ay,dx=(float)(bx-ax),dy=(float)(by-ay);float length=Mathf.Sqrt(dx*dx+dy*dy);if(length==0)return;
        Matrix4x4 prior=GUI.matrix;try {GUIUtility.RotateAroundPivot(Mathf.Atan2(dy,dx)*Mathf.Rad2Deg,new Vector2(x,y));GUI.DrawTexture(new Rect(x,y-width*.5f,length,width),Texture2D.whiteTexture);}finally{GUI.matrix=prior;}
    }
    static ScientificVector3 Add(ScientificVector3 a,ScientificVector3 b) {return new ScientificVector3(a.x+b.x,a.y+b.y,a.z+b.z);}
    static ScientificVector3 Sub(ScientificVector3 a,ScientificVector3 b) {return new ScientificVector3(a.x-b.x,a.y-b.y,a.z-b.z);}
    static ScientificVector3 Scale(ScientificVector3 a,double s) {return new ScientificVector3(a.x*s,a.y*s,a.z*s);}
    static double Dot(ScientificVector3 a,ScientificVector3 b) {return a.x*b.x+a.y*b.y+a.z*b.z;}
    void Axes(string plane,ScientificView view,out ScientificVector3 u,out ScientificVector3 v)
    {
        if(plane=="view"&&view!=null) {u=view.right;v=view.up;return;}
        if(plane.StartsWith("ship-",StringComparison.Ordinal)) {RequireReference(plane);u=plane=="ship-yz"?referenceUp:referenceRight;v=plane=="ship-xy"?referenceUp:referenceForward;return;}
        u=plane=="yz"?new ScientificVector3(0,1,0):new ScientificVector3(1,0,0);v=plane=="xz"?new ScientificVector3(0,0,1):plane=="yz"?new ScientificVector3(0,0,1):new ScientificVector3(0,1,0);
    }
    void RequireReference(string plane) {if(plane.StartsWith("ship-",StringComparison.Ordinal)&&!Status.referenceInstalled)throw new InvalidOperationException("Ship reference axes have not been supplied.");}
    static ScientificVector3 Point(string[] w,int i) {return new ScientificVector3(Number(w[i]),Number(w[i+1]),Number(w[i+2]));}
    static double Number(string s) {double n;if(!double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out n))throw new ArgumentException("Number is invalid.");Finite(n);if(Math.Abs(n)>1e15)throw new ArgumentException("Number exceeds scientific coordinate bounds.");return n;}
    static void Finite(double n) {if(double.IsNaN(n)||double.IsInfinity(n))throw new ArgumentException("Finite number required.");}
    static void Plane(string s) {if(s!="view"&&s!="xy"&&s!="xz"&&s!="yz"&&s!="ship-xy"&&s!="ship-xz"&&s!="ship-yz")throw new ArgumentException("Plane must be view, xy, xz, yz, ship-xy, ship-xz or ship-yz.");}
    static void Id(string s) {if(string.IsNullOrEmpty(s)||s.Length>64)throw new ArgumentException("Stable ID is invalid.");foreach(char c in s)if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='-'||c=='.'||c==':'))throw new ArgumentException("Stable ID contains unsupported characters.");}
    static bool Control(string s) {foreach(char c in s)if(char.IsControl(c))return true;return false;}
    static string Label(string[] w,int start,string fallback) {string s=start==w.Length?fallback:string.Join(" ",w,start,w.Length-start);if(s.Length>160||Control(s))throw new ArgumentException("Label is invalid.");return s;}
    static void Exact(string[] w,int n) {if(w.Length!=n)throw new ArgumentException("Command argument count differs.");}
    static void Minimum(string[] w,int n) {if(w.Length<n)throw new ArgumentException("Command arguments are incomplete.");}
    public void Dispose() {if(disposed)return;disposed=true;store.Clear();planes.Clear();labels.Clear();Status.gridEnabled=false;Status.status="disposed";Refresh();}
}
