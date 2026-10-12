using System;
using KspContinuum;

class Program
{
    static void Near(double actual,double expected,double tolerance=1e-10)
    { if (!double.IsFinite(actual)||Math.Abs(actual-expected)>tolerance*Math.Max(1,Math.Abs(expected)))throw new Exception("Mismatch: "+actual+" versus "+expected); }
    static void Vector(Vec actual,Vec expected) {Near(actual.X,expected.X);Near(actual.Y,expected.Y);Near(actual.Z,expected.Z);}
    static void Reject(Action action)
    {try {action();}catch(ArgumentException) {return;}throw new Exception("Invalid aggregate input was accepted.");}
    static RigidAssemblyMember Member(int id,double mass,Vec position,Vec inertia)
    {return new RigidAssemblyMember(id,mass,position,Rotation.Identity,new Vec(),inertia,Rotation.Identity);}
    static double Dot(Vec a,Vec b) {return a.X*b.X+a.Y*b.Y+a.Z*b.Z;}
    static void Reconstruct(RigidAssemblyProperties p)
    {
        Vec x=p.PrincipalRotation.Rotate(new Vec(1,0,0)),y=p.PrincipalRotation.Rotate(new Vec(0,1,0)),z=p.PrincipalRotation.Rotate(new Vec(0,0,1));
        Near(Dot(x,Vec.Cross(y,z)),1); Near(Dot(x,y),0); Near(Dot(x,z),0); Near(Dot(y,z),0);
        if (!(p.PrincipalInertia.X<=p.PrincipalInertia.Y&&p.PrincipalInertia.Y<=p.PrincipalInertia.Z))throw new Exception("Principal moments not sorted.");
        foreach (var axis in new[] {new Vec(1,0,0),new Vec(0,1,0),new Vec(0,0,1),new Vec(2,-3,4)})
        {
            Vec expected=x*(p.PrincipalInertia.X*Dot(x,axis))+y*(p.PrincipalInertia.Y*Dot(y,axis))+z*(p.PrincipalInertia.Z*Dot(z,axis));
            Vector(p.Inertia.Multiply(axis),expected);
        }
    }
    static void Main(string[] args)
    {
        if(args.Length==1){NativeReceipt.Verify(args[0]);return;}
        // Two separated 2x4x6 boxes: each mass 3, diagonal (13,10,5).
        var boxes=RigidAssemblyProperties.Combine(new[] {Member(1,3,new Vec(-2,0,0),new Vec(13,10,5)),Member(2,3,new Vec(2,0,0),new Vec(13,10,5))});
        Near(boxes.Mass,6);Vector(boxes.CenterOfMass,new Vec());
        Near(boxes.Inertia.XX,26);Near(boxes.Inertia.YY,44);Near(boxes.Inertia.ZZ,34);
        Near(boxes.Inertia.XY,0);Near(boxes.Inertia.XZ,0);Near(boxes.Inertia.YZ,0);Reconstruct(boxes);
        var heavy=Member(3,2,new Vec(1,0,0),new Vec(1,1,1));
        var light=Member(4,1,new Vec(4,0,0),new Vec(1,1,1));
        var unequal=RigidAssemblyProperties.Combine(new[] {heavy,light});
        Vector(unequal.CenterOfMass,new Vec(2,0,0));Near(unequal.Inertia.XX,2);Near(unequal.Inertia.YY,8);Near(unequal.Inertia.ZZ,8);Reconstruct(unequal);
        var changedMass=RigidAssemblyProperties.Combine(new[] {heavy.WithMass(4,new Vec(2,2,2)),light});
        Vector(changedMass.CenterOfMass,new Vec(1.6,0,0));Near(changedMass.Inertia.XX,3);Near(changedMass.Inertia.YY,10.2);Near(changedMass.Inertia.ZZ,10.2);Reconstruct(changedMass);

        // A 90-degree local rotation carries COM(1,0,0) to world(0,1,0),
        // independently swapping the body's X/Y inertia axes.
        var offset=RigidAssemblyProperties.Combine(new[] {new RigidAssemblyMember(4,2,new Vec(3,4,5),Rotation.AxisAngle(new Vec(0,0,1),Math.PI/2),new Vec(1,0,0),new Vec(2,3,4),Rotation.Identity)});
        Vector(offset.CenterOfMass,new Vec(3,5,5));Near(offset.Inertia.XX,3);Near(offset.Inertia.YY,2);Near(offset.Inertia.ZZ,4);Reconstruct(offset);

        // Independent tensor: diag(2,4,6) rotated 45 degrees about Z =>
        // [[3,-1,0],[-1,3,0],[0,0,6]]. Two mass-2 centers at +/-(1,2,3)
        // contribute [[52,-8,-12],[-8,40,-24],[-12,-24,20]].
        Rotation turn=Rotation.AxisAngle(new Vec(0,0,1),Math.PI/4);
        var a=new RigidAssemblyMember(8,2,new Vec(1,2,3),Rotation.Identity,new Vec(),new Vec(2,4,6),turn);
        var b=new RigidAssemblyMember(9,2,new Vec(-1,-2,-3),Rotation.Identity,new Vec(),new Vec(2,4,6),turn);
        var offaxis=RigidAssemblyProperties.Combine(new[] {a,b});
        Vector(offaxis.CenterOfMass,new Vec());Near(offaxis.Inertia.XX,58);Near(offaxis.Inertia.XY,-10);Near(offaxis.Inertia.XZ,-12);
        Near(offaxis.Inertia.YY,46);Near(offaxis.Inertia.YZ,-24);Near(offaxis.Inertia.ZZ,32);Reconstruct(offaxis);
        var reversed=RigidAssemblyProperties.Combine(new[] {b,a});
        Near(offaxis.PrincipalRotation.X,reversed.PrincipalRotation.X,0);Near(offaxis.PrincipalRotation.Y,reversed.PrincipalRotation.Y,0);
        Near(offaxis.PrincipalRotation.Z,reversed.PrincipalRotation.Z,0);Near(offaxis.PrincipalRotation.W,reversed.PrincipalRotation.W,0);

        // Nested nontrivial rotations cancel; principal frame is not body frame.
        Rotation arbitrary=Rotation.AxisAngle(new Vec(2,-3,4),.73);
        var cancel=RigidAssemblyProperties.Combine(new[] {new RigidAssemblyMember(5,1,new Vec(),arbitrary,new Vec(),new Vec(3,5,7),arbitrary.Inverse)});
        Near(cancel.Inertia.XX,3);Near(cancel.Inertia.YY,5);Near(cancel.Inertia.ZZ,7);Reconstruct(cancel);
        var sphere=RigidAssemblyProperties.Combine(new[] {Member(0,1,new Vec(),new Vec(2,2,2))});
        Vector(sphere.PrincipalInertia,new Vec(2,2,2));Near(sphere.PrincipalRotation.W,1);Reconstruct(sphere);
        foreach(double scale in new[] {1e-100,1e100})
        {
            var scaled=RigidAssemblyProperties.Combine(new[] {new RigidAssemblyMember(1,1,new Vec(),Rotation.Identity,new Vec(),new Vec(2*scale,4*scale,6*scale),turn)});
            Near(scaled.Inertia.XY/scale,-1);Near(scaled.PrincipalInertia.X/scale,2);Near(scaled.PrincipalInertia.Y/scale,4);Near(scaled.PrincipalInertia.Z/scale,6);
        }
        var largeOffset=RigidAssemblyProperties.Combine(new[] {Member(1,1e-300,new Vec(1e200,0,0),new Vec(1,1,1)),Member(2,1e-300,new Vec(-1e200,0,0),new Vec(1,1,1))});
        Near(largeOffset.Inertia.XX,2);Near(largeOffset.Inertia.YY/1e100,2);Near(largeOffset.Inertia.ZZ/1e100,2);
        var tinyOffset=RigidAssemblyProperties.Combine(new[] {Member(1,1e300,new Vec(1e-200,0,0),new Vec(1e-100,1e-100,1e-100)),Member(2,1e300,new Vec(-1e-200,0,0),new Vec(1e-100,1e-100,1e-100))});
        Near(tinyOffset.Inertia.XX/1e-100,2);Near(tinyOffset.Inertia.YY/1e-100,4);Near(tinyOffset.Inertia.ZZ/1e-100,4);
        var revised=a.WithMass(4,new Vec(4,8,12));
        Near(a.Mass,2);Near(revised.Mass,4);Vector(revised.LocalPosition,a.LocalPosition);Vector(revised.LocalCenterOfMass,a.LocalCenterOfMass);
        Near(Rotation.Dot(revised.LocalRotation,a.LocalRotation),1);Near(Rotation.Dot(revised.PrincipalRotation,a.PrincipalRotation),1);
        Reject(()=>RigidAssemblyProperties.Combine(null));Reject(()=>RigidAssemblyProperties.Combine(new RigidAssemblyMember[0]));
        Reject(()=>RigidAssemblyProperties.Combine(new[] {a,a}));Reject(()=>RigidAssemblyProperties.Combine(new RigidAssemblyMember[] {null}));
        Reject(()=>Member(-1,1,new Vec(),new Vec(1,1,1)));Reject(()=>Member(1,0,new Vec(),new Vec(1,1,1)));
        Reject(()=>Member(1,double.NaN,new Vec(),new Vec(1,1,1)));Reject(()=>Member(1,1,new Vec(double.PositiveInfinity,0,0),new Vec(1,1,1)));
        Reject(()=>Member(1,1,new Vec(),new Vec(0,1,1)));Reject(()=>Member(1,1,new Vec(),new Vec(1,double.NaN,1)));
        Reject(()=>new RigidAssemblyMember(1,1,new Vec(),default(Rotation),new Vec(),new Vec(1,1,1),Rotation.Identity));
        Reject(()=>new RigidAssemblyMember(1,1,new Vec(),Rotation.Identity,new Vec(),new Vec(1,1,1),default(Rotation)));
        Reject(()=>RigidAssemblyProperties.Combine(new[] {Member(1,double.MaxValue,new Vec(),new Vec(1,1,1)),Member(2,double.MaxValue,new Vec(),new Vec(1,1,1))}));
        Reject(()=>RigidAssemblyProperties.Combine(new[] {Member(1,1,new Vec(1e200,0,0),new Vec(1,1,1)),Member(2,1,new Vec(-1e200,0,0),new Vec(1,1,1))}));
        Console.WriteLine("Rigid aggregate independent boxes, COM offsets, rotated/off-axis tensors, principal reconstruction, deterministic degeneracy, scaling, revised mass and invalid-input checks passed.");
    }
}
