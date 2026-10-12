using System;
using System.Globalization;
using System.IO;
using KspContinuum;
using UnityEngine;

public sealed class ScientificCameraControls
{
    public sealed class Anchor
    {
        public string kind,id,label; public ScientificVector3 position; public Quaternion basis; public bool axesAvailable;
        public Anchor(string kind,string id,string label,ScientificVector3 position,Quaternion basis,bool axesAvailable=true)
        { this.kind=kind;this.id=id;this.label=label;this.position=position;this.basis=basis;this.axesAvailable=axesAvailable; }
    }
    [Serializable] public sealed class Option
    { public string kind,id,label;public bool axesAvailable; }
    [Serializable] public sealed class StatusData
    {
        public string schema="continuum-scientific-camera-controls/v1",focusKind,focusId,focusLabel,frameKind,frameId,frameLabel,axesKind,axesId,axesLabel,rotationPolicy="world-up orbit",lastError;
        public bool frameWorldAxes,focusAxesAvailable,controlsEnabled;
        public float[] rotation=new float[4]; public double[] focusPosition=new double[3],framePosition=new double[3];
        public Option[] options=new Option[0]; public long generation;
    }
    public readonly StatusData Status=new StatusData();
    Anchor[] anchors=new Anchor[0]; Anchor focus,frame,axisFrame; Quaternion rotation=Quaternion.identity; bool dragging; int dragButton;
    public Quaternion Rotation {get{return rotation;}}
    public Anchor Focus {get{return focus;}}
    public Anchor GridReference {get{return frame;}}
    public Anchor AxisReference {get{return axisFrame;}}
    public Quaternion GridBasis {get{return Status.frameWorldAxes?Quaternion.identity:axisFrame.basis;}}
    public string GridLabel {get{return frame.label+" / axes: "+(Status.frameWorldAxes?"world":axisFrame.label);}}
    public bool Initialized {get{return focus!=null;}}
    public void ValidateReplacement(Anchor[] next)
    {
        Validate(next);
        if(focus!=null&&Find(next,focus.kind,focus.id)==null)throw new InvalidDataException("Reload removes selected camera focus "+focus.kind+" "+focus.id+".");
        Anchor replacement=frame==null?null:Find(next,frame.kind,frame.id);
        if(frame!=null&&replacement==null)throw new InvalidDataException("Reload removes selected grid parent "+frame.kind+" "+frame.id+".");
        if(axisFrame!=null&&!Status.frameWorldAxes) {Anchor axes=Find(next,axisFrame.kind,axisFrame.id);if(axes==null||!axes.axesAvailable)throw new InvalidDataException("Reload removes selected grid axes "+axisFrame.kind+" "+axisFrame.id+".");}
    }
    public void SetAnchors(Anchor[] next,string preferredId)
    {
        ValidateReplacement(next);Anchor newFocus=focus==null?null:Find(next,focus.kind,focus.id),newFrame=frame==null?null:Find(next,frame.kind,frame.id),newAxes=axisFrame==null?null:Find(next,axisFrame.kind,axisFrame.id);
        if(newFocus==null) {foreach(Anchor a in next)if(a.id==preferredId) {if(newFocus!=null)throw new InvalidDataException("Default camera anchor ID ambiguous.");newFocus=a;}if(newFocus==null)throw new InvalidDataException("Default camera anchor unavailable.");}
        anchors=next;
        focus=newFocus;frame=newFrame??focus;axisFrame=newAxes;
        if(axisFrame==null) {if(frame.axesAvailable)axisFrame=frame;else foreach(Anchor a in anchors)if(a.axesAvailable) {axisFrame=a;break;}if(axisFrame==null)Status.frameWorldAxes=true;}
        Status.options=new Option[anchors.Length];for(int i=0;i<anchors.Length;i++)Status.options[i]=new Option {kind=anchors[i].kind,id=anchors[i].id,label=anchors[i].label,axesAvailable=anchors[i].axesAvailable};
        Status.generation++;Refresh();
    }
    public void Upsert(Anchor anchor)
    {
        ValidateAnchor(anchor);Anchor existing=Find(anchors,anchor.kind,anchor.id);
        if(existing!=null) {if(existing==axisFrame&&!Status.frameWorldAxes&&!anchor.axesAvailable)throw new InvalidDataException("Selected grid axes unavailable.");existing.position=anchor.position;existing.basis=anchor.basis;existing.axesAvailable=anchor.axesAvailable;Refresh();return;}
        if(anchors.Length==512)throw new InvalidDataException("Camera anchor capacity exceeded.");
        var next=new Anchor[anchors.Length+1];Array.Copy(anchors,next,anchors.Length);next[next.Length-1]=anchor;SetAnchors(next,focus.id);
    }
    public void UpdateAnchor(string kind,string id,ScientificVector3 position,Quaternion basis,bool axesAvailable)
    {
        Anchor a=Find(anchors,kind,id);if(a==null)throw new InvalidDataException("Camera anchor update identity absent.");
        Position(position);if(axesAvailable)basis=Normalized(basis);
        if(a==axisFrame&&!Status.frameWorldAxes&&!axesAvailable)throw new InvalidDataException("Selected grid axes unavailable.");
        a.position=position;a.basis=basis;a.axesAvailable=axesAvailable;Refresh();
    }
    public void SetRotation(Quaternion value,string policy)
    {rotation=Normalized(value);Status.rotationPolicy=policy;Refresh();}
    public void SetWorldUpOrbit(float yaw,float pitch)
    {
        Scalar(yaw);Scalar(pitch);Quaternion q=Quaternion.Euler(Mathf.Clamp(pitch,-89,89),yaw,0);
        SetRotation(Quaternion.LookRotation(q*Vector3.forward,Vector3.up),"world-up orbit; roll reset");
    }
    public bool Handle(string[] words)
    {
        if(words.Length==0||words[0]!="camera")return false;
        try
        {
            if(!Initialized)throw new InvalidOperationException("Camera anchors not bound.");
            if(words.Length==3&&words[1]=="snap")
            {
                if(!Status.frameWorldAxes&&(axisFrame==null||!axisFrame.axesAvailable))throw new InvalidDataException("Selected reference axes unavailable.");
                Vector3 axis;switch(words[2]) {case "+x":axis=Vector3.right;break;case "-x":axis=Vector3.left;break;case "+y":axis=Vector3.up;break;case "-y":axis=Vector3.down;break;case "+z":axis=Vector3.forward;break;case "-z":axis=Vector3.back;break;default:throw new InvalidDataException("camera snap +x|-x|+y|-y|+z|-z");}
                Quaternion basis=GridBasis;Vector3 forward=basis*(-axis),up=basis*(Math.Abs(axis.y)>.5f?Vector3.forward:Vector3.up);
                SetRotation(Quaternion.LookRotation(forward,up),"reference-axis snap "+words[2]+" / "+GridLabel);
            }
            else if((words.Length==4||words.Length==5)&&words[1]=="drag")
            {
                float dx=Number(words[2]),dy=Number(words[3]),roll=words.Length==5?Number(words[4]):0;
                if(Math.Abs(dx)>4096||Math.Abs(dy)>4096||Math.Abs(roll)>360)throw new InvalidDataException("Camera drag delta/roll bound exceeded.");
                SetRotation(rotation*Quaternion.AngleAxis(-dx*.25f,Vector3.up)*Quaternion.AngleAxis(dy*.25f,Vector3.right)*Quaternion.AngleAxis(roll,Vector3.forward),"quaternion drag; roll retained");
            }
            else if((words.Length==3||words.Length==4)&&(words[1]=="focus"||words[1]=="frame"||words[1]=="axes"))
            {
                if((words[1]=="frame"||words[1]=="axes")&&words[2]=="world"&&words.Length==3)Status.frameWorldAxes=true;
                else
                {
                    Anchor selected=Select(words[2],words.Length==4?words[3]:null);
                    if(words[1]=="focus")focus=selected;
                    else if(words[1]=="axes") {if(!selected.axesAvailable)throw new InvalidDataException("Selected axes unavailable: "+selected.label);axisFrame=selected;Status.frameWorldAxes=false;}
                    else {frame=selected;if(selected.axesAvailable) {axisFrame=selected;Status.frameWorldAxes=false;}}
                }
                Refresh();
            }
            else throw new InvalidDataException("camera snap AXIS | camera drag DX DY [ROLL_DEGREES] | camera focus KIND [ID] | camera frame KIND [ID] | camera axes world|KIND [ID]");
            Status.lastError=null;return true;
        }
        catch(Exception e) {Status.lastError=e.Message;throw;}
    }
    Anchor Select(string kind,string id)
    {
        if(kind=="vessel")kind="vessel-origin";
        Anchor found=null;foreach(Anchor a in anchors)if(a.kind==kind&&(id==null||a.id==id)) {if(found!=null)throw new InvalidDataException("Camera anchor kind is ambiguous; supply ID.");found=a;}
        if(found==null)throw new InvalidDataException("Requested camera anchor unavailable: "+kind+(id==null?"":" "+id));return found;
    }
    public void Refresh()
    {
        Status.rotation[0]=rotation.x;Status.rotation[1]=rotation.y;Status.rotation[2]=rotation.z;Status.rotation[3]=rotation.w;
        if(focus==null)return;Status.focusKind=focus.kind;Status.focusId=focus.id;Status.focusLabel=focus.label;Status.focusAxesAvailable=focus.axesAvailable;
        Status.frameKind=frame.kind;Status.frameId=frame.id;Status.frameLabel=GridLabel;
        Status.axesKind=Status.frameWorldAxes?"world":axisFrame.kind;Status.axesId=Status.frameWorldAxes?"world":axisFrame.id;Status.axesLabel=Status.frameWorldAxes?"World axes":axisFrame.label;
        Copy(focus.position,Status.focusPosition);Copy(frame.position,Status.framePosition);
    }
    public void Draw(Rect area,bool allowed,bool orthoSupported,bool orthographic,float fov,float span,Action<string[]> execute)
    {
        Status.controlsEnabled=allowed;if(!allowed)dragging=false;bool prior=GUI.enabled;GUI.enabled=allowed;
        try
        {
            GUI.Box(area,"Scientific camera");float x=area.x+8,y=area.y+24,w=area.width-16;
            GUI.Label(new Rect(x,y,w,20),"Focus: "+Status.focusLabel);y+=22;
            if(GUI.Button(new Rect(x,y,w*.5f-2,22),"Focus previous"))Cycle(false,-1,execute);
            if(GUI.Button(new Rect(x+w*.5f+2,y,w*.5f-2,22),"Focus next"))Cycle(false,1,execute);y+=26;
            GUI.Label(new Rect(x,y,w,20),"Grid: "+Status.frameLabel);y+=22;
            if(GUI.Button(new Rect(x,y,w/3-2,22),"Grid prev"))Cycle(true,-1,execute);
            if(GUI.Button(new Rect(x+w/3,y,w/3-2,22),"Grid next"))Cycle(true,1,execute);
            if(GUI.Button(new Rect(x+2*w/3,y,w/3,22),"World axes"))execute(new[] {"camera","frame","world"});y+=28;
            bool normal=GUI.enabled;GUI.enabled=normal&&(Status.frameWorldAxes||(axisFrame!=null&&axisFrame.axesAvailable));
            string[] axes={"+x","-x","+y","-y","+z","-z"};for(int i=0;i<6;i++)if(GUI.Button(new Rect(x+(i%3)*w/3,y+(i/3)*24,w/3-2,22),axes[i]))execute(new[] {"camera","snap",axes[i]});
            GUI.enabled=normal;y+=52;
            if(GUI.Button(new Rect(x,y,w*.5f-2,22),"Perspective"))execute(new[] {"projection","perspective",fov.ToString("R",CultureInfo.InvariantCulture)});
            GUI.enabled=normal&&orthoSupported;
            if(GUI.Button(new Rect(x+w*.5f+2,y,w*.5f-2,22),"Ortho"))execute(new[] {"projection","ortho",Math.Max(.001f,span).ToString("R",CultureInfo.InvariantCulture)});
            GUI.enabled=normal;y+=28;
            Rect drag=new Rect(x,y,w,64);GUI.Box(drag,"Drag here to rotate\nShift + drag: roll");Event e=Event.current;
            if(allowed&&e.type==EventType.MouseDown&&e.button==0&&drag.Contains(e.mousePosition)) {dragging=true;dragButton=e.button;e.Use();}
            if(dragging&&e.type==EventType.MouseDrag&&e.button==dragButton) {execute(new[] {"camera","drag",(e.shift?0:e.delta.x).ToString("R",CultureInfo.InvariantCulture),(e.shift?0:e.delta.y).ToString("R",CultureInfo.InvariantCulture),(e.shift?-e.delta.x*.25f:0).ToString("R",CultureInfo.InvariantCulture)});e.Use();}
            if(dragging&&e.type==EventType.MouseUp&&e.button==dragButton) {dragging=false;e.Use();}
            if(!string.IsNullOrEmpty(Status.lastError))GUI.Label(new Rect(x,y+66,w,40),Status.lastError);
        }
        finally {GUI.enabled=prior;}
    }
    void Cycle(bool grid,int direction,Action<string[]> execute)
    {
        Anchor selected=grid?frame:focus;int index=Array.IndexOf(anchors,selected);Anchor a=anchors[(index+direction+anchors.Length)%anchors.Length];execute(new[] {"camera",grid?"frame":"focus",a.kind,a.id});
    }
    public static bool TryBasis(float[] matrix,out Quaternion basis)
    {
        basis=Quaternion.identity;if(matrix==null||matrix.Length!=16)return false;
        return TryBasis(new Vector3(matrix[0],matrix[4],matrix[8]),new Vector3(matrix[1],matrix[5],matrix[9]),new Vector3(matrix[2],matrix[6],matrix[10]),out basis);
    }
    public static bool TryBasis(Vector3 right,Vector3 up,Vector3 forward,out Quaternion basis)
    {
        basis=Quaternion.identity;if(!VectorFinite(right)||!VectorFinite(up)||!VectorFinite(forward)||right.sqrMagnitude<1e-12f||up.sqrMagnitude<1e-12f||forward.sqrMagnitude<1e-12f)return false;
        right.Normalize();up.Normalize();forward.Normalize();
        if(Math.Abs(Vector3.Dot(right,up))>1e-4f||Math.Abs(Vector3.Dot(right,forward))>1e-4f||Math.Abs(Vector3.Dot(up,forward))>1e-4f||Vector3.Dot(Vector3.Cross(right,up),forward)<.9999f)return false;
        basis=Normalized(Quaternion.LookRotation(forward,up));return true;
    }
    static void Validate(Anchor[] rows)
    {if(rows==null||rows.Length<1||rows.Length>512)throw new InvalidDataException("Camera anchor bound1..512.");for(int i=0;i<rows.Length;i++) {ValidateAnchor(rows[i]);for(int j=0;j<i;j++)if(rows[i].kind==rows[j].kind&&rows[i].id==rows[j].id)throw new InvalidDataException("Duplicate camera anchor.");}}
    static void ValidateAnchor(Anchor a)
    {if(a==null||string.IsNullOrEmpty(a.kind)||string.IsNullOrEmpty(a.id)||string.IsNullOrEmpty(a.label))throw new InvalidDataException("Camera anchor identity absent.");Position(a.position);if(a.axesAvailable)a.basis=Normalized(a.basis);}
    static Anchor Find(Anchor[] rows,string kind,string id) {foreach(Anchor a in rows)if(a.kind==kind&&a.id==id)return a;return null;}
    static Quaternion Normalized(Quaternion q)
    {Scalar(q.x);Scalar(q.y);Scalar(q.z);Scalar(q.w);double n=Math.Sqrt((double)q.x*q.x+(double)q.y*q.y+(double)q.z*q.z+(double)q.w*q.w);if(n<1e-12)throw new InvalidDataException("Camera quaternion degenerate.");return new Quaternion((float)(q.x/n),(float)(q.y/n),(float)(q.z/n),(float)(q.w/n));}
    static bool VectorFinite(Vector3 v) {return Finite(v.x)&&Finite(v.y)&&Finite(v.z);}
    static bool Finite(double n) {return !double.IsNaN(n)&&!double.IsInfinity(n);}
    static void Scalar(float n) {if(!Finite(n))throw new InvalidDataException("Camera scalar nonfinite.");}
    static void Position(ScientificVector3 v) {if(!Finite(v.x)||!Finite(v.y)||!Finite(v.z)||Math.Abs(v.x)>1e15||Math.Abs(v.y)>1e15||Math.Abs(v.z)>1e15)throw new InvalidDataException("Camera anchor coordinate bound.");}
    static float Number(string s) {float n;if(!float.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out n)||!Finite(n))throw new InvalidDataException("Finite camera number required.");return n;}
    static void Copy(ScientificVector3 v,double[] destination) {destination[0]=v.x;destination[1]=v.y;destination[2]=v.z;}
}
