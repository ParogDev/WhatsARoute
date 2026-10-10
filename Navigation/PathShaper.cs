using System;
using System.Collections.Generic;
using System.Numerics;

namespace WhatsARoute.Navigation;

/// <summary>
/// Turns a staircase of grid cells into the line a player would walk: line-of-sight simplification (with a wall
/// margin), Chaikin rounding of the corners, then resampling so the drawn line follows the ground height over
/// ramps, stairs and bridges. All buffers are reused; one instance per thread.
/// </summary>
public sealed class PathShaper
{
    private const int MaxLegCells = 40;          // keep legs short enough that resampling catches height changes
    private const float ResampleCells = 1.5f;

    private readonly List<Vector2> _pull = new(128), _a = new(512), _b = new(512);
    private readonly List<float> _z = new(1024);

    /// <summary>World-space polyline (x, y in world units, z = ground) into <paramref name="world"/>.</summary>
    public void Shape(AreaGrid g, List<(int x, int y)> cells, List<Vector3> world)
    {
        world.Clear();
        if (cells.Count == 0) return;
        Pull(g, cells);
        // Two Chaikin passes: corners become curves; the ends stay put.
        Chaikin(_pull, _a);
        Chaikin(_a, _b);
        Resample(g, _b, world);
    }

    private void Pull(AreaGrid g, List<(int x, int y)> cells)
    {
        _pull.Clear();
        var i = 0;
        _pull.Add(Center(cells[0]));
        while (i < cells.Count - 1)
        {
            var j = i + 1;
            while (j + 1 < cells.Count && j + 1 - i <= MaxLegCells && LineClear(g, cells[i], cells[j + 1])) j++;
            _pull.Add(Center(cells[j]));
            i = j;
        }
    }

    private static Vector2 Center((int x, int y) c) => new(c.x + 0.5f, c.y + 0.5f);

    /// <summary>Walkable along the line with at least one cell of clearance (so it doesn't clip corners).</summary>
    private static bool LineClear(AreaGrid g, (int x, int y) a, (int x, int y) b)
    {
        var n = Math.Max(Math.Abs(b.x - a.x), Math.Abs(b.y - a.y)) * 2;
        for (var k = 1; k < n; k++)
        {
            var t = k / (float)n;
            var x = (int)(a.x + (b.x - a.x) * t + 0.5f);
            var y = (int)(a.y + (b.y - a.y) * t + 0.5f);
            if (g.ClearanceAt(x, y) < 1) return false;
        }
        return true;
    }

    private static void Chaikin(List<Vector2> src, List<Vector2> dst)
    {
        dst.Clear();
        if (src.Count < 3) { dst.AddRange(src); return; }
        dst.Add(src[0]);
        for (var i = 0; i < src.Count - 1; i++)
        {
            dst.Add(Vector2.Lerp(src[i], src[i + 1], 0.25f));
            dst.Add(Vector2.Lerp(src[i], src[i + 1], 0.75f));
        }
        dst.Add(src[^1]);
    }

    private void Resample(AreaGrid g, List<Vector2> pts, List<Vector3> world)
    {
        // Points every ~1.5 cells (16 world units), ground height under each, then a light 5-tap average on the
        // heights only: the height grid has steps at cliff edges that would otherwise read as spikes in the line.
        var flat = _a;
        flat.Clear();
        flat.Add(pts[0]);
        for (var i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var steps = Math.Max(1, (int)(Vector2.Distance(a, b) / ResampleCells));
            for (var s = 1; s <= steps; s++) flat.Add(Vector2.Lerp(a, b, s / (float)steps));
        }
        _z.Clear();
        foreach (var p in flat) _z.Add(g.HeightAt(p.X, p.Y));
        for (var i = 0; i < flat.Count; i++)
        {
            float sum = 0; var n = 0;
            for (var k = Math.Max(0, i - 2); k <= Math.Min(flat.Count - 1, i + 2); k++) { sum += _z[k]; n++; }
            world.Add(new Vector3(flat[i].X * AreaGrid.GridToWorld, flat[i].Y * AreaGrid.GridToWorld, sum / n));
        }
    }
}
