using Windows.Foundation;

namespace SideCarMonitor.Services;

/// <summary>
/// Packs tiles of different aspect ratios (dual-lens ultra-wide, 16:9, 4:3 doorbells) into rows so the
/// every camera gets a reasonably large view, like a justified photo gallery. Every tile in a row
/// shares one height; the whole block is scaled down to fit and centered.
/// </summary>
public static class JustifiedLayout
{
    /// <summary>Returns one rectangle per input aspect, in input order.</summary>
    public static Rect[] Compute(IReadOnlyList<double> aspects, double width, double height, double gap)
    {
        var n = aspects.Count;
        if (n == 0 || width <= 0 || height <= 0) return new Rect[n];

        // Try every ordering (small n) and every way to split it into consecutive rows; keep the
        // most balanced layout (largest geometric-mean tile area). The identity order is tried first and only replaced by a
        // clearly better one, so tiles don't reshuffle for marginal gains.
        var orders = n <= 6 ? Permutations(Enumerable.Range(0, n).ToArray()) : [Enumerable.Range(0, n).ToArray()];
        Rect[]? best = null;
        var bestScore = 0.0;

        foreach (var order in orders)
        {
            for (var mask = 0; mask < 1 << (n - 1); mask++)
            {
                var rects = Evaluate(order, mask, aspects, width, height, gap, out var score);
                if (best is null || score > bestScore + 0.05)
                {
                    best = rects;
                    bestScore = score;
                }
            }
        }
        return best!;
    }

    private static Rect[] Evaluate(int[] order, int mask, IReadOnlyList<double> aspects,
                                   double width, double height, double gap, out double score)
    {
        // Split order into rows: bit i set => row break after position i.
        var rows = new List<List<int>> { new() };
        for (var i = 0; i < order.Length; i++)
        {
            rows[^1].Add(order[i]);
            if (i < order.Length - 1 && (mask & (1 << i)) != 0) rows.Add([]);
        }

        // Natural height of each row when it spans the full width.
        var rowHeights = rows.Select(r => (width - gap * (r.Count - 1)) / r.Sum(i => aspects[i])).ToArray();
        var verticalGaps = gap * (rows.Count - 1);
        var scale = Math.Min(1.0, Math.Max(0, height - verticalGaps) / rowHeights.Sum());

        var rects = new Rect[order.Length];
        var totalHeight = rowHeights.Sum() * scale + verticalGaps;
        var y = Math.Max(0, (height - totalHeight) / 2);
        score = 0;

        for (var r = 0; r < rows.Count; r++)
        {
            var h = rowHeights[r] * scale;
            var rowWidth = rows[r].Sum(i => aspects[i] * h) + gap * (rows[r].Count - 1);
            var x = Math.Max(0, (width - rowWidth) / 2);
            foreach (var i in rows[r])
            {
                var w = aspects[i] * h;
                rects[i] = new Rect(Math.Floor(x), Math.Floor(y), Math.Floor(w), Math.Floor(h));
                // Sum of log(area) = geometric mean: favors layouts where every camera is reasonably big
                // over ones with a single giant tile and several thumbnails.
                score += Math.Log(Math.Max(1, w * h));
                x += w + gap;
            }
            y += h + gap;
        }
        return rects;
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var p in Permutations(rest))
                yield return [items[i], .. p];
        }
    }
}
