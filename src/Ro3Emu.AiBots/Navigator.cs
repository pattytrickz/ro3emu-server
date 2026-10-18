using Ro3Emu.Server.Maps;

namespace Ro3Emu.AiBots;

public readonly record struct Point3(float X, float Y, float Z)
{
    public float FlatDistance(float x, float z) => MathF.Sqrt((X - x) * (X - x) + (Z - z) * (Z - z));
}

/// <summary>
/// Paths on the client's walkability grid (1 m cells), so bots walk around walls the way players do and every step
/// passes the server's walk check (SceneService.AcceptWalkAsync: walkable cell, straight segment walkable).
/// </summary>
public static class Navigator
{
    private static readonly (int Dx, int Dz, float Cost)[] Steps =
        [(1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.414f), (1, -1, 1.414f), (-1, 1, 1.414f), (-1, -1, 1.414f)];

    /// <summary>
    /// A* from <paramref name="from"/> to (tx, tz), at most <paramref name="maxNodes"/> cells expanded, smoothed into
    /// straight walkable segments. Ends at the reachable cell nearest the target when the target itself is not
    /// walkable but something within <paramref name="slack"/> m is. Null when there is no way there.
    /// </summary>
    public static List<Point3>? FindPath(NavGrid nav, Point3 from, float tx, float tz, float slack = 1.5f, int maxNodes = 30000)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref Calls);
        try { return FindPathCore(nav, from, tx, tz, slack, maxNodes); }
        finally { Interlocked.Add(ref Ticks, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
    }

    /// <summary>Paths planned, cells expanded and time spent (Stopwatch ticks), for the status line.</summary>
    public static long Calls, Expanded, Ticks;

    /// <summary>
    /// A straight walk the server accepts with room to spare: the centre line and two lines 0.4 m to either side are
    /// walkable (the server samples its own points along the segment, so a line grazing a wall corner can fail there).
    /// </summary>
    public static bool Clear(NavGrid nav, float ax, float ay, float az, float bx, float bz)
    {
        if (!nav.CanWalk(ax, ay, az, bx, bz)) return false;
        float dx = bx - ax, dz = bz - az, d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 0.01f) return true;
        float ox = -dz / d * 0.4f, oz = dx / d * 0.4f;
        return nav.CanWalk(ax + ox, ay, az + oz, bx + ox, bz + oz) && nav.CanWalk(ax - ox, ay, az - oz, bx - ox, bz - oz);
    }

    private static List<Point3>? FindPathCore(NavGrid nav, Point3 from, float tx, float tz, float slack, int maxNodes)
    {
        if (Clear(nav, from.X, from.Y, from.Z, tx, tz) && nav.WalkHeight(tx, tz, from.Y) is { } direct)
            return [from, new Point3(tx, direct, tz)];

        int sx = (int)MathF.Floor(from.X), sz = (int)MathF.Floor(from.Z);
        int gx = (int)MathF.Floor(tx), gz = (int)MathF.Floor(tz);
        static long Key(int x, int z) => ((long)x << 32) | (uint)z;
        float H(int x, int z)
        {
            int dx = Math.Abs(x - gx), dz = Math.Abs(z - gz);
            return dx + dz + (1.414f - 2) * Math.Min(dx, dz);
        }

        var open = new PriorityQueue<(int X, int Z), float>();
        var cost = new Dictionary<long, float> { [Key(sx, sz)] = 0 };
        var height = new Dictionary<long, float> { [Key(sx, sz)] = from.Y };
        var parent = new Dictionary<long, long>();
        open.Enqueue((sx, sz), H(sx, sz));
        long best = Key(sx, sz);
        float bestH = H(sx, sz);
        int expanded = 0;
        while (open.TryDequeue(out var cell, out _) && expanded++ < maxNodes)
        {
            long key = Key(cell.X, cell.Z);
            float here = height[key], g = cost[key];
            float h = H(cell.X, cell.Z);
            if (h < bestH) { bestH = h; best = key; }
            if (cell.X == gx && cell.Z == gz) { best = key; bestH = 0; break; }
            foreach (var (dx, dz, step) in Steps)
            {
                int nx = cell.X + dx, nz = cell.Z + dz;
                if (Walk(nav, nx, nz, here) is not { } nh) continue;
                // No corner cutting: a diagonal needs both of its side cells.
                if (dx != 0 && dz != 0 && (Walk(nav, cell.X + dx, cell.Z, here) == null || Walk(nav, cell.X, cell.Z + dz, here) == null)) continue;
                long nk = Key(nx, nz);
                float ng = g + step;
                if (cost.TryGetValue(nk, out float old) && old <= ng) continue;
                cost[nk] = ng;
                height[nk] = nh;
                parent[nk] = key;
                open.Enqueue((nx, nz), ng + H(nx, nz));
            }
        }
        Interlocked.Add(ref Expanded, expanded);
        if (bestH > slack + 1) return null;

        var cells = new List<Point3>();
        for (long k = best; ; k = parent[k])
        {
            int x = (int)(k >> 32), z = (int)(uint)k;
            cells.Add(new Point3(x + 0.5f, height[k], z + 0.5f));
            if (!parent.ContainsKey(k)) break;
        }
        cells.Reverse();
        cells[0] = from;
        return Smooth(nav, cells);
    }

    private static float? Walk(NavGrid nav, int x, int z, float fromHeight) =>
        nav.WalkHeight(x + 0.5f, z + 0.5f, fromHeight) is { } h && MathF.Abs(h - fromHeight) <= NavGrid.WalkHeightTolerance ? h : null;

    /// <summary>Keeps only the corners: from each kept point, jump to the farthest point still in a straight walkable line.</summary>
    private static List<Point3> Smooth(NavGrid nav, List<Point3> cells)
    {
        var path = new List<Point3> { cells[0] };
        int i = 0;
        while (i < cells.Count - 1)
        {
            int j = Math.Min(cells.Count - 1, i + 60); // a bounded look-ahead keeps long paths cheap
            while (j > i + 1 && !Clear(nav, cells[i].X, cells[i].Y, cells[i].Z, cells[j].X, cells[j].Z)) j--;
            path.Add(cells[j]);
            i = j;
        }
        return path;
    }

    /// <summary>A random walkable point within <paramref name="radius"/> m of (x, z) on the same floor, or null.</summary>
    public static Point3? RandomPointNear(NavGrid nav, Random random, float x, float y, float z, float radius, int tries = 20)
    {
        for (int i = 0; i < tries; i++)
        {
            float a = (float)(random.NextDouble() * MathF.Tau), r = (float)(Math.Sqrt(random.NextDouble()) * radius);
            float px = x + r * MathF.Cos(a), pz = z + r * MathF.Sin(a);
            if (nav.WalkHeight(px, pz, y) is { } h && MathF.Abs(h - y) <= 6) return new Point3(px, h, pz);
        }
        return null;
    }
}
