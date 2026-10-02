namespace DroneDash_x64.Desktop.PvAnalysis;

public static class PvThermalAnomalyDetector
{
    public static IReadOnlyList<PvHotspotCandidate> Detect(
        float[] temperatures,
        int width,
        int height,
        PvAnalysisSettings settings)
    {
        settings.Validate();

        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Thermal-Auflösung ist ungültig.");

        if (temperatures.Length != checked(width * height))
            throw new ArgumentException(
                "Temperaturmatrix passt nicht zur angegebenen Auflösung.",
                nameof(temperatures));

        var finite = new bool[temperatures.Length];
        var integral = new double[(width + 1) * (height + 1)];
        var countIntegral = new int[(width + 1) * (height + 1)];

        for (var y = 0; y < height; y++)
        {
            double rowSum = 0;
            var rowCount = 0;

            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var value = temperatures[index];
                var valid = float.IsFinite(value);

                finite[index] = valid;
                if (valid)
                {
                    rowSum += value;
                    rowCount++;
                }

                var ii = (y + 1) * (width + 1) + (x + 1);
                integral[ii] =
                    integral[y * (width + 1) + (x + 1)] +
                    rowSum;
                countIntegral[ii] =
                    countIntegral[y * (width + 1) + (x + 1)] +
                    rowCount;
            }
        }

        var localBaseline = new double[temperatures.Length];
        var mask = new bool[temperatures.Length];
        var radius = settings.LocalWindowRadiusPixels;

        for (var y = 0; y < height; y++)
        {
            var y0 = Math.Max(0, y - radius);
            var y1 = Math.Min(height - 1, y + radius);

            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (!finite[index])
                {
                    localBaseline[index] = double.NaN;
                    continue;
                }

                var x0 = Math.Max(0, x - radius);
                var x1 = Math.Min(width - 1, x + radius);

                var sum = RectangleSum(
                    integral,
                    width + 1,
                    x0,
                    y0,
                    x1,
                    y1);

                var count = RectangleCount(
                    countIntegral,
                    width + 1,
                    x0,
                    y0,
                    x1,
                    y1);

                if (count <= 1)
                {
                    localBaseline[index] = temperatures[index];
                    continue;
                }

                sum -= temperatures[index];
                count--;

                var baseline = sum / count;
                localBaseline[index] = baseline;
                mask[index] =
                    temperatures[index] - baseline >= settings.WarningDeltaC;
            }
        }

        var visited = new bool[temperatures.Length];
        var candidates = new List<PvHotspotCandidate>();
        var queue = new Queue<int>();

        for (var seed = 0; seed < mask.Length; seed++)
        {
            if (!mask[seed] || visited[seed])
                continue;

            visited[seed] = true;
            queue.Enqueue(seed);

            var pixels = new List<int>();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                pixels.Add(current);

                var cx = current % width;
                var cy = current / width;

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;

                        var nx = cx + dx;
                        var ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;

                        var neighbor = ny * width + nx;
                        if (!mask[neighbor] || visited[neighbor])
                            continue;

                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            if (pixels.Count < settings.MinimumClusterPixels)
                continue;

            var peakIndex = pixels[0];
            foreach (var index in pixels)
            {
                if (temperatures[index] > temperatures[peakIndex])
                    peakIndex = index;
            }

            var peakX = peakIndex % width;
            var peakY = peakIndex / width;
            var peak = temperatures[peakIndex];
            var baselineAtPeak = localBaseline[peakIndex];
            var delta = peak - baselineAtPeak;

            var severity = delta >= settings.CriticalDeltaC
                ? PvAnomalySeverity.Critical
                : PvAnomalySeverity.Warning;

            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;
            double sumX = 0;
            double sumY = 0;

            foreach (var index in pixels)
            {
                var x = index % width;
                var y = index / width;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                sumX += x;
                sumY += y;
            }

            candidates.Add(new PvHotspotCandidate(
                candidates.Count + 1,
                severity,
                pixels.Count,
                peakX,
                peakY,
                peak,
                baselineAtPeak,
                delta,
                sumX / pixels.Count,
                sumY / pixels.Count,
                minX,
                minY,
                maxX,
                maxY));
        }

        return candidates
            .OrderByDescending(candidate => candidate.DeltaC)
            .Select((candidate, index) => candidate with { Index = index + 1 })
            .ToArray();
    }

    private static double RectangleSum(
        double[] integral,
        int stride,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        var ax = x0;
        var ay = y0;
        var bx = x1 + 1;
        var by = y1 + 1;

        return
            integral[by * stride + bx] -
            integral[ay * stride + bx] -
            integral[by * stride + ax] +
            integral[ay * stride + ax];
    }

    private static int RectangleCount(
        int[] integral,
        int stride,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        var ax = x0;
        var ay = y0;
        var bx = x1 + 1;
        var by = y1 + 1;

        return
            integral[by * stride + bx] -
            integral[ay * stride + bx] -
            integral[by * stride + ax] +
            integral[ay * stride + ax];
    }
}
