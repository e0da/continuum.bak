using System;
using KspContinuum;

static class Program
{
    static void Need(bool yes,string reason){if(!yes)throw new Exception(reason);}
    static void Near(double a,double b){Need(Math.Abs(a-b)<=1e-12*Math.Max(1,Math.Abs(b)),a+" differs from "+b);}
    static void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new Exception("Invalid supply source was admitted.");}
    static OwnedRuntimeSupplyCurve Constant(double value){return new OwnedRuntimeSupplyCurve(new[]{0d},new[]{value},new[]{0d},new[]{0d});}
    static OwnedRuntimeSolarSupplySpec Panel(double launch=100){return new OwnedRuntimeSolarSupplySpec(7,0,"ElectricCharge",4,new Vec(),new Vec(1,0,0),1,launch,Constant(1),Constant(1));}
    static OwnedRuntimeSolarEphemeris Source(){return new OwnedRuntimeSolarEphemeris(new string('a',64),100,300,new Vec(10,0,0),new Vec(2,0,0),1,1);}
    static void Main()
    {
        var source=Source();var panel=Panel();var face=OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(2,0,0),new Vec(1,0,0),300);Near(face.UnitsPerSecond,4);Need(!face.Eclipsed,"Front panel eclipsed.");
        var edge=OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(2,0,0),new Vec(0,1,0),300);Near(edge.UnitsPerSecond,0);
        double root=Math.Sqrt(.5);var tilted=OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(2,0,0),new Vec(root,root,0),300);Near(tilted.UnitsPerSecond,4*root);
        var far=OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(4,0,0),new Vec(1,0,0),300);Near(far.UnitsPerSecond,64d/9);
        var dark=OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(-2,0,0),new Vec(1,0,0),300);Need(dark.Eclipsed,"Body eclipse missed.");Near(dark.UnitsPerSecond,0);
        var clear=OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(0,2,0),new Vec(10/Math.Sqrt(104d),-2/Math.Sqrt(104d),0),300);Need(!clear.Eclipsed,"Off-body ray eclipsed.");
        double[] times={100d,300},values={1d,.5},slopes={-.0025,-.0025};var temperature=new OwnedRuntimeSupplyCurve(times,values,slopes,slopes);times[0]=-100;values[0]=99;slopes[0]=99;Near(temperature.Evaluate(200),.75);Near(temperature.Evaluate(0),1);Near(temperature.Evaluate(400),.5);
        var age=new OwnedRuntimeSupplyCurve(new[]{0d,1},new[]{1d,.5},new[]{-.5,-.5},new[]{-.5,-.5});
        var aged=new OwnedRuntimeSolarSupplySpec(8,0,"ElectricCharge",4,new Vec(),new Vec(1,0,0),.8,100-21600,temperature,age);
        Near(OwnedRuntimeScalarSupply.Evaluate(aged,source,100,new Vec(2,0,0),new Vec(1,0,0),200).UnitsPerSecond,4*.75*.875*.8);
        var command=new OwnedRuntimeCommandLoadSpec(9,0,"ElectricCharge",.05,.01,0,false);Near(OwnedRuntimeScalarSupply.CommandRate(command),.05);Need(OwnedRuntimeScalarSupply.CommandReady(command,1,.9)&&!OwnedRuntimeScalarSupply.CommandReady(command,1,.899),"Readiness threshold differs.");
        var hibernating=new OwnedRuntimeCommandLoadSpec(9,0,"ElectricCharge",.05,.01,0,true);Near(OwnedRuntimeScalarSupply.CommandRate(hibernating),.0005);Need(!OwnedRuntimeScalarSupply.CommandReady(hibernating,1,1),"Hibernating command admitted full readiness.");
        Reject(()=>OwnedRuntimeScalarSupply.CommandReady(command,1,2));Reject(()=>OwnedRuntimeScalarSupply.Evaluate(panel,source,401,new Vec(2,0,0),new Vec(1,0,0),300));Reject(()=>OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(),new Vec(1,0,0),300));Reject(()=>OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(2,0,0),new Vec(2,0,0),300));Reject(()=>OwnedRuntimeScalarSupply.Evaluate(Panel(101),source,100,new Vec(2,0,0),new Vec(1,0,0),300));
        Reject(()=>new OwnedRuntimeSupplyCurve(new[]{0d,0},new[]{1d,1},new[]{0d,0},new[]{0d,0}));Reject(()=>new OwnedRuntimeSolarEphemeris(new string('a',64),100,301,new Vec(10,0,0),new Vec(2,0,0),1,1));
        Near(OwnedRuntimeScalarSupply.Evaluate(panel,source,100,new Vec(2,0,0),new Vec(1,0,0),300).UnitsPerSecond,face.UnitsPerSecond);
        Console.WriteLine("PASS scalar supply: analytic incidence/flux/eclipse/curves, command demand/readiness, immutable source and bounded rejection.");
    }
}
