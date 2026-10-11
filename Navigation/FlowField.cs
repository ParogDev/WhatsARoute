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
    /// <paramref name="start"/>, or a reached cell near it; null when the start is on another island than every goal.
    /// The field floods with the no-corner-cutting moves below, so a walkable cell it did not reach IS on another
    /// island: a ledge or a room past a wall or chasm. Snapping that to any reached cell within 12 cells (the old ring
    /// search) drew a route across the gap the player could not walk (review note, 2026-10-10). A player on blocked
    /// ground for a moment belongs to the nearest walkable cell. Only a speck of under 40 cells counts as the ground
    /// around it: grid noise the player brushes past (route-bench: Act 2's unreached cells near the field are mostly on
    /// 1-30 cell specks; the rest are 53+ cell ledges and whole regions).
    /// </summary>
    public (int x, int y)? Snap((int x, int y) start)
    {
        if (!float.IsPositiveInfinity(CostAt(start.x, start.y))) return start;
        if (Grid.NearestWalkable(start.x, start.y, SnapRadius) is not { } from) return null;
        if (!float.IsPositiveInfinity(CostAt(from.x, from.y))) return from;
        return IslandSize(from) < SpeckCells ? NearestReached(start) : null;
    }

    private const int SpeckCells = 40;

    /// <summary>Cells of the walkable island holding <paramref name="from"/> (the field's own moves), counted up to <see cref="SpeckCells"/>.</summary>
    private int IslandSize((int x, int y) from)
    {
        var g = Grid;
        Span<int> cells = stackalloc int[SpeckCells];
        var n = 0;
        cells[n++] = from.y * g.W + from.x;
        for (var head = 0; head < n; head++)
        {
            int x = cells[head] % g.W, y = cells[head] / g.W;
            for (var k = 0; k < 8; k++)
            {
                int nx = x + Dx[k], ny = y + Dy[k];
                if (!g.Walkable(nx, ny)) continue;
                if (k >= 4 && (!g.Walkable(x + Dx[k], y) || !g.Walkable(x, y + Dy[k]))) continue;
                var i = ny * g.W + nx;
                if (cells[..n].Contains(i)) continue;
                cells[n++] = i;
                if (n == SpeckCells) return n;
            }
        }
        return n;
    }

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
    /// first, reused). A start off the field snaps as <see cref="Snap"/> says (blocked ground or a speck: a nearby reached
    /// cell; another island: no way down). Returns false with the reason when there is no way down.
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
