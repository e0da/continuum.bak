using System;

namespace KspContinuum
{
    public static class CylinderOcclusionFold
    {
        public struct Row
        {
            public float minX, minY, maxX, maxY, centerX, centerY, extentX, extentY;
        }

        public static double Intersection(Row target, Row source)
        {
            // Stock first adds Vector2 floats, then promotes the shifted bounds to doubles.
            float minX = -source.centerX + target.minX, maxX = -source.centerX + target.maxX;
            float minY = -source.centerY + target.minY, maxY = -source.centerY + target.maxY;
            double width = (double)maxX - minX, height = (double)maxY - minY;
            double area = width * height;
            if (maxX < -(double)source.extentX || minX > source.extentX ||
                maxY < -(double)source.extentY || minY > source.extentY || area == 0.0) return 0.0;
            double result = Math.Max(0.0, Math.Min(source.extentX, maxX) - Math.Max(-(double)source.extentX, minX)) *
                Math.Max(0.0, Math.Min(source.extentY, maxY) - Math.Max(-(double)source.extentY, minY)) / area;
            return double.IsNaN(result) ? 0.0 : result;
        }

        public static int Evaluate(Row[] ascendingRows, int count, double[] multipliers, int[] accepted,
            Action<int, int, double> observe = null)
        {
            if (ascendingRows == null || multipliers == null || accepted == null || count < 1 ||
                count > ascendingRows.Length || count > multipliers.Length || count > accepted.Length)
                throw new ArgumentException("One complete ordered pass and reusable output storage are required.");
            for (int i = 0; i < count; i++)
            {
                Row row = ascendingRows[i];
                if (!Finite(row.minX) || !Finite(row.minY) || !Finite(row.maxX) || !Finite(row.maxY) ||
                    !Finite(row.centerX) || !Finite(row.centerY) || !Finite(row.extentX) || !Finite(row.extentY) ||
                    row.maxX < row.minX || row.maxY < row.minY || row.extentX < 0 || row.extentY < 0)
                    throw new ArgumentException("Nonfinite or reversed geometry requires original fallback.");
            }
            int sourceCount = 1;
            accepted[0] = count - 1; multipliers[count - 1] = 1.0;
            for (int target = count - 2; target >= 0; target--)
            {
                double exposure = 1.0;
                for (int j = 0; j < sourceCount; j++)
                {
                    int source = accepted[j];
                    double occlusion = Intersection(ascendingRows[target], ascendingRows[source]);
                    if (observe != null) observe(target, source, occlusion);
                    exposure -= occlusion;
                    if (exposure <= 0.001) { exposure = 0.0; break; }
                }
                multipliers[target] = exposure;
                if (exposure > 0.0) accepted[sourceCount++] = target;
            }
            return sourceCount;
        }

        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
