using System;
using KspContinuum;
static class Program
{
    static ScientificVector3 V(double x,double y,double z) {return new ScientificVector3(x,y,z);}
    static void Need(bool condition) {if(!condition)throw new Exception("Scientific view independent check failed.");}
    static void Near(double a,double b) {Need(!double.IsNaN(a)&&Math.Abs(a-b)<=1e-9);}
    static void Reject(Action action) {try{action();}catch(ArgumentException){return;}throw new Exception("Invalid input accepted.");}
    static ScientificView View(ScientificProjectionKind kind,ScientificVector3 origin)
    {return new ScientificView(origin,V(1,0,0),V(0,1,0),V(0,0,1),800,600,kind,90,20,1,101);}
    static void Main()
    {
        var perspective=View(ScientificProjectionKind.Perspective,V(0,0,0));double x,y,z;
        Need(perspective.Project(V(2,1,10),out x,out y,out z));Near(x,460);Near(y,270);Near(z,10);
        ScientificVector3 world;Need(perspective.Unproject(x,y,z,out world));Near(world.x,2);Near(world.y,1);Near(world.z,10);
        Near(perspective.PixelsPerMeter(10),30);Near(perspective.PixelsPerMeter(20),15);
        Need(!perspective.Project(V(0,0,-1),out x,out y,out z));Need(!perspective.Project(V(0,0,.5),out x,out y,out z));Need(!perspective.Project(V(0,0,102),out x,out y,out z));
        Need(perspective.ProjectUnclipped(V(100,0,10),out x,out y,out z));Near(x,3400);Need(!perspective.Project(V(100,0,10),out x,out y,out z));
        var ortho=View(ScientificProjectionKind.Orthographic,V(0,0,0));Need(ortho.Project(V(2,1,10),out x,out y,out z));Near(x,460);Near(y,270);Need(ortho.Project(V(2,1,20),out x,out y,out z));Near(x,460);Near(y,270);
        Need(ortho.Unproject(x,y,z,out world));Near(world.x,2);Near(world.y,1);Near(world.z,20);Near(ortho.PixelsPerMeter(10),30);Near(ortho.PixelsPerMeter(100),30);
        Near(perspective.ChordDepth(.5,2,10),10d/3);Near(ortho.ChordDepth(.5,2,10),6);
        Near(ortho.DepthFromDevice(.25,false),26);Near(ortho.DepthFromDevice(.75,true),26);Near(perspective.DepthFromDevice(0,false),1);Near(perspective.DepthFromDevice(1,false),101);Near(perspective.DepthFromDevice(1,true),1);
        var shifted=View(ScientificProjectionKind.Orthographic,V(1e9,-1e9,1e9));Need(shifted.Project(V(1e9+2,-1e9+1,1e9+10),out x,out y,out z));Near(x,460);Near(y,270);
        var turned=new ScientificView(V(0,0,0),V(0,0,-1),V(0,1,0),V(1,0,0),800,600,ScientificProjectionKind.Orthographic,90,20,1,101);Need(turned.Project(V(10,1,-2),out x,out y,out z));Near(x,460);Near(y,270);Near(z,10);
        Reject(delegate{new ScientificView(V(0,0,0),V(1,0,0),V(0,1,0),V(0,0,-1),800,600,ScientificProjectionKind.Orthographic,90,20,1,101);});
        Reject(delegate{new ScientificView(V(0,0,0),V(1,0,0),V(0,1,0),V(0,0,1),800,600,ScientificProjectionKind.Orthographic,90,0,1,101);});
        Reject(delegate{ortho.ChordDepth(.5,-1,2);});Reject(delegate{ortho.DepthFromDevice(double.NaN,false);});Need(!ortho.Unproject(-1,0,10,out world));
        var store=new AnnotationStore(2);Near(store.Upsert(ScientificAnnotation.Marker("a",V(1,2,3),"anchor")),1);Near(store.Upsert(ScientificAnnotation.Measurement("b",V(0,0,0),V(3,4,0),"distance")),2);Near(store.At(1).value,5);Need(store.At(1).unit=="m");
        Reject(delegate{store.Upsert(ScientificAnnotation.Marker("c",V(0,0,0),"full"));});Need(store.Count==2&&store.Generation==2);
        Near(store.Upsert(ScientificAnnotation.Readout("a",V(1,2,3),"temperature",12,"K",false)),3);Need(store.At(0).id=="a"&&!store.At(0).valid);Near(store.Remove("absent"),3);Reject(delegate{store.Remove(null);});Near(store.Remove("a"),4);Need(store.At(0).id=="b");
        var ring=ScientificAnnotation.Ring("r",V(0,0,10),V(1,0,0),V(0,1,0),2,"ring");var point=ring.RingPoint(Math.PI/2);Near(point.x,0);Near(point.y,2);Near(point.z,10);Need(ortho.Project(point,out x,out y,out z));Near(x,400);Near(y,240);
        var grid=ScientificAnnotation.Grid("g",V(0,0,0),V(1,0,0),V(0,1,0),5,4,"plane");Near(grid.spacing*grid.cells,20);Reject(delegate{ScientificAnnotation.Grid("bad",V(0,0,0),V(1,0,0),V(1,0,0),5,4,"");});Reject(delegate{ScientificAnnotation.Readout("bad",V(0,0,0),"",double.NaN,"",false);});
        Near(store.Clear(),5);Near(store.Clear(),5);Need(store.Count==0);
        Console.WriteLine("Scientific view/annotation analytic contract checks passed.");
    }
}
