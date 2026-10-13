namespace Lasero.Core.Trace;

/// <summary>
/// Distance lookup against a closed reference polyline on a grid whose cell is the distance limit: a point
/// with no reference segment in its 3 x 3 cell neighbourhood is further away than the limit. Used to prove
/// that a fitted curve never strays from the contour it was fitted to.
/// </summary>
internal sealed class PolylineProximity
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double _limit;
    private readonly Dictionary<long, List<int>> _cells = new();

    public PolylineProximity(double[] x, double[] y, double limit)
    {
        _x = x;
        _y = y;
        _limit = Math.Max(limit, 1e-9);
        for (var i = 0; i < x.Length; i++)
        {
            var j = (i + 1) % x.Length;
            // A segment is registered in every cell its bounding box touches (segments can be long when the
            // reference has been thinned), so the neighbourhood lookup cannot miss it.
            var cx0 = Cell(Math.Min(x[i], x[j]));
            var cx1 = Cell(Math.Max(x[i], x[j]));
            var cy0 = Cell(Math.Min(y[i], y[j]));
            var cy1 = Cell(Math.Max(y[i], y[j]));
            for (var cx = cx0; cx <= cx1; cx++)
            {
                for (var cy = cy0; cy <= cy1; cy++)
                {
                    var key = Key(cx, cy);
                    if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                    list.Add(i);
                }
            }
        }
    }

    private int Cell(double v) => (int)Math.Floor(v / _limit);
    private static long Key(int cx, int cy) => (cx + 2_000_000L) * 8_000_000L + (cy + 2_000_000L);

    /// <summary>Distance to the reference polyline, or infinity when it is further than the limit.</summary>
    public double DistanceTo(double px, double py)
    {
        var cx = Cell(px);
        var cy = Cell(py);
        var best = double.PositiveInfinity;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!_cells.TryGetValue(Key(cx + dx, cy + dy), out var list)) continue;
                foreach (var i in list)
                {
                    var j = (i + 1) % _x.Length;
                    var abx = _x[j] - _x[i];
                    var aby = _y[j] - _y[i];
                    var lengthSquared = abx * abx + aby * aby;
                    var t = lengthSquared < 1e-18 ? 0 : Math.Clamp(((px - _x[i]) * abx + (py - _y[i]) * aby) / lengthSquared, 0, 1);
                    var ex = px - (_x[i] + abx * t);
                    var ey = py - (_y[i] + aby * t);
                    best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
                }
            }
        }
        return best;
    }
}
