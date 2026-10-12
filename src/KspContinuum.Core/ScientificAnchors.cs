using System;
using System.Collections.Generic;
using System.IO;
namespace KspContinuum
{
    [Serializable] public sealed class ScientificAnchorData
    {
        public string id,kind,label,source,nodeId;
        public uint partFlightId;
        public bool axesAvailable;
        public double[] offsetMeters,right,up,forward;
    }
    [Serializable] public sealed class ScientificAnchorSet
    {
        public string schema="continuum-scientific-anchors/v1",runId,vesselId,bodyId,sourceFrame="body-centred-world-axes",preferredId;
        public string offsetConvention="translation-only-world-axes-relative-vessel-origin",scope="Capture-epoch frozen offsets and axes; translation-only replay, not live rotation or deformation tracking";
        public int processId;
        public double epochSeconds;
        public ScientificAnchorData[] anchors;
    }
    public sealed class ScientificAnchor
    {
        public readonly string id,kind,label,source,nodeId;
        public readonly uint partFlightId;
        public readonly bool axesAvailable;
        public readonly ScientificVector3 offsetMeters,right,up,forward;
        internal ScientificAnchor(ScientificAnchorData d)
        {id=d.id;kind=d.kind;label=d.label;source=d.source;nodeId=d.nodeId;partFlightId=d.partFlightId;axesAvailable=d.axesAvailable;offsetMeters=ScientificAnchors.Vector(d.offsetMeters);right=axesAvailable?ScientificAnchors.Vector(d.right):new ScientificVector3();up=axesAvailable?ScientificAnchors.Vector(d.up):new ScientificVector3();forward=axesAvailable?ScientificAnchors.Vector(d.forward):new ScientificVector3();}
    }
    public sealed class ScientificAnchorCatalog
    {
        readonly ScientificAnchor[] items;
        public readonly string runId,vesselId,bodyId,sourceFrame,preferredId;
        public readonly int processId;
        public readonly double epochSeconds;
        internal ScientificAnchorCatalog(ScientificAnchorSet s,ScientificAnchor[] items) {this.items=items;runId=s.runId;vesselId=s.vesselId;bodyId=s.bodyId;sourceFrame=s.sourceFrame;preferredId=s.preferredId;processId=s.processId;epochSeconds=s.epochSeconds;}
        public int Count {get{return items.Length;}}
        public ScientificAnchor At(int index) {return items[index];}
        public ScientificAnchor Find(string id) {for(int i=0;i<items.Length;i++)if(items[i].id==id)return items[i];return null;}
        public ScientificAnchor Preferred {get{return Find(preferredId);}}
    }
    public static class ScientificAnchors
    {
        static void Need(bool b,string reason) {if(!b)throw new InvalidDataException(reason);}
        static void Text(string s) {Need(!string.IsNullOrEmpty(s)&&s.Length<=256,"Anchor identity absent/oversize.");foreach(char c in s)Need(!char.IsControl(c),"Anchor identity contains controls.");}
        internal static ScientificVector3 Vector(double[] a) {Need(a!=null&&a.Length==3,"Anchor vector shape differs.");var v=new ScientificVector3(a[0],a[1],a[2]);try{ScientificView.CheckVector(v);}catch(ArgumentException e){throw new InvalidDataException("Anchor vector invalid.",e);}return v;}
        public static ScientificAnchorCatalog Admit(ScientificAnchorSet set,string runId,int processId,string vesselId,string bodyId,string sourceFrame)
        {
            Need(set!=null&&set.schema=="continuum-scientific-anchors/v1","Anchor schema differs.");
            Text(set.runId);Text(set.vesselId);Text(set.bodyId);Text(set.preferredId);
            Need(set.runId==runId&&set.processId==processId&&processId>0&&set.vesselId==vesselId&&set.bodyId==bodyId&&set.sourceFrame==sourceFrame&&sourceFrame=="body-centred-world-axes","Anchor source owner differs.");
            Need(set.offsetConvention=="translation-only-world-axes-relative-vessel-origin"&&ScientificView.Finite(set.epochSeconds),"Anchor convention/epoch differs.");
            Need(set.anchors!=null&&set.anchors.Length>0&&set.anchors.Length<=256,"Anchor capacity differs.");
            var ids=new HashSet<string>(StringComparer.Ordinal);var singleKinds=new HashSet<string>(StringComparer.Ordinal);var items=new ScientificAnchor[set.anchors.Length];bool origin=false,preferred=false;string controlId=null,rootId=null,originId=null;
            for(int i=0;i<items.Length;i++)
            {
                var d=set.anchors[i];Need(d!=null,"Null anchor row.");Text(d.id);Text(d.label);Text(d.source);Need(ids.Add(d.id),"Duplicate anchor identity.");
                Need(d.kind=="control"||d.kind=="root-part"||d.kind=="center-of-mass"||d.kind=="vessel-origin"||d.kind=="attachment-node","Unknown anchor kind.");
                if(d.kind!="attachment-node")Need(singleKinds.Add(d.kind),"Duplicate singleton anchor kind.");
                Need((d.kind=="control"||d.kind=="root-part"||d.kind=="attachment-node")?d.partFlightId>0:d.partFlightId==0,"Anchor part owner differs.");
                if(d.kind=="attachment-node")Text(d.nodeId);else Need(string.IsNullOrEmpty(d.nodeId),"Unexpected anchor node owner.");
                var offset=Vector(d.offsetMeters);
                if(d.kind=="vessel-origin") {Need(!origin&&offset.x==0&&offset.y==0&&offset.z==0,"Vessel origin must be unique zero offset.");origin=true;originId=d.id;}
                if(d.kind=="control")controlId=d.id;if(d.kind=="root-part")rootId=d.id;
                if(d.axesAvailable)
                {
                    var r=Vector(d.right);var u=Vector(d.up);var f=Vector(d.forward);
                    try {ScientificView.CheckAxes(r,u);ScientificView.CheckAxes(r,f);ScientificView.CheckAxes(u,f);}catch(ArgumentException e){throw new InvalidDataException("Anchor axes are not orthonormal.",e);}
                    var cross=new ScientificVector3(r.y*u.z-r.z*u.y,r.z*u.x-r.x*u.z,r.x*u.y-r.y*u.x);Need(ScientificVector3.Dot(cross,f)>1-1e-6,"Anchor axes are not right handed.");
                }
                else Need(d.right==null&&d.up==null&&d.forward==null,"Unavailable axes must not be fabricated.");
                if(d.id==set.preferredId) {Need(d.kind=="control"||d.kind=="root-part"||d.kind=="vessel-origin","Preferred anchor is not a control/root/origin.");preferred=true;}
                items[i]=new ScientificAnchor(d);
            }
            Need(origin&&preferred&&set.preferredId==(controlId??rootId??originId),"Required origin/preferred priority differs.");return new ScientificAnchorCatalog(set,items);
        }
        public static ScientificVector3 Resolve(ScientificAnchor anchor,ScientificVector3 currentVesselOrigin)
        {
            if(anchor==null)throw new ArgumentNullException("anchor");ScientificView.CheckVector(currentVesselOrigin);
            var result=new ScientificVector3(currentVesselOrigin.x+anchor.offsetMeters.x,currentVesselOrigin.y+anchor.offsetMeters.y,currentVesselOrigin.z+anchor.offsetMeters.z);ScientificView.CheckVector(result);return result;
        }
    }
}
