using System;
using System.Collections.Generic;
using KspContinuum;

static class Falsifiers
{
    static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    static ConvectionFrame Frame(int count, double[] globals)
    {
        var frame = new ConvectionFrame { mach = 4, globals = (double[])globals.Clone(), rows = new ConvectionRow[count], acceptedCones = Array.Empty<ConvectionCone>() };
        for (int row = 0; row < count; row++)
        {
            float x = row * 10000;
            frame.rows[row] = new ConvectionRow { flightId = (uint)row + 1, projectedArea = 4, projectedRadius = 1, invFineness = .5,
                minimumDot = -row * 10 - 2, maximumDot = -row * 10 + 2, centroidDot = -row * 10, maxWidthDepth = 2,
                projectedCenter = new[] { x, 0f, 0f }, center = new[] { x, 0f }, minExtents = new[] { x - 1, -1f },
                maxExtents = new[] { x + 1, 1f }, extents = new[] { 1f, 1f } };
        }
        return frame;
    }
    static ReplayResult Match(ConvectionFrame frame, bool fallback)
    {
        ReplayResult original = Replay.Evaluate(frame, Treatment.Stock), candidate = Replay.Evaluate(frame, Treatment.CandidateIndex);
        Require(Replay.Compare(candidate, original) == null, "Synthetic candidate differs from original bitwise.");
        Require((candidate.fallbackReason != null) == fallback, "Synthetic fallback coverage differs.");
        return candidate;
    }
    static ConvectionCone Cone()
    {
        return new ConvectionCone { rowIndex = 0, center = new[] { 0f, 0f, 0f }, extents = new[] { 1f, 1f }, offset = new[] { 0f, 0f },
            radius = 1, shockConvectionTempMult = 1, shockConvectionCoeffMult = 1, occludeConvectionTempMult = 1, occludeConvectionCoeffMult = 1, occludeConvectionAreaMult = 1 };
    }
    public static object Run()
    {
        Stock.Admit(); double[] globals = Stock.Globals(); var witnesses = new List<object>();
        try
        {
            var sparse = Frame(32, globals); var result = Match(sparse, false);
            Require(result.scalarPairs == 0 && result.possiblePairs == 496, "Sparse fixture did not prune all 496 disjoint pairs.");
            witnesses.Add(new { name = "sparse-stock-bitwise", result.scalarPairs, result.possiblePairs });

            var shock = Frame(2, globals); var recipient = shock.rows[1];
            recipient.projectedCenter[0] = recipient.center[0] = 30; recipient.minExtents[0] = 29; recipient.maxExtents[0] = 31;
            recipient.minimumDot = -102; recipient.maximumDot = -98; recipient.centroidDot = -100;
            result = Match(shock, false);
            Require(result.scalarPairs == 1 && result.multipliers[3] < 1, "Shock-only fixture did not retain a thermally influential pair outside rectangle overlap.");
            witnesses.Add(new { name = "shock-only-stock-bitwise", result.scalarPairs, receiverTemp = result.multipliers[3] });

            var invalid = Frame(2, globals); invalid.globals[11] = double.NaN; result = Match(invalid, true);
            Require(double.IsNaN(result.multipliers[4]), "Nonfinite cone multiplier did not demonstrate zero-times-NaN stock behavior.");
            witnesses.Add(new { name = "nonfinite-stock-fallback-bitwise", result.fallbackReason });
            var signed = Frame(2, globals); signed.globals[11] = BitConverter.Int64BitsToDouble(long.MinValue); result = Match(signed, true);
            witnesses.Add(new { name = "signed-zero-stock-fallback-bitwise", result.fallbackReason });

            var boundary = Frame(2, globals); recipient = boundary.rows[1];
            recipient.projectedCenter[0] = 16777216; recipient.center[0] = 16777216; recipient.minExtents[0] = 16777216; recipient.maxExtents[0] = 16777218;
            var cone = Cone(); cone.center[0] = -.25f; cone.radius = 16777215.125;
            Require(ConvectionCandidateIndex.TryCreate(boundary, out var index, out string reason), "Float-boundary index rejected: " + reason);
            Require(index.Insert(cone, 0) && index.Candidates(1).Count == 1, "Float-subtraction influence was pruned.");
            float difference = recipient.projectedCenter[0] - cone.center[0];
            Require(difference == 16777216 && (double)recipient.projectedCenter[0] - cone.center[0] > cone.radius + recipient.projectedRadius,
                "Float-boundary fixture did not falsify independent double distance.");
            witnesses.Add(new { name = "float-subtraction-conservative-boundary", candidates = index.Candidates(1).Count, floatDifference = difference });

            cone = Cone(); cone.radius = 1;
            Require(ConvectionCandidateIndex.TryCreate(sparse, out index, out reason) && index.Insert(cone, 0), "Nonfinite insertion fixture was not admitted.");
            cone = Cone(); cone.rowIndex = 1; cone.shockConvectionCoeffMult = double.NaN;
            Require(!index.Insert(cone, 1) && !index.Admitted, "Nonfinite published cone failed to reject the candidate path.");
            witnesses.Add(new { name = "published-nonfinite-cone-fallback", index.FallbackReason });

            long[] bits = { 0, long.MinValue, 0x7ff8000000000001, 0x7ff8000000000002 };
            TanCache.BeginBatch();
            foreach (long key in bits)
            {
                double angle = BitConverter.Int64BitsToDouble(key), expected = Math.Tan(angle);
                for (int repeat = 0; repeat < 2; repeat++)
                    Require(BitConverter.DoubleToInt64Bits(TanCache.CachedTan(angle)) == BitConverter.DoubleToInt64Bits(expected), "Tangent cache changed signed-zero/NaN bits.");
            }
            Require(TanCache.Hits == 4 && TanCache.Misses == 4, "Tangent keys merged signed-zero or distinct NaN payloads.");
            witnesses.Add(new { name = "tan-bit-keys", TanCache.Hits, TanCache.Misses }); TanCache.EndBatch();
            TanCache.BeginBatch(true);
            foreach (long key in bits)
            {
                double angle = BitConverter.Int64BitsToDouble(key), expected = Math.Tan(angle);
                for (int repeat = 0; repeat < 2; repeat++)
                    Require(BitConverter.DoubleToInt64Bits(TanCache.CachedTan(angle)) == BitConverter.DoubleToInt64Bits(expected), "Two-slot cache changed signed-zero/NaN bits.");
            }
            Require(TanCache.Hits == 2 && TanCache.Misses == 6 && TanCache.Uncached == 4, "Two-slot cache did not preserve distinct keys and original third-angle fallback.");
            witnesses.Add(new { name = "two-slot-bit-keys-and-uncached-fallback", TanCache.Hits, TanCache.Misses, TanCache.Uncached });
            return new { status = "focused-falsifiers-passed", witnesses };
        }
        finally { Stock.Globals(globals); TanCache.EndBatch(); }
    }
}
