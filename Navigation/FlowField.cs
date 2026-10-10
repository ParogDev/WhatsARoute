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

    /// <summary>Null when cancelled or when no goal is on (or near) walkable ground.</summary>
    public static FlowField? Build(AreaGrid g, IReadOnlyList<(int x, int y)> goals, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cost = new float[g.W * g.H];
        Array.Fill(cost, float.PositiveInfinity);
        var open = new PriorityQueue<int, float>(4096);
        var placed = new List<(int, int)>();
        foreach (var (gx, gy) in goals)
        {
            if (g.NearestWalkable(gx, gy) is not { } c) continue;
            var i = c.y * g.W + c.x;
            if (cost[i] == 0) continue;
            cost[i] = 0;
            open.Enqueue(i, 0);
            placed.Add(c);
        }
        if (placed.Count == 0) return null;

        var reached = 0;
        var pops = 0;
        while (open.TryDequeue(out var i, out var ci))
        {
            if (ci > cost[i]) continue;   // stale entry
            reached++;
            if ((++pops & 0xFFFF) == 0 && ct.IsCancellationRequested) return null;
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

    public float CostAt(int x, int y) => Grid.InBounds(x, y) ? Cost[y * Grid.W + x] : float.PositiveInfinity;

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
        var cur = start;
        if (float.IsPositiveInfinity(CostAt(cur.x, cur.y)))
        {
            var snapped = false;
            for (var r = 1; r <= 12 && !snapped; r++)
            for (var dy = -r; dy <= r && !snapped; dy++)
            for (var dx = -r; dx <= r && !snapped; dx++)
                if (!float.IsPositiveInfinity(CostAt(cur.x + dx, cur.y + dy))) { cur = (cur.x + dx, cur.y + dy); snapped = true; }
            if (!snapped) { why = $"player cell ({start.x},{start.y}) cannot reach the goal (other island, or blocked ground)"; return false; }
        }
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
