using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public sealed class RigidAssemblyMember
    {
        public RigidAssemblyMember(int id, double mass, Vec localPosition, Rotation localRotation,
            Vec localCenterOfMass, Vec principalInertia, Rotation principalRotation)
        {
            if (id < 0) throw new ArgumentOutOfRangeException("id");
            AssemblyModel.Positive(mass);
            RigidAssemblyProperties.RequireVector(localPosition);
            RigidAssemblyProperties.RequireVector(localCenterOfMass);
            RigidAssemblyProperties.RequireRotation(localRotation);
            RigidAssemblyProperties.RequireRotation(principalRotation);
            AssemblyModel.Positive(principalInertia.X);
            AssemblyModel.Positive(principalInertia.Y);
            AssemblyModel.Positive(principalInertia.Z);
            Id = id; Mass = mass; LocalPosition = localPosition; LocalRotation = localRotation;
            LocalCenterOfMass = localCenterOfMass; PrincipalInertia = principalInertia;
            PrincipalRotation = principalRotation;
        }
        public int Id { get; private set; }
        public double Mass { get; private set; }
        public Vec LocalPosition { get; private set; }
        public Rotation LocalRotation { get; private set; }
        public Vec LocalCenterOfMass { get; private set; }
        public Vec PrincipalInertia { get; private set; }
        public Rotation PrincipalRotation { get; private set; }
        public RigidAssemblyMember WithMass(double mass, Vec principalInertia)
        {
            return new RigidAssemblyMember(Id, mass, LocalPosition, LocalRotation,
                LocalCenterOfMass, principalInertia, PrincipalRotation);
        }
    }

    public sealed class RigidAssemblyProperties
    {
        const double EigenTolerance = 1e-15, ReconstructionTolerance = 1e-11;
        internal RigidAssemblyProperties(double mass, Vec center, InertiaTensor6Dof inertia,
            Vec principal, Rotation orientation)
        { Mass = mass; CenterOfMass = center; Inertia = inertia; PrincipalInertia = principal; PrincipalRotation = orientation; }
        public double Mass { get; private set; }
        public Vec CenterOfMass { get; private set; }
        public InertiaTensor6Dof Inertia { get; private set; }
        public Vec PrincipalInertia { get; private set; }
        // Maps principal-axis coordinates into the assembly frame; moments are ascending.
        public Rotation PrincipalRotation { get; private set; }

        public static RigidAssemblyProperties Combine(IEnumerable<RigidAssemblyMember> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var members = new List<RigidAssemblyMember>(); var ids = new HashSet<int>();
            foreach (var member in source)
            {
                if (member == null || !ids.Add(member.Id))
                    throw new ArgumentException("Members must be nonnull with unique IDs.", "source");
                members.Add(member);
            }
            if (members.Count == 0) throw new ArgumentException("At least one member is required.", "source");
            members.Sort((a, b) => a.Id.CompareTo(b.Id));
            var total = new Sum(); foreach (var member in members) total.Add(member.Mass);
            double mass = total.Value; AssemblyModel.Positive(mass);
            var centers = new Vec[members.Count]; var cx = new Sum(); var cy = new Sum(); var cz = new Sum();
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                Vec center = member.LocalPosition + member.LocalRotation.Rotate(member.LocalCenterOfMass);
                RequireVector(center); centers[i] = center;
                double weight = member.Mass / mass;
                cx.Add(center.X * weight); cy.Add(center.Y * weight); cz.Add(center.Z * weight);
            }
            Vec com = new Vec(cx.Value, cy.Value, cz.Value); RequireVector(com);
            var xx = new Sum(); var xy = new Sum(); var xz = new Sum();
            var yy = new Sum(); var yz = new Sum(); var zz = new Sum();
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i]; Rotation q = member.LocalRotation * member.PrincipalRotation;
                RequireRotation(q);
                Vec a = q.Rotate(new Vec(1, 0, 0)), b = q.Rotate(new Vec(0, 1, 0)), c = q.Rotate(new Vec(0, 0, 1));
                Vec d = Subtract(centers[i], com); RequireVector(d); Vec p = member.PrincipalInertia;
                // Scale before squaring so a finite parallel-axis term need not overflow d*d.
                Vec u = d*Math.Sqrt(member.Mass); RequireVector(u);
                xx.Add(p.X*a.X*a.X + p.Y*b.X*b.X + p.Z*c.X*c.X + u.Y*u.Y + u.Z*u.Z);
                xy.Add(p.X*a.X*a.Y + p.Y*b.X*b.Y + p.Z*c.X*c.Y - u.X*u.Y);
                xz.Add(p.X*a.X*a.Z + p.Y*b.X*b.Z + p.Z*c.X*c.Z - u.X*u.Z);
                yy.Add(p.X*a.Y*a.Y + p.Y*b.Y*b.Y + p.Z*c.Y*c.Y + u.X*u.X + u.Z*u.Z);
                yz.Add(p.X*a.Y*a.Z + p.Y*b.Y*b.Z + p.Z*c.Y*c.Z - u.Y*u.Z);
                zz.Add(p.X*a.Z*a.Z + p.Y*b.Z*b.Z + p.Z*c.Z*c.Z + u.X*u.X + u.Y*u.Y);
            }
            var tensor = new InertiaTensor6Dof(xx.Value, xy.Value, xz.Value, yy.Value, yz.Value, zz.Value);
            Vec principal; Rotation axes; Diagonalize(tensor, out principal, out axes);
            return new RigidAssemblyProperties(mass, com, tensor, principal, axes);
        }

        internal static void Diagonalize(InertiaTensor6Dof tensor, out Vec principal, out Rotation axes)
        {
            double[,] original = {{tensor.XX,tensor.XY,tensor.XZ},{tensor.XY,tensor.YY,tensor.YZ},{tensor.XZ,tensor.YZ,tensor.ZZ}};
            double scale = 0; foreach (double value in original) { AssemblyModel.Finite(value); scale = Math.Max(scale, Math.Abs(value)); }
            AssemblyModel.Positive(scale);
            var a = new double[3,3]; var v = new double[3,3];
            for (int i=0;i<3;i++) for (int j=0;j<3;j++) { a[i,j]=original[i,j]/scale; v[i,j]=i==j?1:0; }
            bool converged = false;
            for (int iteration=0;iteration<96;iteration++)
            {
                int p=0,q=1; double largest=Math.Abs(a[0,1]);
                if (Math.Abs(a[0,2])>largest) { p=0;q=2;largest=Math.Abs(a[0,2]); }
                if (Math.Abs(a[1,2])>largest) { p=1;q=2;largest=Math.Abs(a[1,2]); }
                if (largest<=EigenTolerance) { converged=true;break; }
                double tau=(a[q,q]-a[p,p])/(2*a[p,q]);
                double t=(tau>=0?1:-1)/(Math.Abs(tau)+Math.Sqrt(1+tau*tau));
                double cosine=1/Math.Sqrt(1+t*t),sine=t*cosine,off=a[p,q];
                a[p,p]-=t*off; a[q,q]+=t*off; a[p,q]=a[q,p]=0;
                for (int k=0;k<3;k++)
                {
                    if (k!=p&&k!=q)
                    {
                        double kp=a[k,p],kq=a[k,q];
                        a[k,p]=a[p,k]=cosine*kp-sine*kq;
                        a[k,q]=a[q,k]=sine*kp+cosine*kq;
                    }
                    double vp=v[k,p],vq=v[k,q]; v[k,p]=cosine*vp-sine*vq; v[k,q]=sine*vp+cosine*vq;
                }
            }
            if (!converged) throw new ArgumentException("Symmetric inertia diagonalization did not converge.");
            int[] order={0,1,2};
            Array.Sort(order,(i,j)=> { int comparison=a[i,i].CompareTo(a[j,j]); return comparison==0?i.CompareTo(j):comparison; });
            principal=new Vec(a[order[0],order[0]]*scale,a[order[1],order[1]]*scale,a[order[2],order[2]]*scale);
            AssemblyModel.Positive(principal.X); AssemblyModel.Positive(principal.Y); AssemblyModel.Positive(principal.Z);
            Vec x=CanonicalAxis(new Vec(v[0,order[0]],v[1,order[0]],v[2,order[0]]));
            Vec y=CanonicalAxis(new Vec(v[0,order[1]],v[1,order[1]],v[2,order[1]]));
            Vec z=Normalize(Vec.Cross(x,y)); y=Normalize(Vec.Cross(z,x));
            axes=FromBasis(x,y,z); RequireRotation(axes);
            Vec[] basis={axes.Rotate(new Vec(1,0,0)),axes.Rotate(new Vec(0,1,0)),axes.Rotate(new Vec(0,0,1))};
            double[] values={principal.X/scale,principal.Y/scale,principal.Z/scale};
            for (int i=0;i<3;i++) for (int j=0;j<3;j++)
            {
                double reconstructed=0;
                for (int k=0;k<3;k++) reconstructed+=values[k]*Component(basis[k],i)*Component(basis[k],j);
                if (Math.Abs(reconstructed-original[i,j]/scale)>ReconstructionTolerance)
                    throw new ArgumentException("Principal inertia reconstruction exceeds its scale-relative residual bound.");
            }
        }
        static Rotation FromBasis(Vec x, Vec y, Vec z)
        {
            double qx,qy,qz,qw,trace=x.X+y.Y+z.Z;
            if (trace>0) { double s=Math.Sqrt(trace+1)*2; qw=s/4;qx=(y.Z-z.Y)/s;qy=(z.X-x.Z)/s;qz=(x.Y-y.X)/s; }
            else if (x.X>=y.Y&&x.X>=z.Z) { double s=Math.Sqrt(1+x.X-y.Y-z.Z)*2;qw=(y.Z-z.Y)/s;qx=s/4;qy=(y.X+x.Y)/s;qz=(z.X+x.Z)/s; }
            else if (y.Y>=z.Z) { double s=Math.Sqrt(1+y.Y-x.X-z.Z)*2;qw=(z.X-x.Z)/s;qx=(y.X+x.Y)/s;qy=s/4;qz=(z.Y+y.Z)/s; }
            else { double s=Math.Sqrt(1+z.Z-x.X-y.Y)*2;qw=(x.Y-y.X)/s;qx=(z.X+x.Z)/s;qy=(z.Y+y.Z)/s;qz=s/4; }
            if (qw<0 || qw==0&&(qx<0 || qx==0&&(qy<0 || qy==0&&qz<0))) { qx=-qx;qy=-qy;qz=-qz;qw=-qw; }
            return new Rotation(qx,qy,qz,qw);
        }
        static Vec CanonicalAxis(Vec axis)
        {
            int largest=0; if (Math.Abs(axis.Y)>Math.Abs(axis.X))largest=1;
            if (Math.Abs(axis.Z)>Math.Abs(Component(axis,largest)))largest=2;
            return Normalize(Component(axis,largest)<0?axis*(-1):axis);
        }
        static Vec Normalize(Vec v) { double length=Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);AssemblyModel.Positive(length);return v*(1/length); }
        static double Component(Vec v,int i) {return i==0?v.X:i==1?v.Y:v.Z;}
        static Vec Subtract(Vec a,Vec b) {return new Vec(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
        internal static void RequireVector(Vec v) {AssemblyModel.Finite(v.X);AssemblyModel.Finite(v.Y);AssemblyModel.Finite(v.Z);}
        internal static void RequireRotation(Rotation q)
        {
            AssemblyModel.Finite(q.X);AssemblyModel.Finite(q.Y);AssemblyModel.Finite(q.Z);AssemblyModel.Finite(q.W);
            double square=q.X*q.X+q.Y*q.Y+q.Z*q.Z+q.W*q.W;
            if (double.IsInfinity(square)||Math.Abs(square-1)>1e-12)
                throw new ArgumentException("Rotation must be normalized; default or numerically invalid rotations are not accepted.");
        }
        internal struct Sum
        {
            double value,correction;
            public double Value { get { return value; } }
            public void Add(double term)
            {
                AssemblyModel.Finite(term); double adjusted=term-correction,next=value+adjusted;
                AssemblyModel.Finite(next); correction=(next-value)-adjusted; value=next;
                AssemblyModel.Finite(correction);
            }
        }
    }
}
