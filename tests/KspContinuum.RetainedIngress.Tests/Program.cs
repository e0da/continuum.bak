using System;
using KspContinuum;

static class Program
{
    static int checks;
    static void Main()
    {
        Analytic(); Differential(); AtomicRejection(); Allocation(); Ring();
        Console.WriteLine("Retained ingress checks passed: " + checks);
    }
    static RigidAssemblyMember Member(int id,double mass,Vec position,Vec center,Rotation principal)
    {return new RigidAssemblyMember(id,mass,position,Rotation.Identity,center,new Vec(2,3,4),principal);}
    static void Analytic()
    {
        var rows=new[]{Member(7,2,new Vec(3,4,5),new Vec(1,0,0),Rotation.Identity)};
        var ingress=new RetainedRigidIngress(rows);
        Near(ingress.Current.Mass,2); Near(ingress.Current.CenterOfMass,new Vec(4,4,5));
        rows[0]=Member(7,2,new Vec(3,4,5),new Vec(0,2,0),Rotation.Identity); Scan(ingress,rows);
        Near(ingress.Current.CenterOfMass,new Vec(3,6,5)); Near(ingress.Current.Inertia.XX,2);
        rows[0]=Member(7,2,new Vec(3,4,5),new Vec(0,2,0),Rotation.AxisAngle(new Vec(0,0,1),Math.PI/2)); Scan(ingress,rows);
        Near(ingress.Current.Inertia.XX,3); Near(ingress.Current.Inertia.YY,2); Compare(ingress.Current,RigidAssemblyProperties.Combine(rows));
        var sphere=new[]{new RigidAssemblyMember(0,1,new Vec(),Rotation.Identity,new Vec(),new Vec(5,5,5),Rotation.Identity)};
        Compare(new RetainedRigidIngress(sphere).Current,RigidAssemblyProperties.Combine(sphere));
        var pair=new[]{Member(4,2,new Vec(-1,0,0),new Vec(),Rotation.Identity),Member(1,2,new Vec(1,0,0),new Vec(),Rotation.Identity)};
        var pairProperties=new RetainedRigidIngress(pair).Current;
        Near(pairProperties.CenterOfMass,new Vec()); Near(pairProperties.Inertia.XX,4);Near(pairProperties.Inertia.YY,10);Near(pairProperties.Inertia.ZZ,12);
    }
    static void Differential()
    {
        var rows=new RigidAssemblyMember[32];
        for(int i=0;i<rows.Length;i++)rows[i]=Changed(i,0);
        var ingress=new RetainedRigidIngress(rows);
        Compare(ingress.Current,RigidAssemblyProperties.Combine(rows));
        for(int step=1;step<=1200;step++)
        {
            int row=step%rows.Length; rows[row]=Changed(row,step);
            if(step%11==0)rows[(row+7)%rows.Length]=Changed((row+7)%rows.Length,step+1);
            Scan(ingress,rows); Compare(ingress.Current,RigidAssemblyProperties.Combine(rows));
        }
        long generation=ingress.Current.Generation; Scan(ingress,rows);
        Require(ingress.Current.Generation==generation&&ingress.Reuses==1,"unchanged scan reused exact publication");
    }
    static RigidAssemblyMember Changed(int row,int step)
    {
        double phase=step*.0007+row*.13;
        return new RigidAssemblyMember(31-row, .1+(row+1)*.07+(step%37)*.001,
            new Vec(row-16,Math.Sin(phase)*4,row%3-.7),Rotation.AxisAngle(new Vec(1,2,3),phase),
            new Vec(.01*(step%5),.02*(row%4),-.03*(step%7)),
            new Vec(.2+row*.01,.4+row*.02,.7+(step%19)*.001),Rotation.AxisAngle(new Vec(3,-1,2),phase*.8));
    }
    static void AtomicRejection()
    {
        var rows=new[]{Member(9,1,new Vec(),new Vec(),Rotation.Identity),Member(2,2,new Vec(2,0,0),new Vec(),Rotation.Identity)};
        var ingress=new RetainedRigidIngress(new[]{100,200},rows); var initial=ingress.Current;
        ingress.BeginScan(); Set(ingress,0,100,rows[0]); Reject(()=>ingress.CompleteScan()); Unchanged(ingress,initial,rows);
        ingress.BeginScan(); Reject(()=>Set(ingress,0,200,rows[0])); Unchanged(ingress,initial,rows);
        ingress.BeginScan(); Set(ingress,0,100,rows[0]); Reject(()=>Set(ingress,0,100,rows[0])); Unchanged(ingress,initial,rows);
        ingress.BeginScan(); Set(ingress,0,100,Member(9,3,new Vec(),new Vec(),Rotation.Identity));
        Reject(()=>ingress.Set(1,200,2,new Vec(),Rotation.Identity,new Vec(double.NaN,0,0),new Vec(2,3,4),Rotation.Identity)); Unchanged(ingress,initial,rows);
        ingress.BeginScan(); Reject(()=>ingress.Set(0,100,double.PositiveInfinity,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity));
        ingress.BeginScan(); Reject(()=>ingress.Set(0,100,1,new Vec(),Rotation.Identity,new Vec(),new Vec(0,3,4),Rotation.Identity));
        ingress.BeginScan();
        ingress.Set(0,100,1e308,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity);
        ingress.Set(1,200,1e308,new Vec(),Rotation.Identity,new Vec(),new Vec(2,3,4),Rotation.Identity);
        Reject(()=>ingress.CompleteScan());Unchanged(ingress,initial,rows);
        ingress.BeginScan(); Set(ingress,1,200,rows[1]);Set(ingress,0,100,rows[0]); ingress.CompleteScan();
        Unchanged(ingress,initial,rows);
    }
    static void Unchanged(RetainedRigidIngress ingress,RetainedRigidPropertySnapshot initial,RigidAssemblyMember[] rows)
    {
        Compare(ingress.Current,initial.ToProperties()); Require(ingress.Current.Generation==initial.Generation,"failed scan retains generation");
        for(int i=0;i<rows.Length;i++)Bits(ingress.GetMember(i).Mass,rows[i].Mass);
    }
    static void Allocation()
    {
        var ingress=new RetainedRigidIngress(new[]{Member(0,1,new Vec(),new Vec(),Rotation.Identity)});
        for(int i=0;i<100;i++)PrimitiveScan(ingress,i);
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){PrimitiveScan(ingress,i);PrimitiveScan(ingress,i);}
        Require(GC.GetAllocatedBytesForCurrentThread()==start,"changed and unchanged hot scans allocate zero bytes");
    }
    static void PrimitiveScan(RetainedRigidIngress ingress,int i)
    {
        ingress.BeginScan(); ingress.Set(0,0,1+(i%17)*.01,new Vec(),Rotation.Identity,new Vec(.01*(i%3),0,0),new Vec(2,3,4),Rotation.Identity);ingress.CompleteScan();
    }
    static void Ring()
    {
        var ring=new RetainedRigidPerformanceRing(3,1000);
        var sample=new RetainedRigidPerformanceSample{Step=1,Frame=1,BeginTicks=10,EndTicks=30,DeltaSeconds=.02,PhysicsStep=true,InputRows=4,ChangedRows=1,PoseWrites=1,IngressTicks=10,PropertyTicks=15,PublicationTicks=12,ObserverTicks=2};
        for(int i=0;i<5;i++)ring.Record(sample);
        Require(ring.Count==3&&ring.DroppedSamples==2&&ring.GetRetained(0).Sequence==3,"ring preserves bounded chronological tail");
        Require(ring.Totals.Records==5&&ring.Totals.TotalTicks==100&&ring.Totals.InputRows==20,"totals include overwritten samples");
        var snapshot=ring.Snapshot();snapshot[0].Sequence=0;Require(ring.GetRetained(0).Sequence==3,"snapshot owns detached copy");
        sample.PropertyTicks=21;Reject(()=>ring.Record(sample));Require(ring.Totals.Records==5,"invalid sample retains totals");sample.PropertyTicks=15;
        for(int i=0;i<100;i++)ring.Record(sample);
        long start=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<1000;i++)ring.Record(sample);
        Require(GC.GetAllocatedBytesForCurrentThread()==start,"hot ring record allocates zero bytes");
    }
    static void Scan(RetainedRigidIngress ingress,RigidAssemblyMember[] rows)
    {ingress.BeginScan();for(int i=rows.Length-1;i>=0;i--)Set(ingress,i,ingress.GetId(i),rows[i]);ingress.CompleteScan();}
    static void Set(RetainedRigidIngress ingress,int row,int id,RigidAssemblyMember value)
    {ingress.Set(row,id,value.Mass,value.LocalPosition,value.LocalRotation,value.LocalCenterOfMass,value.PrincipalInertia,value.PrincipalRotation);}
    static void Compare(RetainedRigidPropertySnapshot a,RigidAssemblyProperties b)
    {
        Bits(a.Mass,b.Mass);Bits(a.CenterOfMass,b.CenterOfMass);Bits(a.PrincipalInertia,b.PrincipalInertia);Bits(a.PrincipalRotation,b.PrincipalRotation);
        Bits(a.Inertia.XX,b.Inertia.XX);Bits(a.Inertia.XY,b.Inertia.XY);Bits(a.Inertia.XZ,b.Inertia.XZ);Bits(a.Inertia.YY,b.Inertia.YY);Bits(a.Inertia.YZ,b.Inertia.YZ);Bits(a.Inertia.ZZ,b.Inertia.ZZ);
    }
    static void Bits(double a,double b){Require(BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b),"bitwise batch oracle mismatch");}
    static void Bits(Vec a,Vec b){Bits(a.X,b.X);Bits(a.Y,b.Y);Bits(a.Z,b.Z);}
    static void Bits(Rotation a,Rotation b){Bits(a.X,b.X);Bits(a.Y,b.Y);Bits(a.Z,b.Z);Bits(a.W,b.W);}
    static void Near(double a,double b){Require(Math.Abs(a-b)<=1e-12,"independent analytic mismatch");}
    static void Near(Vec a,Vec b){Near(a.X,b.X);Near(a.Y,b.Y);Near(a.Z,b.Z);}
    static void Reject(Action action){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}Require(rejected,"malformed transaction must reject");}
    static void Require(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
}
