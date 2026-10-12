using System;
namespace KspContinuum
{
    public enum ScientificAnnotationKind { Marker, Value, Line, Measurement, Ring, Grid }
    public sealed class ScientificAnnotation
    {
        public readonly string id,label,unit;
        public readonly ScientificAnnotationKind kind;
        public readonly ScientificVector3 a,b,axisU,axisV;
        public readonly double value,radius,spacing;
        public readonly bool valid;
        public readonly int cells;
        static void Text(string text,int maximum,bool required)
        {ScientificView.Need(text!=null&&text.Length<=maximum&&(!required||text.Length>0),"Annotation text bound.");foreach(char c in text)ScientificView.Need(!char.IsControl(c),"Annotation text contains control characters.");}
        static void Extent(ScientificVector3 center,ScientificVector3 u,ScientificVector3 v,double extent)
        {ScientificView.CheckVector(center);ScientificView.Need(Math.Abs(center.x)+extent*(Math.Abs(u.x)+Math.Abs(v.x))<=1e15&&Math.Abs(center.y)+extent*(Math.Abs(u.y)+Math.Abs(v.y))<=1e15&&Math.Abs(center.z)+extent*(Math.Abs(u.z)+Math.Abs(v.z))<=1e15,"Annotation geometry exceeds world domain.");}
        ScientificAnnotation(string id,ScientificAnnotationKind kind,ScientificVector3 a,ScientificVector3 b,ScientificVector3 u,ScientificVector3 v,string label,string unit,double value,bool valid,double radius,double spacing,int cells)
        {
            Text(id,128,true);Text(label,256,false);Text(unit,32,false);ScientificView.CheckVector(a);ScientificView.CheckVector(b);
            ScientificView.Need(ScientificView.Finite(value)&&ScientificView.Finite(radius)&&ScientificView.Finite(spacing),"Finite annotation scalars required.");
            this.id=id;this.kind=kind;this.a=a;this.b=b;axisU=u;axisV=v;this.label=label;this.unit=unit;this.value=value;this.valid=valid;this.radius=radius;this.spacing=spacing;this.cells=cells;
        }
        public static ScientificAnnotation Marker(string id,ScientificVector3 position,string label)
        {return new ScientificAnnotation(id,ScientificAnnotationKind.Marker,position,new ScientificVector3(),new ScientificVector3(),new ScientificVector3(),label,"",0,true,0,0,0);}
        public static ScientificAnnotation Readout(string id,ScientificVector3 position,string label,double value,string unit,bool valid)
        {return new ScientificAnnotation(id,ScientificAnnotationKind.Value,position,new ScientificVector3(),new ScientificVector3(),new ScientificVector3(),label,unit,value,valid,0,0,0);}
        public static ScientificAnnotation Line(string id,ScientificVector3 a,ScientificVector3 b,string label)
        {return new ScientificAnnotation(id,ScientificAnnotationKind.Line,a,b,new ScientificVector3(),new ScientificVector3(),label,"",0,true,0,0,0);}
        public static ScientificAnnotation Measurement(string id,ScientificVector3 a,ScientificVector3 b,string label)
        {ScientificView.CheckVector(a);ScientificView.CheckVector(b);return new ScientificAnnotation(id,ScientificAnnotationKind.Measurement,a,b,new ScientificVector3(),new ScientificVector3(),label,"m",ScientificVector3.Distance(a,b),true,0,0,0);}
        public static ScientificAnnotation Ring(string id,ScientificVector3 center,ScientificVector3 axisU,ScientificVector3 axisV,double radius,string label)
        {ScientificView.CheckAxes(axisU,axisV);ScientificView.Need(ScientificView.Finite(radius)&&radius>0&&radius<=1e12,"Ring radius bound.");Extent(center,axisU,axisV,radius);return new ScientificAnnotation(id,ScientificAnnotationKind.Ring,center,new ScientificVector3(),axisU,axisV,label,"m",0,true,radius,0,0);}
        public static ScientificAnnotation Grid(string id,ScientificVector3 origin,ScientificVector3 axisU,ScientificVector3 axisV,double spacing,int cells,string label)
        {ScientificView.CheckAxes(axisU,axisV);ScientificView.Need(ScientificView.Finite(spacing)&&spacing>0&&spacing<=1e12&&cells>=1&&cells<=128,"Grid spacing/cell bound.");Extent(origin,axisU,axisV,spacing*cells);return new ScientificAnnotation(id,ScientificAnnotationKind.Grid,origin,new ScientificVector3(),axisU,axisV,label,"m",0,true,0,spacing,cells);}
        public ScientificVector3 RingPoint(double angleRadians)
        {
            ScientificView.Need(kind==ScientificAnnotationKind.Ring&&ScientificView.Finite(angleRadians),"Ring query invalid.");double x=radius*Math.Cos(angleRadians),y=radius*Math.Sin(angleRadians);
            return new ScientificVector3(a.x+axisU.x*x+axisV.x*y,a.y+axisU.y*x+axisV.y*y,a.z+axisU.z*x+axisV.z*y);
        }
    }
    public sealed class AnnotationStore
    {
        readonly ScientificAnnotation[] items;
        public int Count {get;private set;}
        public int Capacity {get{return items.Length;}}
        public long Generation {get;private set;}
        public AnnotationStore(int capacity=256) {ScientificView.Need(capacity>=1&&capacity<=4096,"Annotation capacity 1..4096 required.");items=new ScientificAnnotation[capacity];}
        public ScientificAnnotation At(int index) {if(index<0||index>=Count)throw new ArgumentOutOfRangeException("index");return items[index];}
        public ScientificAnnotation Find(string id) {for(int i=0;i<Count;i++)if(items[i].id==id)return items[i];return null;}
        public long Upsert(ScientificAnnotation annotation)
        {
            if(annotation==null)throw new ArgumentNullException("annotation");int index=0;while(index<Count&&items[index].id!=annotation.id)index++;
            ScientificView.Need(index<Count||Count<Capacity,"Annotation capacity exhausted.");ScientificView.Need(Generation<long.MaxValue,"Annotation generation exhausted.");
            items[index]=annotation;if(index==Count)Count++;return ++Generation;
        }
        public long Remove(string id)
        {
            ScientificView.Need(!string.IsNullOrEmpty(id)&&id.Length<=128,"Annotation ID required.");foreach(char c in id)ScientificView.Need(!char.IsControl(c),"Annotation ID contains controls.");
            int index=0;while(index<Count&&items[index].id!=id)index++;if(index==Count)return Generation;ScientificView.Need(Generation<long.MaxValue,"Annotation generation exhausted.");
            for(int i=index;i+1<Count;i++)items[i]=items[i+1];items[--Count]=null;return ++Generation;
        }
        public long Clear() {if(Count==0)return Generation;ScientificView.Need(Generation<long.MaxValue,"Annotation generation exhausted.");Array.Clear(items,0,Count);Count=0;return ++Generation;}
    }
}
