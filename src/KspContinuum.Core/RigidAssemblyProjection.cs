using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public sealed class RigidAssemblyMotion
    {
        // Velocity is the member's COM velocity; both vectors use the member
        // properties' common assembly-frame axes, not its own principal axes.
        public RigidAssemblyMotion(RigidAssemblyMember member, Vec velocity, Vec angularVelocity)
        {
            if (member == null) throw new ArgumentNullException("member");
            RigidAssemblyProperties.RequireVector(velocity);
            RigidAssemblyProperties.RequireVector(angularVelocity);
            Member = member; Velocity = velocity; AngularVelocity = angularVelocity;
        }
        public RigidAssemblyMember Member { get; private set; }
        public Vec Velocity { get; private set; }
        public Vec AngularVelocity { get; private set; }
    }

    public sealed class RigidAssemblyProjection
    {
        // These are numerical admission bounds, not native/stock equivalence budgets.
        public const double MinimumPrincipalMomentRatio = 1e-12;
        public const double MomentumResidualRelativeTolerance = 1e-10;
        public const double EnergyRoundoffRelativeTolerance = 1e-10;

        RigidAssemblyProjection(RigidAssemblyProperties properties, Vec momentum, Vec angularMomentum,
            Vec velocity, Vec angularVelocity, double sourceEnergy, double rigidEnergy)
        {
            Properties = properties; LinearMomentum = momentum; AngularMomentumAboutCenter = angularMomentum;
            Velocity = velocity; AngularVelocity = angularVelocity;
            SourceKineticEnergy = sourceEnergy; RigidKineticEnergy = rigidEnergy;
            // Retain a tiny negative difference within the declared roundoff bound.
            RemovedKineticEnergy = sourceEnergy - rigidEnergy;
        }
        public RigidAssemblyProperties Properties { get; private set; }
        public Vec LinearMomentum { get; private set; }
        public Vec AngularMomentumAboutCenter { get; private set; }
        public Vec Velocity { get; private set; }
        public Vec AngularVelocity { get; private set; }
        public double SourceKineticEnergy { get; private set; }
        public double RigidKineticEnergy { get; private set; }
        public double RemovedKineticEnergy { get; private set; }

        public static RigidAssemblyProjection Project(IEnumerable<RigidAssemblyMotion> motions)
        {
            if (motions == null) throw new ArgumentNullException("motions");
            var ordered = new List<RigidAssemblyMotion>(); var ids = new HashSet<int>();
            foreach (var motion in motions)
            {
                if (motion == null || !ids.Add(motion.Member.Id))
                    throw new ArgumentException("Motions must be nonnull with unique member IDs.", "motions");
                ordered.Add(motion);
            }
            if (ordered.Count == 0) throw new ArgumentException("At least one motion is required.", "motions");
            ordered.Sort((a, b) => a.Member.Id.CompareTo(b.Member.Id));
            var members = new List<RigidAssemblyMember>(ordered.Count);
            foreach (var motion in ordered) members.Add(motion.Member);
            RigidAssemblyProperties properties = RigidAssemblyProperties.Combine(members);
            double ratio = properties.PrincipalInertia.X / properties.PrincipalInertia.Z;
            if (!(ratio >= MinimumPrincipalMomentRatio))
                throw new ArgumentException("Aggregate inertia is singular or exceeds the projection conditioning bound.", "motions");

            var px = new Sum(); var py = new Sum(); var pz = new Sum();
            var lx = new Sum(); var ly = new Sum(); var lz = new Sum(); var source = new Sum();
            foreach (var motion in ordered)
            {
                RigidAssemblyMember member = motion.Member;
                Vec center = member.LocalPosition + member.LocalRotation.Rotate(member.LocalCenterOfMass);
                RigidAssemblyProperties.RequireVector(center);
                Vec arm = Subtract(center, properties.CenterOfMass);
                RigidAssemblyProperties.RequireVector(arm);
                Vec momentum = motion.Velocity * member.Mass;
                RigidAssemblyProperties.RequireVector(momentum);
                Rotation principal = member.LocalRotation * member.PrincipalRotation;
                RigidAssemblyProperties.RequireRotation(principal);
                Vec principalOmega = principal.Inverse.Rotate(motion.AngularVelocity);
                RigidAssemblyProperties.RequireVector(principalOmega);
                Vec spin = principal.Rotate(Vec.Product(member.PrincipalInertia, principalOmega));
                RigidAssemblyProperties.RequireVector(spin);
                Vec orbital = Vec.Cross(arm, momentum);
                RigidAssemblyProperties.RequireVector(orbital);
                px.Add(momentum.X); py.Add(momentum.Y); pz.Add(momentum.Z);
                lx.Add(spin.X); lx.Add(orbital.X); ly.Add(spin.Y); ly.Add(orbital.Y); lz.Add(spin.Z); lz.Add(orbital.Z);
                source.Add(Energy(motion.Velocity * Math.Sqrt(member.Mass)));
                source.Add(PrincipalEnergy(principalOmega, member.PrincipalInertia));
            }
            Vec linearMomentum = new Vec(px.Value, py.Value, pz.Value);
            Vec angularMomentum = new Vec(lx.Value, ly.Value, lz.Value);
            Vec velocity = Divide(linearMomentum, properties.Mass);
            RigidAssemblyProperties.RequireVector(velocity);
            Vec angularVelocity = Solve(properties.Inertia, angularMomentum);
            RequireResidual(velocity * properties.Mass, linearMomentum);
            RequireResidual(properties.Inertia.Multiply(angularVelocity), angularMomentum);

            var rigid = new Sum(); rigid.Add(Energy(velocity * Math.Sqrt(properties.Mass)));
            rigid.Add(PrincipalEnergy(properties.PrincipalRotation.Inverse.Rotate(angularVelocity), properties.PrincipalInertia));
            double allowance = Math.Max(source.Value, rigid.Value) * EnergyRoundoffRelativeTolerance;
            if (rigid.Value > source.Value && rigid.Value - source.Value > allowance)
                throw new ArgumentException("Rigid projection increases energy beyond its numerical roundoff bound.", "motions");
            return new RigidAssemblyProjection(properties, linearMomentum, angularMomentum,
                velocity, angularVelocity, source.Value, rigid.Value);
        }

        // Scaled Cholesky solves the complete symmetric tensor in assembly axes.
        // Scaling avoids an absolute determinant threshold tied to particular units.
        static Vec Solve(InertiaTensor6Dof t, Vec momentum)
        {
            double scale = Math.Max(t.XX, Math.Max(t.YY, t.ZZ)); AssemblyModel.Positive(scale);
            double l00 = Root(t.XX / scale);
            double l10 = (t.XY / scale) / l00, l20 = (t.XZ / scale) / l00;
            double l11 = Root(t.YY / scale - l10 * l10);
            double l21 = (t.YZ / scale - l20 * l10) / l11;
            double l22 = Root(t.ZZ / scale - l20 * l20 - l21 * l21);
            double y0 = (momentum.X / scale) / l00;
            double y1 = (momentum.Y / scale - l10 * y0) / l11;
            double y2 = (momentum.Z / scale - l20 * y0 - l21 * y1) / l22;
            double z = y2 / l22, y = (y1 - l21 * z) / l11;
            Vec result = new Vec((y0 - l10 * y - l20 * z) / l00, y, z);
            RigidAssemblyProperties.RequireVector(result); return result;
        }
        static double Root(double value) { AssemblyModel.Positive(value); return Math.Sqrt(value); }
        static Vec Divide(Vec v, double scale) { return new Vec(v.X / scale, v.Y / scale, v.Z / scale); }
        static Vec Subtract(Vec a, Vec b) { return new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        static double PrincipalEnergy(Vec omega, Vec inertia)
        { return Energy(new Vec(omega.X * Math.Sqrt(inertia.X), omega.Y * Math.Sqrt(inertia.Y), omega.Z * Math.Sqrt(inertia.Z))); }
        static double Energy(Vec weightedVelocity)
        {
            RigidAssemblyProperties.RequireVector(weightedVelocity);
            var sum = new Sum();
            sum.Add((weightedVelocity.X * .5) * weightedVelocity.X);
            sum.Add((weightedVelocity.Y * .5) * weightedVelocity.Y);
            sum.Add((weightedVelocity.Z * .5) * weightedVelocity.Z);
            return sum.Value;
        }
        static void RequireResidual(Vec actual, Vec expected)
        {
            RigidAssemblyProperties.RequireVector(actual); RigidAssemblyProperties.RequireVector(expected);
            double scale = Math.Max(MaxAbs(actual), MaxAbs(expected));
            if (scale == 0) return;
            if (Math.Abs(actual.X / scale - expected.X / scale) > MomentumResidualRelativeTolerance ||
                Math.Abs(actual.Y / scale - expected.Y / scale) > MomentumResidualRelativeTolerance ||
                Math.Abs(actual.Z / scale - expected.Z / scale) > MomentumResidualRelativeTolerance)
                throw new ArgumentException("Projection momentum reconstruction exceeds its numerical residual bound.");
        }
        static double MaxAbs(Vec v) { return Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))); }
        struct Sum
        {
            double value, correction;
            public double Value { get { return value; } }
            public void Add(double term)
            {
                AssemblyModel.Finite(term); double adjusted = term - correction, next = value + adjusted;
                AssemblyModel.Finite(next); correction = (next - value) - adjusted; value = next;
                AssemblyModel.Finite(correction);
            }
        }
    }
}
