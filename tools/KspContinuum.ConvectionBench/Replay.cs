using System;
using System.Collections.Generic;
using KspContinuum;

enum Treatment { Stock, CandidateIndex, CachedTan, StockDelegate, CachedTanTwoSlot, ResetStock, ResetCandidate, QueryElision }
sealed class ReplayResult
{
    public double[] multipliers;
    public ConvectionCone[] cones;
    public long scalarPairs, possiblePairs, candidatePairs, indexNodeVisits, tanHits, tanMisses, tanUncached;
    public long resetRectangleCalls, resetGeometryCalls, resetAnchorProbes, resetAnchors, resetBoundSteps, resetRows, resetNoResetRows, resetSkippedScalarPairs, resetAttemptedScalarPairs;
    public long areaIndexNodeVisits, areaUnitCones, areaElidedPairs;
    public string fallbackReason;
}
static class Replay
{
    sealed class Fallback : Exception { public Fallback(string reason) : base(reason) { } }
    public static ReplayResult Evaluate(ConvectionFrame frame, Treatment treatment)
    {
        if (treatment == Treatment.ResetCandidate) return ResetKernel.Evaluate(frame);
        if (treatment == Treatment.QueryElision) return ResetKernel.Evaluate(frame, true);
        if (treatment == Treatment.ResetStock) return ResetKernel.EvaluateStock(frame);
        try { return Run(frame, treatment); }
        catch (Fallback error)
        {
            ReplayResult stock = Run(frame, Treatment.Stock); stock.fallbackReason = error.Message; return stock;
        }
    }
    static ReplayResult Run(ConvectionFrame frame, Treatment treatment)
    {
        Stock.Globals(frame.globals);
        bool cached = treatment == Treatment.CachedTan || treatment == Treatment.CachedTanTwoSlot;
        if (cached) TanCache.BeginBatch(treatment == Treatment.CachedTanTwoSlot);
        ConvectionCandidateIndex index = null;
        if (treatment == Treatment.CandidateIndex && !ConvectionCandidateIndex.TryCreate(frame, out index, out string reason)) throw new Fallback(reason);
        var data = new OcclusionData[frame.rows.Length];
        for (int i = 0; i < data.Length; i++) data[i] = Stock.Input(frame.rows[i]);
        var accepted = new List<OcclusionCone>(); var cones = new List<ConvectionCone>();
        var result = new ReplayResult { multipliers = new double[data.Length * 3] };
        if (frame.mach > 1)
        {
            double sqrtMach = Math.Sqrt(frame.mach), machAngle = Math.Asin(1 / sqrtMach), detachAngle = .7957 * (1 - 1 / (frame.mach * sqrtMach));
            for (int row = 0; row < data.Length; row++)
            {
                OcclusionData current = data[row];
                result.possiblePairs += accepted.Count;
                IReadOnlyList<int> candidates = index == null ? null : index.Candidates(row);
                int count = candidates == null ? accepted.Count : candidates.Count;
                result.candidatePairs += count;
                for (int pair = 0; pair < count; pair++)
                {
                    if (index != null && (!(current.ptd.convectionTempMultiplier > 0) || !(current.ptd.convectionCoeffMultiplier > 0) ||
                        !double.IsFinite(current.ptd.convectionTempMultiplier) || !double.IsFinite(current.ptd.convectionCoeffMultiplier))) throw new Fallback("nonpositive-or-nonfinite-intermediate-multiplier");
                    int at = candidates == null ? pair : candidates[pair];
                    double area = cached || treatment == Treatment.StockDelegate
                        ? TanCache.Caller(current, accepted[at], ref current.ptd.convectionTempMultiplier, ref current.ptd.convectionCoeffMultiplier)
                        : current.GetShockStats(accepted[at], ref current.ptd.convectionTempMultiplier, ref current.ptd.convectionCoeffMultiplier);
                    result.scalarPairs++; current.ptd.convectionAreaMultiplier *= area;
                    if (current.ptd.convectionAreaMultiplier <= .001) { current.ptd.convectionAreaMultiplier = 0; break; }
                }
                if (current.ptd.convectionAreaMultiplier > 0)
                {
                    if (index != null && (!(current.ptd.convectionTempMultiplier > 0) || !(current.ptd.convectionCoeffMultiplier > 0) ||
                        !double.IsFinite(current.ptd.convectionTempMultiplier) || !double.IsFinite(current.ptd.convectionCoeffMultiplier))) throw new Fallback("nonpositive-or-nonfinite-intermediate-multiplier");
                    current.convCone.Setup(current, sqrtMach, machAngle, detachAngle);
                    var cone = Stock.Capture(current.convCone, row);
                    if (index != null && !index.Insert(cone, accepted.Count)) throw new Fallback(index.FallbackReason);
                    accepted.Add(current.convCone); cones.Add(cone);
                }
            }
        }
        for (int row = 0; row < data.Length; row++)
        {
            result.multipliers[row * 3] = data[row].ptd.convectionTempMultiplier;
            result.multipliers[row * 3 + 1] = data[row].ptd.convectionCoeffMultiplier;
            result.multipliers[row * 3 + 2] = data[row].ptd.convectionAreaMultiplier;
        }
        result.cones = cones.ToArray(); result.indexNodeVisits = index == null ? 0 : index.nodeVisits;
        if (cached) { result.tanHits = TanCache.Hits; result.tanMisses = TanCache.Misses; result.tanUncached = TanCache.Uncached; TanCache.EndBatch(); }
        return result;
    }
    static bool Same(double left, double right) { return BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right); }
    static bool Same(float[] left, float[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (BitConverter.SingleToInt32Bits(left[i]) != BitConverter.SingleToInt32Bits(right[i])) return false;
        return true;
    }
    static bool Same(ConvectionCone left, ConvectionCone right)
    {
        return left.rowIndex == right.rowIndex && Same(left.center, right.center) && Same(left.extents, right.extents) && Same(left.offset, right.offset) &&
            Same(left.radius, right.radius) && Same(left.cylNoseDot, right.cylNoseDot) && Same(left.shockNoseDot, right.shockNoseDot) && Same(left.shockAngle, right.shockAngle) &&
            Same(left.shockConvectionTempMult, right.shockConvectionTempMult) && Same(left.shockConvectionCoeffMult, right.shockConvectionCoeffMult) &&
            Same(left.occludeConvectionTempMult, right.occludeConvectionTempMult) && Same(left.occludeConvectionCoeffMult, right.occludeConvectionCoeffMult) && Same(left.occludeConvectionAreaMult, right.occludeConvectionAreaMult);
    }
    public static string Compare(ReplayResult actual, ConvectionFrame expected)
    {
        for (int row = 0; row < expected.rows.Length; row++)
        {
            var input = expected.rows[row];
            double[] values = { input.expectedTemp, input.expectedCoeff, input.expectedArea };
            for (int field = 0; field < 3; field++) if (!Same(actual.multipliers[row * 3 + field], values[field]))
                return "recorded row " + row + " flightId " + input.flightId + " multiplier " + field + " bits " +
                    BitConverter.DoubleToInt64Bits(actual.multipliers[row * 3 + field]).ToString("x16") + " != " + BitConverter.DoubleToInt64Bits(values[field]).ToString("x16");
        }
        if (actual.cones.Length != expected.acceptedCones.Length) return "recorded accepted cone count differs";
        for (int cone = 0; cone < actual.cones.Length; cone++) if (!Same(actual.cones[cone], expected.acceptedCones[cone])) return "recorded accepted cone fields differ at " + cone;
        return null;
    }
    public static string Compare(ReplayResult actual, ReplayResult expected)
    {
        if (actual.multipliers.Length != expected.multipliers.Length || actual.cones.Length != expected.cones.Length) return "baseline output shape differs";
        for (int i = 0; i < actual.multipliers.Length; i++) if (!Same(actual.multipliers[i], expected.multipliers[i])) return "baseline multiplier bits differ at " + i;
        for (int i = 0; i < actual.cones.Length; i++) if (!Same(actual.cones[i], expected.cones[i])) return "baseline cone fields differ at " + i;
        return null;
    }
}
