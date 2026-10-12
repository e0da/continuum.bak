using System;
using KspContinuum;
static class Program
{
    static int checks;
    static ScientificVector3 V(double x,double y,double z) {return new ScientificVector3(x,y,z);}
    static void Need(bool b,string name) {checks++;if(!b)throw new Exception(name);}
    static void Near(ScientificVector3 a,ScientificVector3 b,string name) {Need(ScientificVector3.Distance(a,b)<1e-12,name);}
    static void Reject(Action action,string name) {bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Need(rejected,name);}
    static ScientificFrameObservation Observation(bool valid=true) {return new ScientificFrameObservation("body-owner/epoch10","inertial-metres",10,12,valid);}
    static ScientificView View() {return new ScientificView(V(0,0,-100),V(1,0,0),V(0,1,0),V(0,0,1),1200,800,ScientificProjectionKind.Orthographic,50,40,.1,1000);}
    static void Main()
    {
        var eq=ScientificReferenceFrames.Equatorial("equator",V(0,0,0),V(0,0,2),V(3,0,1),Observation());
        Near(eq.right,V(1,0,0),"Prime meridian projection");Near(eq.up,V(0,1,0),"Equatorial orthogonal axis");Near(eq.normal,V(0,0,1),"Body rotation axis");
        var orbit=ScientificReferenceFrames.Orbital("vessel-orbit",V(0,0,0),V(100,0,0),V(0,0,5),Observation());
        Near(orbit.normal,V(0,-1,0),"Inclined orbital plane cross");Near(orbit.up,V(0,0,1),"Inclined along-track axis");
        var retro=ScientificReferenceFrames.Orbital("retrograde",V(0,0,0),V(100,0,0),V(0,0,-5),Observation());Near(retro.normal,V(0,1,0),"Retrograde normal changes sign");
        var radial=ScientificReferenceFrames.RadialIn("orbit-in",V(100,0,0),V(100,0,0),V(0,-1,0),Observation());Near(radial.normal,V(1,0,0),"Radial-in outward normal");Near(radial.up,V(0,-1,0),"Radial-in orbit normal is screen up");
        foreach(ScientificFrameSide side in new[] {ScientificFrameSide.PositiveNormal,ScientificFrameSide.NegativeNormal})
        {
            var snapped=ScientificReferenceFrames.Snap(eq,View(),100,side,11);double x,y,z;
            Need(snapped.Project(eq.origin,out x,out y,out z)&&x==600&&y==400&&z==100,"Origin projects to center at chosen distance");
            Need(snapped.projection==ScientificProjectionKind.Orthographic&&snapped.verticalSpanMeters==40&&snapped.width==1200&&snapped.height==800&&snapped.near==.1&&snapped.far==1000,"Snap preserves scale/viewport/clip");
            var unitWorld=V(0,1,0);Need(snapped.Project(unitWorld,out x,out y,out z)&&Math.Abs(y-380)<1e-10,"One metre is independent analytic20pixels");
            Need(ScientificVector3.Dot(snapped.forward,eq.normal)==(side==ScientificFrameSide.PositiveNormal?-1:1),"Explicit side faces into origin");
        }
        var large=ScientificReferenceFrames.Plane("large-system",V(1e12,-1e12,2e12),V(0,0,1),V(1,0,0),Observation());
        var largeView=ScientificReferenceFrames.Snap(large,View(),100,ScientificFrameSide.NegativeNormal,11);double px,py,depth;Need(largeView.Project(large.origin,out px,out py,out depth)&&px==600&&py==400&&depth==100,"Subtract large origin in double");
        var tiny=ScientificReferenceFrames.Orbital("tiny-scale",V(0,0,0),V(1e-250,0,0),V(0,1e-250,0),Observation());Near(tiny.normal,V(0,0,1),"Direction normalization avoids product underflow");
        var futureObservation=new ScientificFrameObservation("node-17/predicted-point-2","body-relative-inertial-metres",1000,10,12,true);
        var future=ScientificReferenceFrames.Orbital("node-plane",V(0,0,0),V(0,200,0),V(0,0,4),futureObservation);
        Near(future.normal,V(1,0,0),"Future node position defines its own plane, not live craft");Need(future.observation.epochSeconds==1000&&future.observation.sourceId=="node-17/predicted-point-2","Future sample provenance retained");
        ScientificReferenceFrames.Snap(future,View(),100,ScientificFrameSide.PositiveNormal,11);
        Reject(()=>ScientificReferenceFrames.Snap(future,View(),100,ScientificFrameSide.PositiveNormal,13),"Predicted frame still expires");
        Reject(()=>ScientificReferenceFrames.Snap(eq,View(),100,ScientificFrameSide.PositiveNormal,9),"Not-yet-issued observation");
        var invalid=ScientificReferenceFrames.Plane("invalid",V(0,0,0),V(0,0,1),V(1,0,0),Observation(false));Reject(()=>ScientificReferenceFrames.Snap(invalid,View(),100,ScientificFrameSide.PositiveNormal,11),"Invalid observed plane cannot snap");
        Reject(()=>ScientificReferenceFrames.Orbital("radial",V(0,0,0),V(1,0,0),V(2,0,0),Observation()),"Radial orbit has no plane");
        Reject(()=>ScientificReferenceFrames.Plane("parallel",V(0,0,0),V(0,0,1),V(0,0,2),Observation()),"Parallel in-plane reference");
        Reject(()=>ScientificReferenceFrames.RadialIn("mismatched",V(0,0,0),V(1,0,0),V(1,1,0),Observation()),"Inconsistent radial and angular momentum");
        Reject(()=>ScientificReferenceFrames.Plane("zero",V(0,0,0),V(0,0,0),V(1,0,0),Observation()),"Zero normal");
        Reject(()=>ScientificReferenceFrames.Plane("nan",V(double.NaN,0,0),V(0,0,1),V(1,0,0),Observation()),"Nonfinite origin");
        Reject(()=>ScientificReferenceFrames.Snap(eq,View(),2000,ScientificFrameSide.PositiveNormal,11),"Preserved far clip cannot cover origin");
        Console.WriteLine("Scientific reference-frame checks passed: "+checks);
    }
}
