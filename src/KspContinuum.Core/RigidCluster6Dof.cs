using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KspContinuum
{
    public struct Rotation
    {
        public readonly double X, Y, Z, W;

        public Rotation(double x, double y, double z, double w)
        {
            AssemblyModel.Finite(x); AssemblyModel.Finite(y); AssemblyModel.Finite(z); AssemblyModel.Finite(w);
            double norm = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (norm <= 0) throw new ArgumentException("Rotation must have nonzero magnitude.");
            X = x / norm; Y = y / norm; Z = z / norm; W = w / norm;
        }

        Rotation(double x, double y, double z, double w, bool retained)
        { X = x; Y = y; Z = z; W = w; }

        public static Rotation FromUnitComponents(double x, double y, double z, double w)
        {
            AssemblyModel.Finite(x); AssemblyModel.Finite(y); AssemblyModel.Finite(z); AssemblyModel.Finite(w);
            if (Math.Abs(x*x+y*y+z*z+w*w-1) > 1e-12)
                throw new ArgumentException("Retained rotation must already be unit length.");
            return new Rotation(x,y,z,w,true);
        }

        public static Rotation Identity { get { return new Rotation(0, 0, 0, 1); } }

        public static Rotation AxisAngle(Vec axis, double radians)
        {
            AssemblyModel.Finite(radians);
            double length = Math.Sqrt(Dot(axis, axis));
            if (length <= 0) throw new ArgumentException("Rotation axis must have nonzero magnitude.", "axis");
            double half = radians * .5, scale = Math.Sin(half) / length;
            return new Rotation(axis.X * scale, axis.Y * scale, axis.Z * scale, Math.Cos(half));
        }

        public Rotation Inverse { get { return new Rotation(-X, -Y, -Z, W); } }

        public Vec Rotate(Vec value)
        {
            Vec q = new Vec(X, Y, Z);
            Vec crossed = Vec.Cross(q, value);
            return value + crossed * (2 * W) + Vec.Cross(q, crossed) * 2;
        }

        public static Rotation operator *(Rotation a, Rotation b)
        {
            return new Rotation(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        public static double Dot(Rotation a, Rotation b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W; }
        static double Dot(Vec a, Vec b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
    }

    public sealed class RigidBody6Dof
    {
        public RigidBody6Dof(int id, double mass, Vec position, Rotation orientation,
            Vec linearVelocity, Vec angularVelocity, Vec principalInertia)
        {
            if (id < 0) throw new ArgumentException("Body ID must be nonnegative.", "id");
            AssemblyModel.Positive(mass); Validate(position); Validate(linearVelocity); Validate(angularVelocity);
            AssemblyModel.Positive(principalInertia.X); AssemblyModel.Positive(principalInertia.Y); AssemblyModel.Positive(principalInertia.Z);
            Id = id; Mass = mass; Position = position; Orientation = orientation;
            LinearVelocity = linearVelocity; AngularVelocity = angularVelocity; PrincipalInertia = principalInertia;
        }

        public int Id { get; private set; }
        public double Mass { get; private set; }
        public Vec Position { get; private set; }
        public Rotation Orientation { get; private set; }
        public Vec LinearVelocity { get; private set; }
        public Vec AngularVelocity { get; private set; }
        public Vec PrincipalInertia { get; private set; }

        static void Validate(Vec value)
        { AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z); }
    }

    public sealed class RigidPose6Dof
    {
        internal RigidPose6Dof(int id, Vec position, Rotation orientation, Vec linearVelocity, Vec angularVelocity)
        { Id = id; Position = position; Orientation = orientation; LinearVelocity = linearVelocity; AngularVelocity = angularVelocity; }
        public int Id { get; private set; }
        public Vec Position { get; private set; }
        public Rotation Orientation { get; private set; }
        public Vec LinearVelocity { get; private set; }
        public Vec AngularVelocity { get; private set; }
    }

    public struct InertiaTensor6Dof
    {
        internal InertiaTensor6Dof(double xx, double xy, double xz, double yy, double yz, double zz)
        { XX = xx; XY = xy; XZ = xz; YY = yy; YZ = yz; ZZ = zz; }
        public double XX { get; private set; }
        public double XY { get; private set; }
        public double XZ { get; private set; }
        public double YY { get; private set; }
        public double YZ { get; private set; }
        public double ZZ { get; private set; }
        public Vec Multiply(Vec value)
        {
            return new Vec(XX * value.X + XY * value.Y + XZ * value.Z,
                XY * value.X + YY * value.Y + YZ * value.Z,
                XZ * value.X + YZ * value.Y + ZZ * value.Z);
        }
    }

    public sealed class RigidCluster6Dof
    {
        sealed class Member
        {
            public int Id;
            public Vec LocalPosition;
            public Rotation LocalOrientation;
        }

        readonly Member[] members;
        readonly Matrix3 localInertia;

        RigidCluster6Dof(double mass, Vec center, Rotation orientation, Vec linearMomentum,
            Vec angularMomentum, Matrix3 inertia, Member[] members)
        {
            TotalMass = mass; CenterOfMass = center; Orientation = orientation;
            LinearMomentum = linearMomentum; AngularMomentum = angularMomentum;
            localInertia = inertia; this.members = members;
        }

        public double TotalMass { get; private set; }
        public Vec CenterOfMass { get; private set; }
        public Rotation Orientation { get; private set; }
        public Vec LinearMomentum { get; private set; }
        public Vec AngularMomentum { get; private set; }
        public InertiaTensor6Dof LocalInertia { get { return localInertia.Public; } }
        public InertiaTensor6Dof WorldInertia { get { return WorldInertiaMatrix().Public; } }
        public Vec LinearVelocity { get { return LinearMomentum * (1 / TotalMass); } }
        public Vec AngularVelocity { get { return WorldInertiaMatrix().Inverse().Multiply(AngularMomentum); } }

        public static RigidCluster6Dof Capture(IEnumerable<RigidBody6Dof> source)
        {
            if (source == null) throw new ArgumentException("Bodies are required.", "source");
            var bodies = new List<RigidBody6Dof>(); var ids = new HashSet<int>();
            double mass = 0; Vec weightedCenter = new Vec(), momentum = new Vec();
            foreach (RigidBody6Dof body in source)
            {
                if (body == null || !ids.Add(body.Id)) throw new ArgumentException("Bodies must be nonnull with unique IDs.", "source");
                bodies.Add(body); mass += body.Mass; weightedCenter += body.Position * body.Mass; momentum += body.LinearVelocity * body.Mass;
            }
            if (bodies.Count == 0) throw new ArgumentException("At least one body is required.", "source");
            AssemblyModel.Positive(mass);
            Vec center = weightedCenter * (1 / mass);
            Rotation orientation = bodies[0].Orientation;
            Rotation inverse = orientation.Inverse;
            var members = new Member[bodies.Count];
            Matrix3 inertia = new Matrix3(); Vec angularMomentum = new Vec();
            for (int i = 0; i < bodies.Count; i++)
            {
                RigidBody6Dof body = bodies[i];
                Vec worldOffset = Subtract(body.Position, center);
                Vec localOffset = inverse.Rotate(worldOffset);
                Rotation localOrientation = inverse * body.Orientation;
                members[i] = new Member { Id = body.Id, LocalPosition = localOffset, LocalOrientation = localOrientation };
                Matrix3 bodyLocal = Matrix3.RotateDiagonal(localOrientation, body.PrincipalInertia);
                inertia += bodyLocal + Matrix3.ParallelAxis(body.Mass, localOffset);
                Matrix3 bodyWorld = Matrix3.RotateDiagonal(body.Orientation, body.PrincipalInertia);
                angularMomentum += bodyWorld.Multiply(body.AngularVelocity) + Vec.Cross(worldOffset, body.LinearVelocity * body.Mass);
            }
            inertia.RequirePositiveDefinite();
            return new RigidCluster6Dof(mass, center, orientation, momentum, angularMomentum, inertia, members);
        }

        public RigidCluster6Dof ApplyImpulse(Vec worldPoint, Vec impulse)
        {
            Validate(worldPoint); Validate(impulse);
            Vec angular = AngularMomentum + Vec.Cross(Subtract(worldPoint, CenterOfMass), impulse);
            return new RigidCluster6Dof(TotalMass, CenterOfMass, Orientation, LinearMomentum + impulse, angular, localInertia, members);
        }

        public RigidCluster6Dof Advance(double stepSeconds)
        {
            AssemblyModel.Positive(stepSeconds);
            Vec velocity = LinearVelocity, omega = AngularVelocity;
            double speed = Math.Sqrt(Dot(omega, omega));
            Rotation nextOrientation = speed == 0 ? Orientation : Rotation.AxisAngle(omega, speed * stepSeconds) * Orientation;
            Vec nextCenter = CenterOfMass + velocity * stepSeconds;
            return new RigidCluster6Dof(TotalMass, nextCenter, nextOrientation, LinearMomentum, AngularMomentum, localInertia, members);
        }

        public RigidCluster6Dof AdvanceFrozenAcceleration(Vec acceleration, double stepSeconds)
        {
            Validate(acceleration); AssemblyModel.Positive(stepSeconds);
            Vec halfImpulse = acceleration * (TotalMass * stepSeconds * .5);
            RigidCluster6Dof halfKicked = ApplyImpulse(CenterOfMass, halfImpulse);
            RigidCluster6Dof advanced = halfKicked.Advance(stepSeconds);
            return advanced.ApplyImpulse(advanced.CenterOfMass, halfImpulse);
        }

        public IReadOnlyList<RigidPose6Dof> Reconstruct()
        {
            var result = new RigidPose6Dof[members.Length];
            Vec omega = AngularVelocity, velocity = LinearVelocity;
            for (int i = 0; i < members.Length; i++)
            {
                Member member = members[i]; Vec offset = Orientation.Rotate(member.LocalPosition);
                result[i] = new RigidPose6Dof(member.Id, CenterOfMass + offset, Orientation * member.LocalOrientation,
                    velocity + Vec.Cross(omega, offset), omega);
            }
            return new ReadOnlyCollection<RigidPose6Dof>(result);
        }

        Matrix3 WorldInertiaMatrix() { return Matrix3.Rotate(Orientation, localInertia); }
        static Vec Subtract(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        static double Dot(Vec a, Vec b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        static void Validate(Vec value) { AssemblyModel.Finite(value.X); AssemblyModel.Finite(value.Y); AssemblyModel.Finite(value.Z); }

        struct Matrix3
        {
            public double M00, M01, M02, M10, M11, M12, M20, M21, M22;
            public InertiaTensor6Dof Public { get { return new InertiaTensor6Dof(M00, M01, M02, M11, M12, M22); } }
            public static Matrix3 operator +(Matrix3 a, Matrix3 b)
            {
                return new Matrix3 { M00=a.M00+b.M00, M01=a.M01+b.M01, M02=a.M02+b.M02,
                    M10=a.M10+b.M10, M11=a.M11+b.M11, M12=a.M12+b.M12,
                    M20=a.M20+b.M20, M21=a.M21+b.M21, M22=a.M22+b.M22 };
            }
            public Vec Multiply(Vec v) { return new Vec(M00*v.X+M01*v.Y+M02*v.Z, M10*v.X+M11*v.Y+M12*v.Z, M20*v.X+M21*v.Y+M22*v.Z); }
            public Matrix3 Multiply(Matrix3 b)
            {
                Matrix3 a=this; return new Matrix3 {
                    M00=a.M00*b.M00+a.M01*b.M10+a.M02*b.M20, M01=a.M00*b.M01+a.M01*b.M11+a.M02*b.M21, M02=a.M00*b.M02+a.M01*b.M12+a.M02*b.M22,
                    M10=a.M10*b.M00+a.M11*b.M10+a.M12*b.M20, M11=a.M10*b.M01+a.M11*b.M11+a.M12*b.M21, M12=a.M10*b.M02+a.M11*b.M12+a.M12*b.M22,
                    M20=a.M20*b.M00+a.M21*b.M10+a.M22*b.M20, M21=a.M20*b.M01+a.M21*b.M11+a.M22*b.M21, M22=a.M20*b.M02+a.M21*b.M12+a.M22*b.M22 };
            }
            public Matrix3 Transpose() { return new Matrix3 { M00=M00,M01=M10,M02=M20,M10=M01,M11=M11,M12=M21,M20=M02,M21=M12,M22=M22 }; }
            public Matrix3 Inverse()
            {
                double c00=M11*M22-M12*M21, c01=M12*M20-M10*M22, c02=M10*M21-M11*M20;
                double determinant=M00*c00+M01*c01+M02*c02;
                if (!(determinant > 0) || double.IsInfinity(determinant)) throw new InvalidOperationException("Cluster inertia is singular.");
                double s=1/determinant;
                return new Matrix3 { M00=c00*s, M01=(M02*M21-M01*M22)*s, M02=(M01*M12-M02*M11)*s,
                    M10=c01*s, M11=(M00*M22-M02*M20)*s, M12=(M02*M10-M00*M12)*s,
                    M20=c02*s, M21=(M01*M20-M00*M21)*s, M22=(M00*M11-M01*M10)*s };
            }
            public void RequirePositiveDefinite()
            {
                AssemblyModel.Positive(M00);
                AssemblyModel.Positive(M00*M11-M01*M10);
                AssemblyModel.Positive(M00*(M11*M22-M12*M21)-M01*(M10*M22-M12*M20)+M02*(M10*M21-M11*M20));
            }
            public static Matrix3 ParallelAxis(double mass, Vec r)
            {
                double xx=r.X*r.X, yy=r.Y*r.Y, zz=r.Z*r.Z;
                return new Matrix3 { M00=mass*(yy+zz), M01=-mass*r.X*r.Y, M02=-mass*r.X*r.Z,
                    M10=-mass*r.Y*r.X, M11=mass*(xx+zz), M12=-mass*r.Y*r.Z,
                    M20=-mass*r.Z*r.X, M21=-mass*r.Z*r.Y, M22=mass*(xx+yy) };
            }
            public static Matrix3 RotateDiagonal(Rotation q, Vec diagonal)
            {
                return Rotate(q, new Matrix3 { M00=diagonal.X, M11=diagonal.Y, M22=diagonal.Z });
            }
            public static Matrix3 Rotate(Rotation q, Matrix3 value)
            {
                double xx=q.X*q.X, yy=q.Y*q.Y, zz=q.Z*q.Z, xy=q.X*q.Y, xz=q.X*q.Z, yz=q.Y*q.Z, wx=q.W*q.X, wy=q.W*q.Y, wz=q.W*q.Z;
                var r=new Matrix3 { M00=1-2*(yy+zz),M01=2*(xy-wz),M02=2*(xz+wy), M10=2*(xy+wz),M11=1-2*(xx+zz),M12=2*(yz-wx), M20=2*(xz-wy),M21=2*(yz+wx),M22=1-2*(xx+yy) };
                return r.Multiply(value).Multiply(r.Transpose());
            }
        }
    }
}
