using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace WhatsARoute.Navigation;

/// <summary>
/// Cost-to-goal for every reachable cell of the area (Dijkstra outward from the goal cells, 8-connected, no corner
/// cutting). Built once per objective on a worker; afterwards the path from ANY cell is a steepest descent, O(path
/// length), so re-planning on every step costs microseconds and the answer depends only on where the player stands
/// (stateless). Several goal cells make it "nearest of these" for free.
/// Step cost grows near walls (clearance below <see cref="ComfortClearance"/>) so the route keeps to the middle of
/// corridors and bridges the way a player walks, instead of hugging corners.
/// </summary>
public sealed class FlowField
{
    public const int ComfortClearance = 4;
    private const float WallPenalty = 0.45f;   // extra cost per cell of missing clearance

    public readonly AreaGrid Grid;
    public readonly float[] Cost;
    public readonly int Reached;
    public readonly double BuildMs;
    public readonly (int x, int y)[] Goals;

    private static readonly int[] Dx = [1, -1, 0, 0, 1, 1, -1, -1];
    private static readonly int[] Dy = [0, 0, 1, -1, 1, -1, 1, -1];

    private FlowField(AreaGrid grid, float[] cost, int reached, double ms, (int, int)[] goals)
    {
        Grid = grid; Cost = cost; Reached = reached; BuildMs = ms; Goals = goals;
    }

    /// <summary>
    /// How far a goal may sit from walkable ground. Interaction markers can stand inside blocked geometry: G2_6's
    /// TitanGrottoPointOfInterest (a ProximityTrigger) is 27 cells from the nearest walkable cell.
    /// </summary>
    public const int GoalSnapCells = 64;

    /// <summary>Null when cancelled, or (with <paramref name="why"/>) when no goal is on or near walkable ground.</summary>
    public static FlowField? Build(AreaGrid g, IReadOnlyList<(int x, int y)> goals, CancellationToken ct) => Build(g, goals, ct, out _);

    public static FlowField? Build(AreaGrid g, IReadOnlyList<(int x, int y)> goals, CancellationToken ct, out string? why)
    {
        why = null;
        var sw = Stopwatch.StartNew();
        var cost = Rent(g.W * g.H);
        Array.Fill(cost, float.PositiveInfinity);
        var open = new PriorityQueue<int, float>(4096);
        var placed = new List<(int, int)>();
        foreach (var (gx, gy) in goals)
        {
            if (g.NearestWalkable(gx, gy, GoalSnapCells) is not { } c) continue;
            var i = c.y * g.W + c.x;
            if (cost[i] == 0) continue;
            cost[i] = 0;
            open.Enqueue(i, 0);
            placed.Add(c);
        }
        if (placed.Count == 0)
        {
            var (fx, fy) = goals.Count > 0 ? goals[0] : (0, 0);
            why = $"{goals.Count} goal(s), none within {GoalSnapCells} cells of walkable ground (first at {fx},{fy})";
            Return(cost);
            return null;
        }

        var reached = 0;
        var pops = 0;
        while (open.TryDequeue(out var i, out var ci))
        {
            if (ci > cost[i]) continue;   // stale entry
            reached++;
            if ((++pops & 0xFFFF) == 0 && ct.IsCancellationRequested) { Return(cost); return null; }
            int x = i % g.W, y = i / g.W;
            for (var k = 0; k < 8; k++)
            {
                int nx = x + Dx[k], ny = y + Dy[k];
                if (!g.Walkable(nx, ny)) continue;
                if (k >= 4 && (!g.Walkable(x + Dx[k], y) || !g.Walkable(x, y + Dy[k]))) continue;
                var n = ny * g.W + nx;
                var clear = g.Clear[n];
                var step = (k >= 4 ? 1.41421f : 1f) * (1f + (clear < ComfortClearance ? (ComfortClearance - clear) * WallPenalty : 0f));
                var nc = ci + step;
                if (nc >= cost[n]) continue;
                cost[n] = nc;
                open.Enqueue(n, nc);
            }
        }
        return new FlowField(g, cost, reached, sw.Elapsed.TotalMilliseconds, placed.ToArray());
    }

    // Cost arrays are one float per cell: 17.4 MB for G2_4_3 (3244x1403), a large-object-heap allocation per objective
    // change (route-bench, 2026-10-10). A few are kept for reuse; the owner hands a field back with Recycle once nothing
    // reads it any more.
    private const int PoolSize = 2;
    private static readonly List<float[]> Pool = new();

    private static float[] Rent(int n)
    {
        lock (Pool)
        {
            for (var i = Pool.Count - 1; i >= 0; i--)
                if (Pool[i].Length == n) { var a = Pool[i]; Pool.RemoveAt(i); return a; }
            if (Pool.Count > 0 && Pool[0].Length != n) Pool.Clear();   // another area's size: let them go
        }
        return new float[n];
    }

    private static void Return(float[] a)
    {
        lock (Pool) { if (Pool.Count < PoolSize) Pool.Add(a); }
    }

    private bool _recycled;

    /// <summary>
    /// Gives this field's cost array back for the next build. Only when nothing reads the field any more: CostAt,
    /// Snap and Descend on it afterwards read another objective's costs.
    /// </summary>
    public void Recycle()
    {
        if (_recycled) return;
        _recycled = true;
        Return(Cost);
    }

    public float CostAt(int x, int y) => Grid.InBounds(x, y) ? Cost[y * Grid.W + x] : float.PositiveInfinity;

    private const int SnapRadius = 12;

    /// <summary>
    /// <paramref name="start"/>, or the nearest cell the field reached within 12 cells WITHOUT crossing blocked ground
    /// (the player can stand on a blocked cell for a moment, or in a pocket only joined diagonally past a corner); null
    /// when the start is on another island than every goal. A plain 12-cell ring search used to jump a thin wall or
    /// chasm onto the goal's island and draw a route the player could not walk (review note, 2026-10-10).
    /// </summary>
    public (int x, int y)? Snap((int x, int y) start)
    {
        if (!float.IsPositiveInfinity(CostAt(start.x, start.y))) return start;
        // Standing on blocked ground: the nearest walkable cell is the side the player is on.
        if (Grid.NearestWalkable(start.x, start.y, SnapRadius) is not { } from) return null;
        if (!float.IsPositiveInfinity(CostAt(from.x, from.y))) return from;
        // Flood the walkable cells around it (8-connected, corners allowed: a pocket the field's no-corner-cutting rule
        // left out) until one the field reached.
        const int side = 2 * SnapRadius + 1;
        Span<bool> seen = stackalloc bool[side * side];
        Span<int> queue = stackalloc int[side * side];
        int head = 0, tail = 0;
        int ox = start.x - SnapRadius, oy = start.y - SnapRadius;
        seen[(from.y - oy) * side + (from.x - ox)] = true;
        queue[tail++] = (from.y - oy) * side + (from.x - ox);
        while (head < tail)
        {
            var q = queue[head++];
            int lx = q % side, ly = q / side;
            for (var k = 0; k < 8; k++)
            {
                int nlx = lx + Dx[k], nly = ly + Dy[k];
                if ((uint)nlx >= side || (uint)nly >= side) continue;
                var n = nly * side + nlx;
                if (seen[n]) continue;
                seen[n] = true;
                int x = ox + nlx, y = oy + nly;
                if (!Grid.Walkable(x, y)) continue;
                if (!float.IsPositiveInfinity(Cost[y * Grid.W + x])) return (x, y);
                queue[tail++] = n;
            }
        }
        // A speck of a few walkable cells is pathfinding-grid noise the player only brushes past (route-bench: Act 2's
        // dropped islands are mostly 1-30 cells); treat it as the ground around it, as the ring search did. Real ground
        // on the other side of a wall or chasm is larger: the player is cut off from the goal.
        return tail < SpeckCells ? NearestReached(start) : null;
    }

    private const int SpeckCells = 40;

    private (int x, int y)? NearestReached((int x, int y) start)
    {
        for (var r = 1; r <= SnapRadius; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
            if (!float.IsPositiveInfinity(CostAt(start.x + dx, start.y + dy))) return (start.x + dx, start.y + dy);
        }
        return null;
    }

    /// <summary>
    /// Cells from <paramref name="start"/> down the cost gradient to a goal, into <paramref name="cells"/> (cleared
    /// first, reused). A start off the field (blocked cell, other island) snaps to the nearest reached cell. Returns
    /// false with the reason when there is no way down.
    /// </summary>
    public bool Descend((int x, int y) start, List<(int x, int y)> cells, int maxCells, out string? why)
    {
        cells.Clear();
        why = null;
        var g = Grid;
        if (Snap(start) is not { } cur) { why = $"player cell ({start.x},{start.y}) cannot reach the goal (other island, or blocked ground)"; return false; }
        cells.Add(cur);
        while (cells.Count < maxCells)
        {
            var c = CostAt(cur.x, cur.y);
            if (c <= 0) return true;
            var best = c;
            var next = cur;
            for (var k = 0; k < 8; k++)
            {
                int nx = cur.x + Dx[k], ny = cur.y + Dy[k];
                if (k >= 4 && (!g.Walkable(cur.x + Dx[k], cur.y) || !g.Walkable(cur.x, cur.y + Dy[k]))) continue;
                var nc = CostAt(nx, ny);
                if (nc < best) { best = nc; next = (nx, ny); }
            }
            if (next == cur) { why = $"descent stuck at ({cur.x},{cur.y}), cost {c:0.#}"; return false; }
            cur = next;
            cells.Add(cur);
        }
        return true;   // truncated at maxCells: the caller only wanted the near part
    }
}
