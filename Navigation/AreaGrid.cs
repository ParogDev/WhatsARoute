using System;

namespace WhatsARoute.Navigation;

/// <summary>
/// One area's terrain, copied once on area change into flat arrays: walkability (RawPathfindingData, 0 = blocked),
/// ground height (RawTerrainHeightData, world Z) and clearance (cells to the nearest blocked cell, capped). Immutable
/// after construction, so worker threads and the render thread can share it without locks.
/// </summary>
public sealed class AreaGrid
{
    public const float GridToWorld = 250f / 23f;   // world units per grid cell
    public const byte MaxClearance = 12;

    public readonly int W, H;
    public readonly bool[] Walk;
    public readonly float[] Z;
    public readonly byte[] Clear;

    private AreaGrid(int w, int h)
    {
        W = w; H = h;
        Walk = new bool[w * h];
        Z = new float[w * h];
        Clear = new byte[w * h];
    }

    /// <summary>Null (with the broken link in <paramref name="why"/>) when the HUD has no terrain for this area yet.</summary>
    public static AreaGrid? From(int[][]? pf, float[][]? height, out string? why)
    {
        if (pf is not { Length: > 0 } || pf[0] is not { Length: > 0 })
        {
            why = "IngameData.RawPathfindingData is empty (terrain not loaded for this area yet)";
            return null;
        }
        int h = pf.Length, w = pf[0].Length;
        var g = new AreaGrid(w, h);
        for (var y = 0; y < h; y++)
        {
            var row = pf[y];
            var hr = height != null && y < height.Length ? height[y] : null;
            var o = y * w;
            for (var x = 0; x < w && x < row.Length; x++)
            {
                g.Walk[o + x] = row[x] > 0;
                if (hr != null && x < hr.Length) g.Z[o + x] = hr[x];
            }
        }
        g.BuildClearance();
        why = height is not { Length: > 0 } ? "IngameData.RawTerrainHeightData is empty: drawing at height 0" : null;
        return g;
    }

    public bool InBounds(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H;
    public bool Walkable(int x, int y) => InBounds(x, y) && Walk[y * W + x];
    public byte ClearanceAt(int x, int y) => InBounds(x, y) ? Clear[y * W + x] : (byte)0;

    /// <summary>Ground height (world Z) under a grid position; 0 outside the area.</summary>
    public float HeightAt(float gx, float gy)
    {
        int x = (int)gx, y = (int)gy;
        return InBounds(x, y) ? Z[y * W + x] : 0f;
    }

    /// <summary>
    /// Two-pass chamfer distance (1 / 1.4) from every walkable cell to the nearest blocked one, capped at
    /// <see cref="MaxClearance"/>. O(cells); paths use it to keep off walls, line-of-sight checks to keep a margin.
    /// </summary>
    private void BuildClearance()
    {
        var d = new float[W * H];
        for (var i = 0; i < d.Length; i++) d[i] = Walk[i] ? MaxClearance : 0f;
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = y * W + x;
            if (d[i] == 0) continue;
            var v = d[i];
            if (x > 0) v = MathF.Min(v, d[i - 1] + 1);
            if (y > 0)
            {
                v = MathF.Min(v, d[i - W] + 1);
                if (x > 0) v = MathF.Min(v, d[i - W - 1] + 1.4f);
                if (x < W - 1) v = MathF.Min(v, d[i - W + 1] + 1.4f);
            }
            d[i] = v;
        }
        for (var y = H - 1; y >= 0; y--)
        for (var x = W - 1; x >= 0; x--)
        {
            var i = y * W + x;
            if (d[i] == 0) continue;
            var v = d[i];
            if (x < W - 1) v = MathF.Min(v, d[i + 1] + 1);
            if (y < H - 1)
            {
                v = MathF.Min(v, d[i + W] + 1);
                if (x < W - 1) v = MathF.Min(v, d[i + W + 1] + 1.4f);
                if (x > 0) v = MathF.Min(v, d[i + W - 1] + 1.4f);
            }
            d[i] = v;
        }
        for (var i = 0; i < d.Length; i++) Clear[i] = (byte)MathF.Min(MaxClearance, d[i]);
    }

    /// <summary>Nearest walkable cell within <paramref name="radius"/> (square rings), or null.</summary>
    public (int x, int y)? NearestWalkable(int cx, int cy, int radius = 25)
    {
        if (Walkable(cx, cy)) return (cx, cy);
        for (var r = 1; r <= radius; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
            if (Walkable(cx + dx, cy + dy)) return (cx + dx, cy + dy);
        }
        return null;
    }
}
