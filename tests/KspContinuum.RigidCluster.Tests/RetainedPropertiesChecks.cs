using System;
using KspContinuum;

static class RetainedPropertiesChecks
{
    static int checks;
    static readonly Rotation Identity = new Rotation(0,0,0,1);
    static void Need(bool value,string why) { checks++; if(!value)throw new Exception(why); }
    static void Near(double expected,double actual,string why)
    { Need(Math.Abs(expected-actual)<=1e-11*Math.Max(1,Math.Abs(expected)),why); }
    static void Bits(double expected,double actual,string why)
    { Need(BitConverter.DoubleToInt64Bits(expected)==BitConverter.DoubleToInt64Bits(actual),why); }
    static void Reject(Action action,string why)
    { checks++;try{action();}catch(ArgumentException){return;}throw new Exception(why); }
    static RigidAssemblyMember Member(int id,double mass,Vec position,Vec center,Vec inertia,Rotation principal)
    { return new RigidAssemblyMember(id,mass,position,Identity,center,inertia,principal); }
    static void Equal(RigidAssemblyProperties expected,RigidAssemblyProperties actual)
    {
        Bits(expected.Mass,actual.Mass,"retained mass bits");
        Bits(expected.CenterOfMass.X,actual.CenterOfMass.X,"retained center X bits");
        Bits(expected.CenterOfMass.Y,actual.CenterOfMass.Y,"retained center Y bits");
        Bits(expected.CenterOfMass.Z,actual.CenterOfMass.Z,"retained center Z bits");
        Bits(expected.Inertia.XX,actual.Inertia.XX,"retained tensor XX bits");
        Bits(expected.Inertia.XY,actual.Inertia.XY,"retained tensor XY bits");
        Bits(expected.Inertia.XZ,actual.Inertia.XZ,"retained tensor XZ bits");
        Bits(expected.Inertia.YY,actual.Inertia.YY,"retained tensor YY bits");
        Bits(expected.Inertia.YZ,actual.Inertia.YZ,"retained tensor YZ bits");
        Bits(expected.Inertia.ZZ,actual.Inertia.ZZ,"retained tensor ZZ bits");
        Bits(expected.PrincipalInertia.X,actual.PrincipalInertia.X,"retained principal X bits");
        Bits(expected.PrincipalInertia.Y,actual.PrincipalInertia.Y,"retained principal Y bits");
        Bits(expected.PrincipalInertia.Z,actual.PrincipalInertia.Z,"retained principal Z bits");
        Bits(expected.PrincipalRotation.X,actual.PrincipalRotation.X,"retained axis X bits");
        Bits(expected.PrincipalRotation.Y,actual.PrincipalRotation.Y,"retained axis Y bits");
        Bits(expected.PrincipalRotation.Z,actual.PrincipalRotation.Z,"retained axis Z bits");
        Bits(expected.PrincipalRotation.W,actual.PrincipalRotation.W,"retained axis W bits");
    }
    public static int Run()
    {
        checks=0;
        var left=Member(9,1,new Vec(),new Vec(),new Vec(1,1,1),Identity);
        var right=Member(2,1,new Vec(2,0,0),new Vec(),new Vec(1,1,1),Identity);
        var reducer=new RetainedRigidAssemblyProperties(new[]{left,right});
        Need(reducer.Count==2&&reducer.GetId(0)==2&&reducer.GetId(1)==9,"cold canonical ID order");
        Equal(RigidAssemblyProperties.Combine(new[]{right,left}),reducer.Combine());
        var moved=Member(9,1,new Vec(),new Vec(1,0,0),new Vec(1,1,1),Identity);
        reducer.Update(moved);var properties=reducer.Combine();
        Near(2,properties.Mass,"same-mass COM update mass");Near(1.5,properties.CenterOfMass.X,"same-mass COM update center");
        Near(2,properties.Inertia.XX,"same-mass COM update axial inertia");Near(2.5,properties.Inertia.YY,"same-mass COM update transverse inertia");
        Equal(RigidAssemblyProperties.Combine(new[]{moved,right}),properties);
        Need(left.LocalCenterOfMass.X==0,"immutable original member preserved");

        var quarterTurn=new Rotation(0,0,Math.Sqrt(.5),Math.Sqrt(.5));
        var anisotropic=Member(4,2,new Vec(),new Vec(),new Vec(2,3,4),Identity);
        var axisReducer=new RetainedRigidAssemblyProperties(new[]{anisotropic});
        var rotated=Member(4,2,new Vec(),new Vec(),new Vec(2,3,4),quarterTurn);
        axisReducer.Update(0,rotated);properties=axisReducer.Combine();
        Near(3,properties.Inertia.XX,"principal-axis-only XX");Near(2,properties.Inertia.YY,"principal-axis-only YY");Near(4,properties.Inertia.ZZ,"principal-axis-only ZZ");
        Equal(RigidAssemblyProperties.Combine(new[]{rotated}),properties);

        var heavy=Member(2,3,new Vec(2,0,0),new Vec(),new Vec(1,1,1),Identity);
        var lossReducer=new RetainedRigidAssemblyProperties(new[]{left,heavy});
        properties=lossReducer.Combine();Near(4,properties.Mass,"pre-loss mass");Near(1.5,properties.CenterOfMass.X,"pre-loss center");Near(5,properties.Inertia.YY,"pre-loss inertia");
        lossReducer.Update(right);properties=lossReducer.Combine();Near(2,properties.Mass,"mass loss");Near(1,properties.CenterOfMass.X,"mass-loss COM");Near(4,properties.Inertia.YY,"mass-loss inertia");

        var isotropic=Member(7,1,new Vec(),new Vec(),new Vec(2,2,2),quarterTurn);
        var degenerate=new RetainedRigidAssemblyProperties(new[]{isotropic}).Combine();
        Near(2,degenerate.Inertia.XX,"degenerate tensor XX");Near(2,degenerate.Inertia.YY,"degenerate tensor YY");Near(2,degenerate.Inertia.ZZ,"degenerate tensor ZZ");
        Near(0,degenerate.Inertia.XY,"degenerate tensor XY");Near(0,degenerate.Inertia.XZ,"degenerate tensor XZ");Near(0,degenerate.Inertia.YZ,"degenerate tensor YZ");
        Vec[] axes={degenerate.PrincipalRotation.Rotate(new Vec(1,0,0)),degenerate.PrincipalRotation.Rotate(new Vec(0,1,0)),degenerate.PrincipalRotation.Rotate(new Vec(0,0,1))};
        Near(2,degenerate.PrincipalInertia.X*axes[0].X*axes[0].X+degenerate.PrincipalInertia.Y*axes[1].X*axes[1].X+degenerate.PrincipalInertia.Z*axes[2].X*axes[2].X,"degenerate principal tensor reconstruction");

        var retainedBefore=reducer.Combine();
        Reject(()=>reducer.Update(anisotropic),"foreign member accepted");
        Reject(()=>reducer.Update(0,moved),"foreign slot ID accepted");
        Reject(()=>reducer.UpdateAll(new[]{moved}),"missing topology accepted");
        Reject(()=>reducer.UpdateAll(new[]{moved,anisotropic}),"foreign topology accepted");
        Reject(()=>reducer.UpdateAll(new[]{moved,moved}),"duplicate topology accepted");
        Equal(retainedBefore,reducer.Combine());
        Reject(()=>new RetainedRigidAssemblyProperties(new[]{left,left}),"duplicate admission accepted");
        Reject(()=>new RetainedRigidAssemblyProperties(new RigidAssemblyMember[0]),"empty admission accepted");

        var sequence=new RigidAssemblyMember[17];
        for(int i=0;i<sequence.Length;i++)sequence[i]=Member(100+3*i,1+i*.25,new Vec(i-8,(i%3)-1,(i%5)*.3),new Vec(),new Vec(2+i*.1,3+i*.2,4+i*.15),Identity);
        var reversed=(RigidAssemblyMember[])sequence.Clone();Array.Reverse(reversed);
        var retained=new RetainedRigidAssemblyProperties(reversed);
        for(int iteration=0;iteration<2048;iteration++)
        {
            int slot=iteration%sequence.Length;var previous=sequence[slot];double angle=(iteration%29)*.03;
            var principal=new Rotation(0,Math.Sin(angle/2),0,Math.Cos(angle/2));
            sequence[slot]=new RigidAssemblyMember(previous.Id,1+(iteration%23)*.03125,previous.LocalPosition,previous.LocalRotation,
                new Vec((iteration%7)*.015625,-(iteration%11)*.0078125,(iteration%13)*.00390625),
                new Vec(2+(iteration%5)*.125,3+(iteration%17)*.0625,4+(iteration%19)*.03125),principal);
            retained.Update(sequence[slot]);Equal(RigidAssemblyProperties.Combine(sequence),retained.Combine());
        }
        retained.UpdateAll(reversed);Equal(RigidAssemblyProperties.Combine(reversed),retained.Combine());
        return checks;
    }
}
