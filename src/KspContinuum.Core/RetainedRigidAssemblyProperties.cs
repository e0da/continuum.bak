using System;
using System.Collections.Generic;

namespace KspContinuum
{
    public sealed class RetainedRigidAssemblyProperties
    {
        readonly RigidAssemblyMember[] members;
        readonly Dictionary<int, int> indices;
        readonly Vec[] centers, axisX, axisY, axisZ;

        public RetainedRigidAssemblyProperties(IEnumerable<RigidAssemblyMember> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var admitted = new List<RigidAssemblyMember>();
            indices = new Dictionary<int, int>();
            foreach (var member in source)
            {
                if (member == null || indices.ContainsKey(member.Id))
                    throw new ArgumentException("Members must be nonnull with unique stable IDs.", "source");
                indices.Add(member.Id, admitted.Count); admitted.Add(member);
            }
            if (admitted.Count == 0) throw new ArgumentException("At least one member is required.", "source");
            admitted.Sort((a, b) => a.Id.CompareTo(b.Id));
            members = admitted.ToArray(); centers = new Vec[members.Length];
            axisX = new Vec[members.Length]; axisY = new Vec[members.Length]; axisZ = new Vec[members.Length];
            indices.Clear();
            for (int i = 0; i < members.Length; i++)
            {
                indices.Add(members[i].Id, i);
                Prepare(members[i], out centers[i], out axisX[i], out axisY[i], out axisZ[i]);
            }
        }

        public int Count { get { return members.Length; } }
        public int GetId(int index) { CheckIndex(index); return members[index].Id; }
        public RigidAssemblyMember GetMember(int index) { CheckIndex(index); return members[index]; }
        public void Update(RigidAssemblyMember member)
        {
            int index;
            if (member == null || !indices.TryGetValue(member.Id, out index))
                throw new ArgumentException("Member does not belong to the admitted topology.", "member");
            Update(index, member);
        }
        public void Update(int index, RigidAssemblyMember member)
        {
            CheckIndex(index);
            if (member == null || member.Id != members[index].Id)
                throw new ArgumentException("Member ID must match the admitted slot.", "member");
            RigidAssemblyMember old = members[index];
            bool centerChanged = !Same(old.LocalPosition, member.LocalPosition) ||
                !Same(old.LocalRotation, member.LocalRotation) || !Same(old.LocalCenterOfMass, member.LocalCenterOfMass);
            bool axesChanged = !Same(old.LocalRotation, member.LocalRotation) || !Same(old.PrincipalRotation, member.PrincipalRotation);
            Vec center = centers[index], a = axisX[index], b = axisY[index], c = axisZ[index];
            if (centerChanged) center = Center(member);
            if (axesChanged) Axes(member, out a, out b, out c);
            members[index] = member; centers[index] = center; axisX[index] = a; axisY[index] = b; axisZ[index] = c;
        }
        public void UpdateAll(IEnumerable<RigidAssemblyMember> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var next = new RigidAssemblyMember[members.Length];
            var nextCenters = new Vec[members.Length];
            var nextA = new Vec[members.Length]; var nextB = new Vec[members.Length]; var nextC = new Vec[members.Length];
            int count = 0;
            foreach (var member in source)
            {
                int index;
                if (member == null || !indices.TryGetValue(member.Id, out index) || next[index] != null)
                    throw new ArgumentException("Update must contain the exact admitted topology once.", "source");
                Prepare(member, out nextCenters[index], out nextA[index], out nextB[index], out nextC[index]);
                next[index] = member; count++;
            }
            if (count != members.Length) throw new ArgumentException("Update is missing admitted members.", "source");
            Array.Copy(next, members, count); Array.Copy(nextCenters, centers, count);
            Array.Copy(nextA, axisX, count); Array.Copy(nextB, axisY, count); Array.Copy(nextC, axisZ, count);
        }

        public RigidAssemblyProperties Combine()
        {
            var total = new RigidAssemblyProperties.Sum();
            for (int i = 0; i < members.Length; i++) total.Add(members[i].Mass);
            double mass = total.Value; AssemblyModel.Positive(mass);
            var cx = new RigidAssemblyProperties.Sum(); var cy = new RigidAssemblyProperties.Sum(); var cz = new RigidAssemblyProperties.Sum();
            for (int i = 0; i < members.Length; i++)
            {
                double weight = members[i].Mass / mass; Vec center = centers[i];
                cx.Add(center.X * weight); cy.Add(center.Y * weight); cz.Add(center.Z * weight);
            }
            Vec com = new Vec(cx.Value, cy.Value, cz.Value); RigidAssemblyProperties.RequireVector(com);
            var xx = new RigidAssemblyProperties.Sum(); var xy = new RigidAssemblyProperties.Sum(); var xz = new RigidAssemblyProperties.Sum();
            var yy = new RigidAssemblyProperties.Sum(); var yz = new RigidAssemblyProperties.Sum(); var zz = new RigidAssemblyProperties.Sum();
            for (int i = 0; i < members.Length; i++)
            {
                Vec a = axisX[i], b = axisY[i], c = axisZ[i]; Vec p = members[i].PrincipalInertia;
                Vec d = new Vec(centers[i].X - com.X, centers[i].Y - com.Y, centers[i].Z - com.Z);
                RigidAssemblyProperties.RequireVector(d);
                Vec u = d * Math.Sqrt(members[i].Mass); RigidAssemblyProperties.RequireVector(u);
                xx.Add(p.X*a.X*a.X + p.Y*b.X*b.X + p.Z*c.X*c.X + u.Y*u.Y + u.Z*u.Z);
                xy.Add(p.X*a.X*a.Y + p.Y*b.X*b.Y + p.Z*c.X*c.Y - u.X*u.Y);
                xz.Add(p.X*a.X*a.Z + p.Y*b.X*b.Z + p.Z*c.X*c.Z - u.X*u.Z);
                yy.Add(p.X*a.Y*a.Y + p.Y*b.Y*b.Y + p.Z*c.Y*c.Y + u.X*u.X + u.Z*u.Z);
                yz.Add(p.X*a.Y*a.Z + p.Y*b.Y*b.Z + p.Z*c.Y*c.Z - u.Y*u.Z);
                zz.Add(p.X*a.Z*a.Z + p.Y*b.Z*b.Z + p.Z*c.Z*c.Z + u.X*u.X + u.Y*u.Y);
            }
            var tensor = new InertiaTensor6Dof(xx.Value, xy.Value, xz.Value, yy.Value, yz.Value, zz.Value);
            Vec principal; Rotation axes;
            RigidAssemblyProperties.Diagonalize(tensor, out principal, out axes);
            return new RigidAssemblyProperties(mass, com, tensor, principal, axes);
        }

        void CheckIndex(int index) { if (index < 0 || index >= members.Length) throw new ArgumentOutOfRangeException("index"); }
        static Vec Center(RigidAssemblyMember member)
        {
            Vec center = member.LocalPosition + member.LocalRotation.Rotate(member.LocalCenterOfMass);
            RigidAssemblyProperties.RequireVector(center); return center;
        }
        static void Axes(RigidAssemblyMember member, out Vec a, out Vec b, out Vec c)
        {
            Rotation q = member.LocalRotation * member.PrincipalRotation; RigidAssemblyProperties.RequireRotation(q);
            a = q.Rotate(new Vec(1, 0, 0)); b = q.Rotate(new Vec(0, 1, 0)); c = q.Rotate(new Vec(0, 0, 1));
        }
        static void Prepare(RigidAssemblyMember member, out Vec center, out Vec a, out Vec b, out Vec c)
        { center = Center(member); Axes(member, out a, out b, out c); }
        static bool Same(double a, double b) { return BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b); }
        static bool Same(Vec a, Vec b) { return Same(a.X,b.X) && Same(a.Y,b.Y) && Same(a.Z,b.Z); }
        static bool Same(Rotation a, Rotation b) { return Same(a.X,b.X) && Same(a.Y,b.Y) && Same(a.Z,b.Z) && Same(a.W,b.W); }
    }
}
