using PokeQuad.Models;

namespace PokeQuad.Services;

public static class MemoryGrowthAnalyzer
{
    public static MemoryScopeSummary Summarize(string scope, IReadOnlyList<(double ElapsedSeconds, long Bytes)> points)
    {
        if (points.Count == 0)
        {
            return new MemoryScopeSummary(scope, 0, 0, 0, 0, 0, 0, null, null,
                "Inconclusive", "No samples were available.");
        }

        (double firstElapsed, long start) = points[0];
        (double lastElapsed, long current) = points[^1];
        double elapsedHours = Math.Max(0, lastElapsed - firstElapsed) / 3600d;
        long peak = points.Max(point => point.Bytes);
        long growth = current - start;
        double? growthPercent = start > 0 ? growth * 100d / start : null;
        double? hourly = elapsedHours > 0 ? growth / elapsedHours : null;
        (string classification, string basis) = Classify(points);

        return new MemoryScopeSummary(scope, points.Count, elapsedHours, start, current, peak, growth,
            growthPercent, hourly, classification, basis);
    }

    private static (string Classification, string Basis) Classify(IReadOnlyList<(double ElapsedSeconds, long Bytes)> points)
    {
        if (points.Count < 6 || points[^1].ElapsedSeconds - points[0].ElapsedSeconds < 300)
        {
            return ("Inconclusive", "Fewer than six samples or less than five minutes of evidence.");
        }

        long start = points[0].Bytes;
        long current = points[^1].Bytes;
        long range = points.Max(point => point.Bytes) - points.Min(point => point.Bytes);
        long scale = Math.Max(start, 1);
        double netRatio = (current - start) / (double)scale;
        double rangeRatio = range / (double)scale;
        int increases = 0;
        for (int index = 1; index < points.Count; index++)
        {
            if (points[index].Bytes > points[index - 1].Bytes) increases++;
        }

        double increasingFraction = increases / (double)(points.Count - 1);
        int tailStart = Math.Max(1, points.Count * 2 / 3);
        long tailStartBytes = points[tailStart].Bytes;
        double tailRatio = (current - tailStartBytes) / (double)Math.Max(tailStartBytes, 1);

        if (Math.Abs(netRatio) <= .05 && rangeRatio <= .15)
            return ("Stable plateau", "Net change is within 5% and the observed range is within 15%.");

        if (netRatio > .05 && Math.Abs(tailRatio) <= .03)
            return ("Initial warm-up then plateau", "Memory rose overall while the final third stayed within 3%.");

        if (netRatio > .10 && increasingFraction >= .75 && tailRatio > .03)
            return ("Sustained growth signal", "More than 75% of intervals increased, net growth exceeded 10%, and growth continued in the final third. This is not proof of a leak.");

        if (netRatio > .05 && rangeRatio > .15)
            return ("Cache-like or variable growth", "Memory increased but was not sufficiently monotonic to identify a leak.");

        return ("Variable / inconclusive", "The sample pattern did not meet a descriptive plateau or sustained-growth rule.");
    }
}
