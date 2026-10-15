using System;
using System.Collections.Generic;
using KspContinuum;

static class Falsifiers
{
    public static object RunQuery()
    {
        Stock.Admit(); double[] globals = Stock.Globals(); bool logging = GameSettings.FI_LOG_TEMP_ERROR; var witnesses = new List<object>();
        try
        {
            GameSettings.FI_LOG_TEMP_ERROR = false;
            using (var kernel = new ResetKernel())
            {
                double[] cylinders = { 0, BitConverter.Int64BitsToDouble(long.MinValue), double.Epsilon, Math.BitIncrement(double.Epsilon), Math.ScaleB(1, -54), Math.BitIncrement(Math.ScaleB(1, -54)), Math.BitDecrement(.5), .5, Math.BitIncrement(.5), Math.BitDecrement(1), 1 };
                foreach (double c in cylinders) Require(BitConverter.DoubleToInt64Bits(ResetKernel.OriginalAreaExpression(c, 1)) == 0x3ff0000000000000, "Original unit-area expression differs at cylinder bits " + BitConverter.DoubleToInt64Bits(c).ToString("x16"));
                Require(double.IsNaN(ResetKernel.OriginalAreaExpression(double.NaN, 1)) && double.IsNaN(ResetKernel.OriginalAreaExpression(double.PositiveInfinity, 1)), "Unit-area poison counterexample did not invalidate unconditional elision.");
                witnesses.Add(new { name = "original-il-unit-area-rounding-and-poison", admittedBoundaryCases = cylinders.Length, nonfiniteCounterexamples = 2 });

                var order = Frame(4, globals);
                order.rows[0].projectedCenter[0] = order.rows[0].center[0] = 50; order.rows[0].minExtents[0] = 49; order.rows[0].maxExtents[0] = 51;
                order.rows[1].projectedCenter[0] = order.rows[1].center[0] = 0; order.rows[1].minExtents[0] = -1; order.rows[1].maxExtents[0] = 1; order.rows[1].invFineness = 1;
                order.rows[3].projectedCenter[0] = order.rows[3].center[0] = 0; order.rows[3].minExtents[0] = -1; order.rows[3].maxExtents[0] = 1;
                ReplayResult original = Replay.Evaluate(order, Treatment.ResetStock), candidate = Replay.Evaluate(order, Treatment.QueryElision);
                Require(Replay.Compare(candidate, original) == null && candidate.fallbackReason == null && candidate.cones.Length == 3 && candidate.multipliers[11] == 0 && candidate.areaUnitCones == 2 && candidate.resetRectangleCalls == 1,
                    "Area-elision order fixture failed to preserve cutoff through omitted neutral cones.");
                witnesses.Add(new { name = "omitted-neutral-cones-preserve-cutoff-prefix", candidate.resetRectangleCalls, candidate.areaElidedPairs, acceptedCones = candidate.cones.Length });

                var boundary = Frame(2, globals); boundary.rows[1].minExtents = new[] { 1f, -1f }; boundary.rows[1].maxExtents = new[] { 3f, 1f };
                var index = new RectangleAreaIndex(boundary); var cone = Cone(); cone.occludeConvectionAreaMult = 0; cone.offset[0] = 16777216; cone.extents[0] = 16777216;
                index.Insert(cone, 0); Require(index.Candidates(1).Count == 1 && cone.offset[0] + boundary.rows[1].minExtents[0] == cone.extents[0] && (double)cone.offset[0] + boundary.rows[1].minExtents[0] > cone.extents[0], "Rectangle index lost binary32 inclusive boundary.");
                witnesses.Add(new { name = "rectangle-index-float-addition-boundary", candidates = index.Candidates(1).Count });
            }
            return new { status = "query-focused-falsifiers-passed", witnesses };
        }
        finally { Stock.Globals(globals); GameSettings.FI_LOG_TEMP_ERROR = logging; }
    }
    public static object RunReset()
    {
        Stock.Admit(); double[] globals = Stock.Globals(); bool logging = GameSettings.FI_LOG_TEMP_ERROR; var witnesses = new List<object>();
        try
        {
            GameSettings.FI_LOG_TEMP_ERROR = false;
            using (var kernel = new ResetKernel())
            {
                ReplayResult MatchReset(ConvectionFrame frame, bool fallback)
                {
                    ReplayResult original = Replay.Evaluate(frame, Treatment.ResetStock), candidate = Replay.Evaluate(frame, Treatment.ResetCandidate);
                    Require(Replay.Compare(candidate, original) == null, "Reset fixture differs from original bits.");
                    Require((candidate.fallbackReason != null) == fallback, "Reset fixture fallback coverage differs."); return candidate;
                }
                var poison = Frame(2, globals); poison.globals[11] = double.NaN;
                var result = MatchReset(poison, true); Require(double.IsNaN(result.multipliers[4]), "Poison fixture did not reach stock zero-times-NaN.");
                witnesses.Add(new { name = "reset-nonfinite-stock-fallback-bitwise", result.fallbackReason });

                var direct = Stock.Input(Frame(1, globals).rows[0]); direct.projectedCenter = new UnityEngine.Vector3(30, 0, 0);
                direct.minExtents = new UnityEngine.Vector2(29, -1); direct.maxExtents = new UnityEngine.Vector2(31, 1);
                var cone = new OcclusionCone { radius = 100, extents = new UnityEngine.Vector2(1, 1), shockConvectionTempMult = .75, shockConvectionCoeffMult = 1,
                    occludeConvectionTempMult = .55, occludeConvectionCoeffMult = 1, occludeConvectionAreaMult = 1 };
                foreach (double old in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    double temp = old, coeff = 1; ResetKernel.Original(direct, cone, ref temp, ref coeff);
                    Require(double.IsNaN(temp), "Full shock incorrectly erased nonfinite prefix poison.");
                }
                witnesses.Add(new { name = "full-shock-still-multiplies-prefix-poison", nonfiniteInputs = 3 });

                var cutoff = Frame(3, globals); cutoff.rows[0].invFineness = 1;
                cutoff.rows[1].projectedCenter[0] = cutoff.rows[1].center[0] = 10; cutoff.rows[1].minExtents[0] = 9; cutoff.rows[1].maxExtents[0] = 11;
                cutoff.rows[1].centroidDot = -100; cutoff.rows[1].minimumDot = -102; cutoff.rows[1].maximumDot = -98;
                cutoff.rows[2].projectedCenter[0] = cutoff.rows[2].center[0] = 0; cutoff.rows[2].minExtents[0] = -1; cutoff.rows[2].maxExtents[0] = 1;
                cutoff.rows[2].centroidDot = -200; cutoff.rows[2].minimumDot = -202; cutoff.rows[2].maximumDot = -198;
                result = MatchReset(cutoff, false);
                Require(result.cones.Length == 2 && result.multipliers[8] == 0 && result.candidatePairs == 2 && result.resetRectangleCalls == 2, "Cutoff did not exclude the later accepted cone.");
                var later = Stock.Input(cutoff.rows[1]); later.convCone.Setup(later, 2, Math.Asin(.5), .7957 * (1 - 1.0 / 8));
                var receiver = Stock.Input(cutoff.rows[2]); double testTemp = result.multipliers[6], testCoeff = result.multipliers[7];
                ResetKernel.Original(receiver, later.convCone, ref testTemp, ref testCoeff);
                Require(BitConverter.DoubleToInt64Bits(testTemp) != BitConverter.DoubleToInt64Bits(result.multipliers[6]), "Excluded later full shock was not an independent thermal counterexample.");
                witnesses.Add(new { name = "area-cutoff-before-later-shock", result.candidatePairs, result.resetRectangleCalls, actualTemp = result.multipliers[6], unreachedResetTemp = testTemp });

                var threshold = Frame(2, globals); threshold.rows[0].invFineness = 1;
                threshold.rows[1].center = new[] { 0f, 0f }; threshold.rows[1].minExtents = new[] { -1f, -1f }; threshold.rows[1].maxExtents = new[] { 1f, 1f };
                result = MatchReset(threshold, false);
                Require(result.multipliers[5] == 0 && result.multipliers[3] == 0 && result.resetRows == 1, "Original cylinder threshold did not force full reset despite disjoint shock circle.");
                witnesses.Add(new { name = "original-cylinder-threshold-precedes-circle", result.resetRows, actualTemp = result.multipliers[3] });

                var signed = Frame(2, globals); signed.globals[11] = BitConverter.Int64BitsToDouble(long.MinValue); result = MatchReset(signed, true);
                witnesses.Add(new { name = "reset-negative-zero-stock-fallback-bitwise", result.fallbackReason });

                var overflow = Frame(2, globals); overflow.rows[1].projectedCenter[0] = 1e30f; result = MatchReset(overflow, true);
                witnesses.Add(new { name = "reset-float-overflow-domain-stock-fallback", result.fallbackReason });
                GameSettings.FI_LOG_TEMP_ERROR = true; result = MatchReset(Frame(2, globals), true);
                witnesses.Add(new { name = "reset-logging-domain-stock-fallback", result.fallbackReason });
            }
            return new { status = "reset-focused-falsifiers-passed", witnesses };
        }
        finally { Stock.Globals(globals); GameSettings.FI_LOG_TEMP_ERROR = logging; }
    }
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
