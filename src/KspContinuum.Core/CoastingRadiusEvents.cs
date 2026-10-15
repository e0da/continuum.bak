using System;

namespace KspContinuum
{
    public enum RadiusCrossingDirection { Inward, Outward }
    public enum CoastingEventGuardState { BeforeGuard, GuardOpen, ReachedOrPassed }

    public sealed class RadiusCrossingSearch
    {
        public RadiusCrossingSearch(double startTimeSeconds, double endTimeSeconds, double radiusMeters,
            RadiusCrossingDirection direction, double timeToleranceSeconds = 1e-6)
        {
            AssemblyModel.Finite(startTimeSeconds); AssemblyModel.Finite(endTimeSeconds); AssemblyModel.Positive(radiusMeters);
            AssemblyModel.Positive(timeToleranceSeconds);
            if (endTimeSeconds <= startTimeSeconds || startTimeSeconds + timeToleranceSeconds <= startTimeSeconds ||
                !Enum.IsDefined(typeof(RadiusCrossingDirection), direction))
                throw new ArgumentException("Search end must follow start and direction must be known.");
            StartTimeSeconds = startTimeSeconds; EndTimeSeconds = endTimeSeconds; RadiusMeters = radiusMeters;
            Direction = direction; TimeToleranceSeconds = timeToleranceSeconds;
        }
        public double StartTimeSeconds { get; private set; }
        public double EndTimeSeconds { get; private set; }
        public double RadiusMeters { get; private set; }
        public RadiusCrossingDirection Direction { get; private set; }
        public double TimeToleranceSeconds { get; private set; }
    }

    public sealed class RadiusCrossingEvent
    {
        internal RadiusCrossingEvent(int bodyId, double time, CoastingBody body, int evaluations, double lowerTime)
        { BodyId = bodyId; TimeSeconds = time; Body = body; Evaluations = evaluations; LowerTimeSeconds=lowerTime; }
        public string Kind { get { return "radius-crossing"; } }
        public int BodyId { get; private set; }
        public double TimeSeconds { get; private set; }
        public double LowerTimeSeconds { get; private set; }
        public CoastingBody Body { get; private set; }
        public int Evaluations { get; private set; }
    }

    public static class CoastingEventScheduler
    {
        const double TwoPi = 2 * Math.PI;
        const double MinimumEccentricity = 1e-8;
        const double MaximumSeedRevolutions = 1000000;

        public static RadiusCrossingEvent FindFirst(CoastingEngine engine, RadiusCrossingSearch search)
        {
            if (engine == null || search == null) throw new ArgumentException("Engine and search are required.");
            RadiusCrossingEvent first = null; int evaluations = 0;
            for (int i = 0; i < engine.SeedBodyCount; i++)
            {
                CoastingBody seed = engine.SeedBody(i); double predicted, period;
                if (!TryPredict(seed, engine.SeedEpochSeconds, engine.GravitationalParameter, search, out predicted, out period)) continue;
                RadiusCrossingEvent candidate = Refine(engine, seed.Id, predicted, period, search, ref evaluations);
                if (candidate == null) continue;
                if (first == null || candidate.TimeSeconds < first.TimeSeconds ||
                    candidate.TimeSeconds == first.TimeSeconds && candidate.BodyId < first.BodyId) first = candidate;
            }
            return first == null ? null : new RadiusCrossingEvent(first.BodyId, first.TimeSeconds, first.Body, evaluations,first.LowerTimeSeconds);
        }

        public static bool GuardReached(double currentTimeSeconds, double eventTimeSeconds, double leadSeconds)
        { return ClassifyGuard(currentTimeSeconds, eventTimeSeconds, leadSeconds) == CoastingEventGuardState.GuardOpen; }

        public static CoastingEventGuardState ClassifyGuard(double currentTimeSeconds, double eventTimeSeconds,
            double leadSeconds)
        {
            AssemblyModel.Finite(currentTimeSeconds); AssemblyModel.Finite(eventTimeSeconds);
            AssemblyModel.Positive(leadSeconds);
            if (currentTimeSeconds >= eventTimeSeconds) return CoastingEventGuardState.ReachedOrPassed;
            return eventTimeSeconds - currentTimeSeconds <= leadSeconds ?
                CoastingEventGuardState.GuardOpen : CoastingEventGuardState.BeforeGuard;
        }

        static bool TryPredict(CoastingBody seed, double epoch, double mu, RadiusCrossingSearch search,
            out double time, out double period)
        {
            time = 0; period = 0; Vec r = seed.Position, v = seed.Velocity;
            double radius = Norm(r), speedSquared = Dot(v, v), rv = Dot(r, v);
            if (radius == 0) return false;
            double energy = speedSquared * .5 - mu / radius;
            if (!(energy < 0) || double.IsInfinity(energy)) return false;
            double semiMajor = -mu / (2 * energy);
            Vec eccentricityVector = (r * (speedSquared - mu / radius) + v * -rv) * (1 / mu);
            double eccentricity = Norm(eccentricityVector);
            if (!(eccentricity >= MinimumEccentricity) || eccentricity >= 1 || double.IsNaN(eccentricity)) return false;
            double periapsis = semiMajor * (1 - eccentricity), apoapsis = semiMajor * (1 + eccentricity);
            if (!(search.RadiusMeters > periapsis && search.RadiusMeters < apoapsis)) return false;
            double cosEpoch = Clamp((1 - radius / semiMajor) / eccentricity);
            double sinEpoch = rv / (eccentricity * Math.Sqrt(mu * semiMajor));
            double eccentricAnomaly = Normalize(Math.Atan2(sinEpoch, cosEpoch));
            double meanAtEpoch = eccentricAnomaly - eccentricity * Math.Sin(eccentricAnomaly);
            double crossingCosine = Clamp((1 - search.RadiusMeters / semiMajor) / eccentricity);
            double crossingAnomaly = Math.Acos(crossingCosine);
            if (search.Direction == RadiusCrossingDirection.Inward) crossingAnomaly = TwoPi - crossingAnomaly;
            double crossingMean = crossingAnomaly - eccentricity * Math.Sin(crossingAnomaly);
            double meanMotion = Math.Sqrt(mu / (semiMajor * semiMajor * semiMajor));
            period = TwoPi / meanMotion;
            double seedAdvance = meanMotion * (search.StartTimeSeconds - epoch);
            if (double.IsNaN(seedAdvance) || double.IsInfinity(seedAdvance) ||
                Math.Abs(seedAdvance) > TwoPi * MaximumSeedRevolutions) return false;
            double meanAtStart = meanAtEpoch + seedAdvance;
            double deltaMean = Normalize(crossingMean - Normalize(meanAtStart));
            if (deltaMean <= meanMotion * search.TimeToleranceSeconds) deltaMean += TwoPi;
            time = search.StartTimeSeconds + deltaMean / meanMotion;
            return !double.IsNaN(time) && !double.IsInfinity(time) && time <= search.EndTimeSeconds;
        }

        static RadiusCrossingEvent Refine(CoastingEngine engine, int bodyId, double predicted, double period,
            RadiusCrossingSearch search, ref int evaluations)
        {
            double halfWidth = Math.Max(search.TimeToleranceSeconds * 4, 1e-5), low = predicted, high = predicted;
            bool bracketed = false;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                low = Math.Max(search.StartTimeSeconds, predicted - halfWidth);
                high = Math.Min(search.EndTimeSeconds, predicted + halfWidth);
                CoastingBody before = engine.SampleBodyAt(bodyId, low), after = engine.SampleBodyAt(bodyId, high); evaluations += 2;
                double a = Norm(before.Position) - search.RadiusMeters, b = Norm(after.Position) - search.RadiusMeters;
                if (Crossed(a, b, search.Direction)) { bracketed = true; break; }
                halfWidth *= 2;
                if (halfWidth > period * .25) break;
            }
            if (!bracketed) return null;
            for (int iteration = 0; iteration < 64 && high - low > search.TimeToleranceSeconds; iteration++)
            {
                double middle = low + (high - low) * .5;
                if (middle <= low || middle >= high) break;
                CoastingBody sample = engine.SampleBodyAt(bodyId, middle); evaluations++;
                double sign = Norm(sample.Position) - search.RadiusMeters;
                bool crossedFromLow = search.Direction == RadiusCrossingDirection.Outward ? sign >= 0 : sign <= 0;
                if (crossedFromLow) high = middle; else low = middle;
            }
            CoastingBody body = engine.SampleBodyAt(bodyId, high); evaluations++;
            return new RadiusCrossingEvent(bodyId, high, body, evaluations,low);
        }

        static bool Crossed(double before, double after, RadiusCrossingDirection direction)
        { return direction == RadiusCrossingDirection.Outward ? before < 0 && after >= 0 : before > 0 && after <= 0; }
        static double Normalize(double angle) { angle %= TwoPi; return angle < 0 ? angle + TwoPi : angle; }
        static double Clamp(double value) { return Math.Max(-1, Math.Min(1, value)); }
        static double Dot(Vec a, Vec b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        static double Norm(Vec value) { return Math.Sqrt(Dot(value, value)); }
    }
}
