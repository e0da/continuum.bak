using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KspContinuum
{
    public readonly struct RetainedRigidMemberValue
    {
        public readonly int Id,StableId;
        public readonly double Mass;
        public readonly Vec LocalPosition,LocalCenterOfMass,PrincipalInertia;
        public readonly Rotation LocalRotation,PrincipalRotation;
        public RetainedRigidMemberValue(int id,int stableId,double mass,Vec position,Rotation rotation,Vec center,Vec inertia,Rotation principalRotation)
        {Id=id;StableId=stableId;Mass=mass;LocalPosition=position;LocalRotation=rotation;LocalCenterOfMass=center;PrincipalInertia=inertia;PrincipalRotation=principalRotation;}
    }
    public readonly struct RetainedRigidPropertySnapshot
    {
        public readonly double Mass;
        public readonly Vec CenterOfMass,PrincipalInertia;
        public readonly InertiaTensor6Dof Inertia;
        public readonly Rotation PrincipalRotation;
        public readonly long Generation;
        internal RetainedRigidPropertySnapshot(double mass,Vec center,InertiaTensor6Dof inertia,Vec principal,Rotation rotation,long generation)
        {Mass=mass;CenterOfMass=center;Inertia=inertia;PrincipalInertia=principal;PrincipalRotation=rotation;Generation=generation;}
        public RigidAssemblyProperties ToProperties()
        {return new RigidAssemblyProperties(Mass,CenterOfMass,Inertia,PrincipalInertia,PrincipalRotation);}
    }

    public sealed class RetainedRigidIngress
    {
        struct Row
        {
            internal RetainedRigidMemberValue Value;
            internal Vec Center,X,Y,Z;
        }
        readonly int[] identities,rank;
        readonly Row[] accepted,pending;
        readonly long[] seen;
        readonly double[] original=new double[9],a=new double[9],v=new double[9],values=new double[3];
        readonly int[] order=new int[3];
        readonly Vec[] basis=new Vec[3];
        RetainedRigidPropertySnapshot published;
        long scanSerial;
        bool scanning;
        int visited,changed;
        public long Scans{get;private set;}
        public long Reductions{get;private set;}
        public long Reuses{get;private set;}
        public long RejectedScans{get;private set;}
        public long ChangedRows{get;private set;}
        public int LastChangedRows{get;private set;}
        public int Count{get{return accepted.Length;}}
        public RetainedRigidPropertySnapshot Current{get{return published;}}

        public RetainedRigidIngress(RigidAssemblyMember[] members):this(null,members){}
        public RetainedRigidIngress(int[] stableIds,RigidAssemblyMember[] members)
        {
            if(members==null||members.Length==0||members.Length>65536||(stableIds!=null&&stableIds.Length!=members.Length))
                throw new ArgumentException("Ingress requires bounded fixed member/identity rows.");
            int count=members.Length;identities=new int[count];rank=new int[count];accepted=new Row[count];pending=new Row[count];seen=new long[count];
            var ids=new HashSet<int>();var memberIds=new HashSet<int>();
            for(int i=0;i<count;i++)
            {
                var member=members[i];if(member==null||!memberIds.Add(member.Id))throw new ArgumentException("Member IDs must be unique.");
                int id=stableIds==null?member.Id:stableIds[i];if(!ids.Add(id))throw new ArgumentException("Ingress identities must be unique.");
                identities[i]=id;rank[i]=i;
                var value=new RetainedRigidMemberValue(member.Id,id,member.Mass,member.LocalPosition,member.LocalRotation,
                    member.LocalCenterOfMass,member.PrincipalInertia,member.PrincipalRotation);
                Validate(value);accepted[i]=Prepare(value,new Row(),true,true);
            }
            Array.Sort(rank,(x,y)=>accepted[x].Value.Id.CompareTo(accepted[y].Value.Id));
            published=Reduce(accepted,0);
        }
        public int GetId(int row){return identities[row];}
        public RetainedRigidMemberValue GetMember(int row){return accepted[row].Value;}
        public void BeginScan()
        {
            if(scanning)throw new InvalidOperationException("Ingress scan already owns the pending bank.");
            scanSerial=checked(scanSerial+1);visited=changed=0;scanning=true;
        }
        public void Set(int row,int id,double mass,Vec localPosition,Rotation localRotation,Vec localCenterOfMass,Vec principalInertia,Rotation principalRotation)
        {
            RequireScan();
            try
            {
                if(row<0||row>=Count||id!=identities[row]||seen[row]==scanSerial)
                    throw new ArgumentException("Missing, foreign, reordered identity or duplicate ingress row.");
                var old=accepted[row];var value=new RetainedRigidMemberValue(old.Value.Id,id,mass,localPosition,localRotation,localCenterOfMass,principalInertia,principalRotation);
                Validate(value);
                bool different=!Same(old.Value,value);
                bool centerChanged=!Same(old.Value.LocalPosition,localPosition)||!Same(old.Value.LocalRotation,localRotation)||!Same(old.Value.LocalCenterOfMass,localCenterOfMass);
                bool axesChanged=!Same(old.Value.LocalRotation,localRotation)||!Same(old.Value.PrincipalRotation,principalRotation);
                pending[row]=Prepare(value,old,centerChanged,axesChanged);seen[row]=scanSerial;visited++;if(different)changed++;
            }
            catch{AbortScan();throw;}
        }
        public RetainedRigidPropertySnapshot CompleteScan()
        {
            RequireScan();
            try
            {
                if(visited!=Count)throw new InvalidOperationException("Ingress scan is incomplete; previous publication retained.");
                if(changed==0){scanning=false;Scans++;Reuses++;LastChangedRows=0;return published;}
                var next=Reduce(pending,checked(published.Generation+1));
                Array.Copy(pending,accepted,Count);published=next;scanning=false;
                Scans++;Reductions++;ChangedRows+=changed;LastChangedRows=changed;return published;
            }
            catch{AbortScan();throw;}
        }
        public void AbortScan(){if(scanning){scanning=false;RejectedScans++;}}
        void RequireScan(){if(!scanning)throw new InvalidOperationException("No pending ingress scan.");}
        static void Validate(RetainedRigidMemberValue value)
        {
            AssemblyModel.Positive(value.Mass);RigidAssemblyProperties.RequireVector(value.LocalPosition);
            RigidAssemblyProperties.RequireVector(value.LocalCenterOfMass);RigidAssemblyProperties.RequireVector(value.PrincipalInertia);
            AssemblyModel.Positive(value.PrincipalInertia.X);AssemblyModel.Positive(value.PrincipalInertia.Y);AssemblyModel.Positive(value.PrincipalInertia.Z);
            RigidAssemblyProperties.RequireRotation(value.LocalRotation);RigidAssemblyProperties.RequireRotation(value.PrincipalRotation);
        }
        static Row Prepare(RetainedRigidMemberValue value,Row old,bool centerChanged,bool axesChanged)
        {
            old.Value=value;
            if(centerChanged){old.Center=value.LocalPosition+value.LocalRotation.Rotate(value.LocalCenterOfMass);RigidAssemblyProperties.RequireVector(old.Center);}
            if(axesChanged)
            {
                Rotation q=value.LocalRotation*value.PrincipalRotation;RigidAssemblyProperties.RequireRotation(q);
                old.X=q.Rotate(new Vec(1,0,0));old.Y=q.Rotate(new Vec(0,1,0));old.Z=q.Rotate(new Vec(0,0,1));
            }
            return old;
        }
        RetainedRigidPropertySnapshot Reduce(Row[] rows,long generation)
        {
            var total=new RigidAssemblyProperties.Sum();for(int j=0;j<Count;j++)total.Add(rows[rank[j]].Value.Mass);
            double mass=total.Value;AssemblyModel.Positive(mass);
            var cx=new RigidAssemblyProperties.Sum();var cy=new RigidAssemblyProperties.Sum();var cz=new RigidAssemblyProperties.Sum();
            for(int j=0;j<Count;j++){var row=rows[rank[j]];double weight=row.Value.Mass/mass;cx.Add(row.Center.X*weight);cy.Add(row.Center.Y*weight);cz.Add(row.Center.Z*weight);}
            Vec center=new Vec(cx.Value,cy.Value,cz.Value);RigidAssemblyProperties.RequireVector(center);
            var xx=new RigidAssemblyProperties.Sum();var xy=new RigidAssemblyProperties.Sum();var xz=new RigidAssemblyProperties.Sum();
            var yy=new RigidAssemblyProperties.Sum();var yz=new RigidAssemblyProperties.Sum();var zz=new RigidAssemblyProperties.Sum();
            for(int j=0;j<Count;j++)
            {
                var row=rows[rank[j]];Vec x=row.X,y=row.Y,z=row.Z,p=row.Value.PrincipalInertia;
                Vec d=new Vec(row.Center.X-center.X,row.Center.Y-center.Y,row.Center.Z-center.Z);RigidAssemblyProperties.RequireVector(d);
                Vec u=d*Math.Sqrt(row.Value.Mass);RigidAssemblyProperties.RequireVector(u);
                xx.Add(p.X*x.X*x.X+p.Y*y.X*y.X+p.Z*z.X*z.X+u.Y*u.Y+u.Z*u.Z);
                xy.Add(p.X*x.X*x.Y+p.Y*y.X*y.Y+p.Z*z.X*z.Y-u.X*u.Y);
                xz.Add(p.X*x.X*x.Z+p.Y*y.X*y.Z+p.Z*z.X*z.Z-u.X*u.Z);
                yy.Add(p.X*x.Y*x.Y+p.Y*y.Y*y.Y+p.Z*z.Y*z.Y+u.X*u.X+u.Z*u.Z);
                yz.Add(p.X*x.Y*x.Z+p.Y*y.Y*y.Z+p.Z*z.Y*z.Z-u.Y*u.Z);
                zz.Add(p.X*x.Z*x.Z+p.Y*y.Z*y.Z+p.Z*z.Z*z.Z+u.X*u.X+u.Y*u.Y);
            }
            var tensor=new InertiaTensor6Dof(xx.Value,xy.Value,xz.Value,yy.Value,yz.Value,zz.Value);
            Vec principal;Rotation axes;Diagonalize(tensor,out principal,out axes);
            return new RetainedRigidPropertySnapshot(mass,center,tensor,principal,axes,generation);
        }
        void Diagonalize(InertiaTensor6Dof tensor,out Vec principal,out Rotation axes)
        {
            original[0]=tensor.XX;original[1]=original[3]=tensor.XY;original[2]=original[6]=tensor.XZ;
            original[4]=tensor.YY;original[5]=original[7]=tensor.YZ;original[8]=tensor.ZZ;
            double scale=0;for(int i=0;i<9;i++){AssemblyModel.Finite(original[i]);scale=Math.Max(scale,Math.Abs(original[i]));}AssemblyModel.Positive(scale);
            for(int i=0;i<3;i++)for(int j=0;j<3;j++){a[i*3+j]=original[i*3+j]/scale;v[i*3+j]=i==j?1:0;}
            bool converged=false;
            for(int iteration=0;iteration<96;iteration++)
            {
                int p=0,q=1;double largest=Math.Abs(a[1]);
                if(Math.Abs(a[2])>largest){p=0;q=2;largest=Math.Abs(a[2]);}
                if(Math.Abs(a[5])>largest){p=1;q=2;largest=Math.Abs(a[5]);}
                if(largest<=1e-15){converged=true;break;}
                double tau=(a[q*3+q]-a[p*3+p])/(2*a[p*3+q]);
                double t=(tau>=0?1:-1)/(Math.Abs(tau)+Math.Sqrt(1+tau*tau));
                double cosine=1/Math.Sqrt(1+t*t),sine=t*cosine,off=a[p*3+q];
                a[p*3+p]-=t*off;a[q*3+q]+=t*off;a[p*3+q]=a[q*3+p]=0;
                for(int k=0;k<3;k++)
                {
                    if(k!=p&&k!=q){double kp=a[k*3+p],kq=a[k*3+q];a[k*3+p]=a[p*3+k]=cosine*kp-sine*kq;a[k*3+q]=a[q*3+k]=sine*kp+cosine*kq;}
                    double vp=v[k*3+p],vq=v[k*3+q];v[k*3+p]=cosine*vp-sine*vq;v[k*3+q]=sine*vp+cosine*vq;
                }
            }
            if(!converged)throw new ArgumentException("Symmetric inertia diagonalization did not converge.");
            order[0]=0;order[1]=1;order[2]=2;
            for(int i=1;i<3;i++){int item=order[i],j=i-1;while(j>=0&&Compare(order[j],item)>0){order[j+1]=order[j];j--;}order[j+1]=item;}
            principal=new Vec(a[order[0]*3+order[0]]*scale,a[order[1]*3+order[1]]*scale,a[order[2]*3+order[2]]*scale);
            AssemblyModel.Positive(principal.X);AssemblyModel.Positive(principal.Y);AssemblyModel.Positive(principal.Z);
            Vec x=Canonical(new Vec(v[order[0]],v[3+order[0]],v[6+order[0]]));Vec y=Canonical(new Vec(v[order[1]],v[3+order[1]],v[6+order[1]]));
            Vec z=Normalize(Vec.Cross(x,y));y=Normalize(Vec.Cross(z,x));axes=FromBasis(x,y,z);RigidAssemblyProperties.RequireRotation(axes);
            basis[0]=axes.Rotate(new Vec(1,0,0));basis[1]=axes.Rotate(new Vec(0,1,0));basis[2]=axes.Rotate(new Vec(0,0,1));
            values[0]=principal.X/scale;values[1]=principal.Y/scale;values[2]=principal.Z/scale;
            for(int i=0;i<3;i++)for(int j=0;j<3;j++)
            {
                double rebuilt=0;for(int k=0;k<3;k++)rebuilt+=values[k]*Component(basis[k],i)*Component(basis[k],j);
                if(Math.Abs(rebuilt-original[i*3+j]/scale)>1e-11)throw new ArgumentException("Principal inertia reconstruction exceeds its scale-relative residual bound.");
            }
        }
        int Compare(int i,int j){int comparison=a[i*3+i].CompareTo(a[j*3+j]);return comparison==0?i.CompareTo(j):comparison;}
        static Rotation FromBasis(Vec x,Vec y,Vec z)
        {
            double qx,qy,qz,qw,trace=x.X+y.Y+z.Z;
            if(trace>0){double s=Math.Sqrt(trace+1)*2;qw=s/4;qx=(y.Z-z.Y)/s;qy=(z.X-x.Z)/s;qz=(x.Y-y.X)/s;}
            else if(x.X>=y.Y&&x.X>=z.Z){double s=Math.Sqrt(1+x.X-y.Y-z.Z)*2;qw=(y.Z-z.Y)/s;qx=s/4;qy=(y.X+x.Y)/s;qz=(z.X+x.Z)/s;}
            else if(y.Y>=z.Z){double s=Math.Sqrt(1+y.Y-x.X-z.Z)*2;qw=(z.X-x.Z)/s;qx=(y.X+x.Y)/s;qy=s/4;qz=(z.Y+y.Z)/s;}
            else{double s=Math.Sqrt(1+z.Z-x.X-y.Y)*2;qw=(x.Y-y.X)/s;qx=(z.X+x.Z)/s;qy=(z.Y+y.Z)/s;qz=s/4;}
            if(qw<0||qw==0&&(qx<0||qx==0&&(qy<0||qy==0&&qz<0))){qx=-qx;qy=-qy;qz=-qz;qw=-qw;}
            return new Rotation(qx,qy,qz,qw);
        }
        static Vec Canonical(Vec axis){int largest=0;if(Math.Abs(axis.Y)>Math.Abs(axis.X))largest=1;if(Math.Abs(axis.Z)>Math.Abs(Component(axis,largest)))largest=2;return Normalize(Component(axis,largest)<0?axis*-1:axis);}
        static Vec Normalize(Vec value){double length=Math.Sqrt(value.X*value.X+value.Y*value.Y+value.Z*value.Z);AssemblyModel.Positive(length);return value*(1/length);}
        static double Component(Vec value,int index){return index==0?value.X:index==1?value.Y:value.Z;}
        static bool Same(double a,double b){return BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);}
        static bool Same(Vec a,Vec b){return Same(a.X,b.X)&&Same(a.Y,b.Y)&&Same(a.Z,b.Z);}
        static bool Same(Rotation a,Rotation b){return Same(a.X,b.X)&&Same(a.Y,b.Y)&&Same(a.Z,b.Z)&&Same(a.W,b.W);}
        static bool Same(RetainedRigidMemberValue a,RetainedRigidMemberValue b)
        {return Same(a.Mass,b.Mass)&&Same(a.LocalPosition,b.LocalPosition)&&Same(a.LocalRotation,b.LocalRotation)&&Same(a.LocalCenterOfMass,b.LocalCenterOfMass)&&Same(a.PrincipalInertia,b.PrincipalInertia)&&Same(a.PrincipalRotation,b.PrincipalRotation);}
    }

    public struct RetainedRigidPerformanceSample
    {
        public long Sequence,Step,BeginTicks,EndTicks,IngressTicks,PropertyTicks,PublicationTicks,ObserverTicks;
        public int Frame,InputRows,ChangedRows,PoseWrites;
        public double DeltaSeconds;
        public bool PhysicsStep;
    }
    public struct RetainedRigidPerformanceTotals
    {
        public long Records,PhysicsSteps,TotalTicks,InputRows,ChangedRows,PoseWrites,IngressTicks,PropertyTicks,PublicationTicks,ObserverTicks;
    }
    public sealed class RetainedRigidPerformanceRing
    {
        readonly RetainedRigidPerformanceSample[] ring;
        int next,count;
        long sequence;
        RetainedRigidPerformanceTotals totals;
        public readonly long ClockFrequency;
        public long DroppedSamples{get;private set;}
        public int Count{get{return count;}}
        public int Capacity{get{return ring.Length;}}
        public RetainedRigidPerformanceTotals Totals{get{return totals;}}
        public RetainedRigidPerformanceRing(int capacity=1024,long clockFrequency=0)
        {
            if(capacity<1||capacity>65536||clockFrequency<0)throw new ArgumentOutOfRangeException("capacity/frequency");
            ring=new RetainedRigidPerformanceSample[capacity];ClockFrequency=clockFrequency==0?Stopwatch.Frequency:clockFrequency;
        }
        public void Record(RetainedRigidPerformanceSample sample)
        {
            AssemblyModel.Finite(sample.DeltaSeconds);
            if(sample.Frame<0||sample.Step<0||sample.BeginTicks<0||sample.EndTicks<sample.BeginTicks||sample.DeltaSeconds<0
                ||(sample.PhysicsStep&&sample.DeltaSeconds<=0)||sample.InputRows<0||sample.ChangedRows<0||sample.ChangedRows>sample.InputRows||sample.PoseWrites<0)
                throw new ArgumentException("Performance sample is outside its domain.");
            long ticks=sample.EndTicks-sample.BeginTicks;
            if(sample.IngressTicks<0||sample.IngressTicks>ticks||sample.PropertyTicks<0||sample.PropertyTicks>ticks
                ||sample.PublicationTicks<0||sample.PublicationTicks>ticks||sample.ObserverTicks<0||sample.ObserverTicks>ticks)
                throw new ArgumentException("Nested timing scope exceeds its enclosing sample.");
            sample.Sequence=checked(sequence+1);sequence=sample.Sequence;ring[next]=sample;next=(next+1)%ring.Length;
            if(count<ring.Length)count++;else DroppedSamples++;
            totals.Records++;if(sample.PhysicsStep)totals.PhysicsSteps++;totals.TotalTicks+=ticks;totals.InputRows+=sample.InputRows;
            totals.ChangedRows+=sample.ChangedRows;totals.PoseWrites+=sample.PoseWrites;totals.IngressTicks+=sample.IngressTicks;
            totals.PropertyTicks+=sample.PropertyTicks;totals.PublicationTicks+=sample.PublicationTicks;totals.ObserverTicks+=sample.ObserverTicks;
        }
        public RetainedRigidPerformanceSample GetRetained(int index)
        {if(index<0||index>=count)throw new ArgumentOutOfRangeException("index");return ring[(next-count+ring.Length+index)%ring.Length];}
        public RetainedRigidPerformanceSample[] Snapshot()
        {var result=new RetainedRigidPerformanceSample[count];for(int i=0;i<count;i++)result[i]=GetRetained(i);return result;}
    }
}
