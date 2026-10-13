using System;

namespace KspContinuum
{
    public struct OwnedRuntimeMotion
    {
        public Vec RootPosition, Velocity, AngularMomentum;
        public Rotation Orientation;
    }

    public static class OwnedRuntimeDynamics
    {
        public static Vec AngularVelocity(RigidAssemblyProperties properties, Rotation orientation, Vec momentum)
        {
            Rotation axes = orientation * properties.PrincipalRotation;
            Vec local = axes.Inverse.Rotate(momentum), inertia = properties.PrincipalInertia;
            return axes.Rotate(new Vec(local.X / inertia.X, local.Y / inertia.Y, local.Z / inertia.Z));
        }

        public static Vec Momentum(RigidAssemblyProperties properties, Rotation orientation, Vec omega)
        {
            Rotation axes = orientation * properties.PrincipalRotation;
            return axes.Rotate(Vec.Product(axes.Inverse.Rotate(omega), properties.PrincipalInertia));
        }

        public static void Step(ref OwnedRuntimeMotion motion, RigidAssemblyProperties properties,
            Vec force, Vec torque, double dt)
        { StepCoupled(ref motion,properties,force,torque,new Vec(),new Vec(),dt); }

        public static void StepCoupled(ref OwnedRuntimeMotion motion, RigidAssemblyProperties properties,
            Vec force, Vec externalTorque, Vec oldInternalMomentumLocal, Vec newInternalMomentumLocal, double dt)
        {
            if (properties == null) throw new ArgumentNullException("properties");
            AssemblyModel.Positive(dt); Require(force); Require(externalTorque); Require(motion.RootPosition);
            Require(oldInternalMomentumLocal); Require(newInternalMomentumLocal);
            Require(motion.Velocity); Require(motion.AngularMomentum);
            Vec oldCenterOffset = motion.Orientation.Rotate(properties.CenterOfMass);
            Vec total = motion.AngularMomentum + motion.Orientation.Rotate(oldInternalMomentumLocal);
            Vec nextMomentum = total + externalTorque * dt;
            Vec middleMomentum = total + externalTorque * (.5 * dt);
            Vec middleInternal=(oldInternalMomentumLocal+newInternalMomentumLocal)*.5;
            Vec omega = AngularVelocity(properties, motion.Orientation,
                middleMomentum + motion.Orientation.Rotate(middleInternal)*-1);
            Rotation middle = Advance(motion.Orientation, omega, .5 * dt);
            omega = AngularVelocity(properties, middle, middleMomentum+middle.Rotate(middleInternal)*-1);
            Rotation next = Advance(motion.Orientation, omega, dt);
            Vec acceleration = force * (1 / properties.Mass);
            Vec rootDisplacement=motion.Velocity*dt+acceleration*(.5*dt*dt)
                +oldCenterOffset+next.Rotate(properties.CenterOfMass)*-1;
            motion.Velocity = motion.Velocity + acceleration * dt;
            motion.AngularMomentum = nextMomentum+next.Rotate(newInternalMomentumLocal)*-1;
            motion.Orientation = next;
            motion.RootPosition = motion.RootPosition+rootDisplacement;
            Require(motion.RootPosition); Require(motion.Velocity); Require(motion.AngularMomentum);
        }

        public static void ChangeProperties(ref OwnedRuntimeMotion motion, RigidAssemblyProperties before,
            RigidAssemblyProperties after)
        {
            Vec omega = AngularVelocity(before, motion.Orientation, motion.AngularMomentum);
            Vec displacement = motion.Orientation.Rotate(after.CenterOfMass + before.CenterOfMass * -1);
            motion.Velocity = motion.Velocity + Vec.Cross(omega, displacement);
            motion.AngularMomentum = Momentum(after, motion.Orientation, omega);
        }

        static Rotation Advance(Rotation rotation, Vec omega, double dt)
        {
            double magnitude = Math.Sqrt(Dot(omega, omega));
            if (magnitude == 0) return rotation;
            return Rotation.AxisAngle(omega, magnitude * dt) * rotation;
        }
        internal static double Dot(Vec a, Vec b) { return a.X*b.X + a.Y*b.Y + a.Z*b.Z; }
        internal static void Require(Vec a) { AssemblyModel.Finite(a.X); AssemblyModel.Finite(a.Y); AssemblyModel.Finite(a.Z); }
    }
}
