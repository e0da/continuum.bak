using System;

namespace KspContinuum
{
    public struct PersistentTrailParcel
    {
        public long id;
        public double x, y, z, age, width, opacity;
    }
    public sealed class PersistentTrailOwner
    {
        struct Birth { public long id; public double time, x, y, z, wx, wy, wz; }
        readonly Birth[] history;
        readonly double lifetime, initialWidth, diffusion, decay, cadence;
        readonly double[] origin, velocity;
        readonly double[] wind = new double[3], integrated = new double[3];
        int head, count;
        long nextId = 1;
        double nextBirth, emissionEpoch;
        long nextBirthIndex;
        public double Time { get; private set; }
        public long Generation { get; private set; }
        public long Emitted { get; private set; }
        public long Expired { get; private set; }
        public long CapacityEvictions { get; private set; }
        public bool Emission { get; private set; }
        public int Count { get { return count; } }
        public int Capacity { get { return history.Length; } }
        public PersistentTrailOwner(int capacity, double[] origin, double[] velocity, double lifetime = 30,
            double width = .5, double diffusion = .1, double decay = .05, double cadence = .1)
        {
            if (capacity < 1 || capacity > 4096) throw new ArgumentException("Trail capacity 1..4096 required.");
            CheckVector(origin, 1e15); CheckVector(velocity, 1e6);
            Range(lifetime, .01, 3600); Range(width, .001, 1000); Range(diffusion, 0, 1000);
            Range(decay, 0, 100); Range(cadence, .001, 60);
            history = new Birth[capacity]; this.origin = (double[])origin.Clone(); this.velocity = (double[])velocity.Clone();
            this.lifetime = lifetime; initialWidth = width; this.diffusion = diffusion; this.decay = decay; this.cadence = cadence;
        }
        static void Range(double value, double minimum, double maximum)
        { if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum) throw new ArgumentException("Finite bounded trail input required."); }
        static void CheckVector(double[] value, double bound)
        { if (value == null || value.Length != 3) throw new ArgumentException("Three-vector required."); foreach (double v in value) Range(v, -bound, bound); }
        public void SetWind(double x, double y, double z)
        { Range(x, -1e6, 1e6); Range(y, -1e6, 1e6); Range(z, -1e6, 1e6); wind[0] = x; wind[1] = y; wind[2] = z; Generation++; }
        public void SetEmission(bool enabled)
        { if (Emission == enabled) return; Emission = enabled; emissionEpoch = Time; nextBirthIndex = 1; nextBirth = Time + cadence; Generation++; }
        public void Reset()
        { head = count = 0; Time = nextBirth = emissionEpoch = 0; nextBirthIndex = 1; nextId = 1; Emitted = Expired = CapacityEvictions = 0; Array.Clear(integrated, 0, 3); Emission = false; Generation++; }
        public void Advance(double target)
        { AdvanceCore(target, null, null); }
        public void AdvanceMeasured(double target, double[] start, double[] end, bool emission)
        {
            Range(target, Time, Math.Min(1e9, Time + 3600)); CheckVector(start, 1e15); CheckVector(end, 1e15);
            double epoch = emission == Emission ? emissionEpoch : Time;
            double first = emission == Emission ? nextBirth : Time + cadence;
            long index = emission == Emission ? nextBirthIndex : 1;
            int births = emission && first <= target ? checked((int)Math.Floor((target - first) / cadence + 1e-9) + 1) : 0;
            while (births > 0 && epoch + (index + births - 1) * cadence > target) births--;
            if (births > Capacity * 4) throw new ArgumentException("Measured advance exceeds bounded emission work.");
            SetEmission(emission); AdvanceCore(target, start, end);
        }
        void AdvanceCore(double target, double[] measuredStart, double[] measuredEnd)
        {
            Range(target, Time, Math.Min(1e9, Time + 3600));
            double dt = target - Time;
            int births = Emission && nextBirth <= target ? checked((int)Math.Floor((target - nextBirth) / cadence + 1e-9) + 1) : 0;
            while (births > 0 && emissionEpoch + (nextBirthIndex + births - 1) * cadence > target) births--;
            if (births > Capacity * 4) throw new ArgumentException("Advance exceeds bounded emission work; split simulated step.");
            if (dt == 0) return;
            double ix = integrated[0] + wind[0] * dt, iy = integrated[1] + wind[1] * dt, iz = integrated[2] + wind[2] * dt;
            for (int i = 0; i < births; i++)
            {
                double birth = emissionEpoch + (nextBirthIndex + i) * cadence;
                ExpireAt(birth);
                if (count == Capacity) { head = (head + 1) % Capacity; count--; CapacityEvictions++; }
                double fraction = (birth - Time) / dt;
                history[(head + count++) % Capacity] = new Birth { id = nextId++, time = birth,
                    x = measuredStart == null ? origin[0] + velocity[0] * birth : measuredStart[0] + (measuredEnd[0] - measuredStart[0]) * fraction,
                    y = measuredStart == null ? origin[1] + velocity[1] * birth : measuredStart[1] + (measuredEnd[1] - measuredStart[1]) * fraction,
                    z = measuredStart == null ? origin[2] + velocity[2] * birth : measuredStart[2] + (measuredEnd[2] - measuredStart[2]) * fraction,
                    wx = integrated[0] + wind[0] * (birth - Time), wy = integrated[1] + wind[1] * (birth - Time), wz = integrated[2] + wind[2] * (birth - Time) };
                Emitted++;
            }
            if (births > 0) { nextBirthIndex += births; nextBirth = emissionEpoch + nextBirthIndex * cadence; }
            integrated[0] = ix; integrated[1] = iy; integrated[2] = iz; Time = target;
            ExpireAt(Time);
            Generation++;
        }
        void ExpireAt(double time)
        { while (count > 0 && time - history[head].time >= lifetime) { head = (head + 1) % Capacity; count--; Expired++; } }
        public PersistentTrailParcel At(int index)
        {
            if (index < 0 || index >= count) throw new ArgumentOutOfRangeException("index");
            Birth b = history[(head + index) % Capacity]; double age = Math.Max(0, Time - b.time);
            return new PersistentTrailParcel { id = b.id, x = b.x + integrated[0] - b.wx,
                y = b.y + integrated[1] - b.wy, z = b.z + integrated[2] - b.wz, age = age,
                width = Math.Sqrt(initialWidth * initialWidth + 2 * diffusion * age), opacity = Math.Exp(-decay * age) };
        }
        public PersistentTrailParcel RelativeAt(int index, double[] renderOrigin)
        { CheckVector(renderOrigin, 1e15); var p = At(index); p.x -= renderOrigin[0]; p.y -= renderOrigin[1]; p.z -= renderOrigin[2]; return p; }
    }
}
