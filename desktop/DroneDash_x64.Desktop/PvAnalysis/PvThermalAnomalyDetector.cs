namespace DroneDash_x64.Desktop.PvAnalysis;

public static class PvThermalAnomalyDetector
{
    private const int QuantileBinCount = 256;
    private const int MinimumReferencePixels = 8;

    public static IReadOnlyList<PvHotspotCandidate> Detect(
        float[] temperatures,
        int width,
        int height,
        PvAnalysisSettings settings)
    {
        settings.Validate();

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Thermal-Auflösung ist ungültig.");
        }

        if (temperatures.Length != checked(width * height))
        {
            throw new ArgumentException(
                "Temperaturmatrix passt nicht zur angegebenen Auflösung.",
                nameof(temperatures));
        }

        var finite = new bool[temperatures.Length];
        var globalMinimum = float.PositiveInfinity;
        var globalMaximum = float.NegativeInfinity;
        var finiteCount = 0;

        for (var index = 0; index < temperatures.Length; index++)
        {
            var value = temperatures[index];
            if (!float.IsFinite(value))
                continue;

            finite[index] = true;
            finiteCount++;
            globalMinimum = Math.Min(globalMinimum, value);
            globalMaximum = Math.Max(globalMaximum, value);
        }

        if (finiteCount == 0)
            return [];

        var localMedian = new double[temperatures.Length];
        var localUpperQuartile = new double[temperatures.Length];

        BuildLocalQuantileMaps(
            temperatures,
            finite,
            width,
            height,
            settings.LocalWindowRadiusPixels,
            globalMinimum,
            globalMaximum,
            localMedian,
            localUpperQuartile);

        var seedMask = new bool[temperatures.Length];
        var growMask = new bool[temperatures.Length];
        var adaptiveSeedThreshold = new double[temperatures.Length];

        for (var index = 0; index < temperatures.Length; index++)
        {
            if (!finite[index])
                continue;

            var median = localMedian[index];
            var upperQuartile = localUpperQuartile[index];
            var spread = Math.Max(0d, upperQuartile - median);

            var seedThreshold = Math.Max(
                settings.WarningDeltaC,
                1.0d + spread * 3.0d);

            var growThreshold = Math.Max(
                settings.WarningDeltaC * 0.55d,
                0.5d + spread * 2.0d);

            var delta =
                temperatures[index] - median;

            adaptiveSeedThreshold[index] =
                seedThreshold;

            seedMask[index] =
                delta >= seedThreshold;

            growMask[index] =
                delta >= growThreshold;
        }

        var visited = new bool[temperatures.Length];
        var candidates = new List<PvHotspotCandidate>();
        var queue = new Queue<int>();

        for (var seed = 0; seed < seedMask.Length; seed++)
        {
            if (!seedMask[seed] || visited[seed])
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

                        if (nx < 0 ||
                            ny < 0 ||
                            nx >= width ||
                            ny >= height)
                        {
                            continue;
                        }

                        var neighbor =
                            ny * width + nx;

                        if (!growMask[neighbor] ||
                            visited[neighbor])
                        {
                            continue;
                        }

                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            if (pixels.Count <
                settings.MinimumClusterPixels)
            {
                continue;
            }

            var peakIndex = pixels[0];
            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;
            double sumX = 0;
            double sumY = 0;

            foreach (var index in pixels)
            {
                if (temperatures[index] >
                    temperatures[peakIndex])
                {
                    peakIndex = index;
                }

                var x = index % width;
                var y = index / width;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                sumX += x;
                sumY += y;
            }

            var peakX = peakIndex % width;
            var peakY = peakIndex / width;
            var peak = temperatures[peakIndex];

            var reference =
                ComputeReferenceOutsideBoundingBox(
                    temperatures,
                    finite,
                    width,
                    height,
                    minX,
                    minY,
                    maxX,
                    maxY,
                    settings.LocalWindowRadiusPixels);

            var baseline =
                reference.Count >= MinimumReferencePixels
                    ? Percentile(
                        reference,
                        0.50d)
                    : localMedian[peakIndex];

            var upperQuartile =
                reference.Count >= MinimumReferencePixels
                    ? Percentile(
                        reference,
                        0.75d)
                    : localUpperQuartile[peakIndex];

            var delta =
                peak - baseline;

            if (!double.IsFinite(delta) ||
                delta < settings.WarningDeltaC)
            {
                continue;
            }

            var severity =
                delta >= settings.CriticalDeltaC
                    ? PvAnomalySeverity.Critical
                    : PvAnomalySeverity.Warning;

            candidates.Add(
                new PvHotspotCandidate(
                    candidates.Count + 1,
                    severity,
                    pixels.Count,
                    peakX,
                    peakY,
                    peak,
                    baseline,
                    upperQuartile,
                    adaptiveSeedThreshold[peakIndex],
                    delta,
                    sumX / pixels.Count,
                    sumY / pixels.Count,
                    minX,
                    minY,
                    maxX,
                    maxY));
        }

        return candidates
            .OrderByDescending(candidate =>
                candidate.DeltaC)
            .ThenByDescending(candidate =>
                candidate.PixelCount)
            .Select((candidate, index) =>
                candidate with
                {
                    Index = index + 1
                })
            .ToArray();
    }

    private static void BuildLocalQuantileMaps(
        float[] temperatures,
        bool[] finite,
        int width,
        int height,
        int radius,
        float globalMinimum,
        float globalMaximum,
        double[] localMedian,
        double[] localUpperQuartile)
    {
        var histogram =
            new int[QuantileBinCount];

        for (var y = 0; y < height; y++)
        {
            Array.Clear(
                histogram,
                0,
                histogram.Length);

            var y0 =
                Math.Max(
                    0,
                    y - radius);

            var y1 =
                Math.Min(
                    height - 1,
                    y + radius);

            var count = 0;
            var initialRight =
                Math.Min(
                    width - 1,
                    radius);

            for (var x = 0;
                 x <= initialRight;
                 x++)
            {
                count += AddColumn(
                    histogram,
                    temperatures,
                    finite,
                    width,
                    x,
                    y0,
                    y1,
                    globalMinimum,
                    globalMaximum,
                    +1);
            }

            for (var x = 0;
                 x < width;
                 x++)
            {
                if (x > 0)
                {
                    var removeX =
                        x - radius - 1;

                    if (removeX >= 0)
                    {
                        count += AddColumn(
                            histogram,
                            temperatures,
                            finite,
                            width,
                            removeX,
                            y0,
                            y1,
                            globalMinimum,
                            globalMaximum,
                            -1);
                    }

                    var addX =
                        x + radius;

                    if (addX < width)
                    {
                        count += AddColumn(
                            histogram,
                            temperatures,
                            finite,
                            width,
                            addX,
                            y0,
                            y1,
                            globalMinimum,
                            globalMaximum,
                            +1);
                    }
                }

                var index =
                    y * width + x;

                if (!finite[index] ||
                    count == 0)
                {
                    localMedian[index] =
                        double.NaN;

                    localUpperQuartile[index] =
                        double.NaN;

                    continue;
                }

                localMedian[index] =
                    QuantileFromHistogram(
                        histogram,
                        count,
                        0.50d,
                        globalMinimum,
                        globalMaximum);

                localUpperQuartile[index] =
                    QuantileFromHistogram(
                        histogram,
                        count,
                        0.75d,
                        globalMinimum,
                        globalMaximum);
            }
        }
    }

    private static int AddColumn(
        int[] histogram,
        float[] temperatures,
        bool[] finite,
        int width,
        int x,
        int y0,
        int y1,
        float globalMinimum,
        float globalMaximum,
        int direction)
    {
        var changed = 0;

        for (var y = y0; y <= y1; y++)
        {
            var index =
                y * width + x;

            if (!finite[index])
                continue;

            var bin =
                ToHistogramBin(
                    temperatures[index],
                    globalMinimum,
                    globalMaximum);

            histogram[bin] +=
                direction;

            changed +=
                direction;
        }

        return changed;
    }

    private static int ToHistogramBin(
        float value,
        float minimum,
        float maximum)
    {
        var span =
            maximum - minimum;

        if (!float.IsFinite(span) ||
            span <= 0f)
        {
            return 0;
        }

        var normalized =
            (value - minimum) /
            span;

        return Math.Clamp(
            (int)Math.Round(
                normalized *
                (QuantileBinCount - 1)),
            0,
            QuantileBinCount - 1);
    }

    private static double QuantileFromHistogram(
        int[] histogram,
        int count,
        double quantile,
        float minimum,
        float maximum)
    {
        if (count <= 0)
            return double.NaN;

        if (maximum <= minimum)
            return minimum;

        var target =
            Math.Clamp(
                (int)Math.Ceiling(
                    Math.Clamp(
                        quantile,
                        0d,
                        1d) *
                    count),
                1,
                count);

        var cumulative = 0;

        for (var bin = 0;
             bin < histogram.Length;
             bin++)
        {
            cumulative +=
                histogram[bin];

            if (cumulative < target)
                continue;

            var fraction =
                bin /
                (double)(histogram.Length - 1);

            return minimum +
                   (maximum - minimum) *
                   fraction;
        }

        return maximum;
    }

    private static List<float> ComputeReferenceOutsideBoundingBox(
        float[] temperatures,
        bool[] finite,
        int width,
        int height,
        int minX,
        int minY,
        int maxX,
        int maxY,
        int radius)
    {
        var x0 =
            Math.Max(
                0,
                minX - radius);

        var y0 =
            Math.Max(
                0,
                minY - radius);

        var x1 =
            Math.Min(
                width - 1,
                maxX + radius);

        var y1 =
            Math.Min(
                height - 1,
                maxY + radius);

        var reference =
            new List<float>(
                Math.Max(
                    MinimumReferencePixels,
                    (x1 - x0 + 1) *
                    (y1 - y0 + 1)));

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (x >= minX &&
                    x <= maxX &&
                    y >= minY &&
                    y <= maxY)
                {
                    continue;
                }

                var index =
                    y * width + x;

                if (finite[index])
                {
                    reference.Add(
                        temperatures[index]);
                }
            }
        }

        if (reference.Count > 1)
            reference.Sort();

        return reference;
    }

    private static double Percentile(
        IReadOnlyList<float> sortedValues,
        double percentile)
    {
        if (sortedValues.Count == 0)
            return double.NaN;

        percentile =
            Math.Clamp(
                percentile,
                0d,
                1d);

        var position =
            (sortedValues.Count - 1) *
            percentile;

        var lower =
            (int)Math.Floor(
                position);

        var upper =
            (int)Math.Ceiling(
                position);

        if (lower == upper)
            return sortedValues[lower];

        var weight =
            position - lower;

        return sortedValues[lower] +
               (sortedValues[upper] -
                sortedValues[lower]) *
               weight;
    }
}
