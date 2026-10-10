using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace WhatsARoute.Navigation;

/// <summary>
/// The outline of the walkable ground (ledges, walls, bridge rails, arch pillars), traced once per area on a worker:
/// every walkable/blocked cell boundary becomes a directed unit edge, edges are chained into contours, straight runs
/// collapsed, corners rounded, heights taken from the walkable side. Contours are cut into short pieces indexed in a
/// coarse spatial hash, so each frame only the pieces near the route are drawn.
/// </summary>
public sealed class EdgeMap
{
    public const int PiecePoints = 12;
    private const int Bucket = 32;

    public sealed class Piece
    {
        public required Vector3[] World;          // world units, z = ground at the wall foot
        public float MinX, MinY, MaxX, MaxY;      // grid units
    }

    public readonly List<Piece> Pieces = new();
    private readonly List<int>[] _buckets;
    private readonly int _bw, _bh;
    public readonly int Contours;
    public readonly double BuildMs;

    private EdgeMap(int w, int h)
    {
        _bw = (w + Bucket - 1) / Bucket;
        _bh = (h + Bucket - 1) / Bucket;
        _buckets = new List<int>[_bw * _bh];
    }

    private EdgeMap(int w, int h, int contours, double ms) : this(w, h)
    {
        Contours = contours;
        BuildMs = ms;
    }

    public static EdgeMap? Build(AreaGrid g, int minContourEdges, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int W = g.W, H = g.H, VW = W + 1;
        // Outgoing edges per corner vertex: at most 2 (a saddle), packed as end-vertex ids; -1 = none.
        var out1 = new int[VW * (H + 1)];
        var out2 = new int[VW * (H + 1)];
        Array.Fill(out1, -1);
        Array.Fill(out2, -1);
        var edgeCount = 0;
        void Add(int ax, int ay, int bx, int by)
        {
            var a = ay * VW + ax;
            var b = by * VW + bx;
            if (out1[a] < 0) out1[a] = b; else out2[a] = b;
            edgeCount++;
        }
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                if (!g.Walk[y * W + x]) continue;
                // Clockwise around the walkable cell (y down): top, right, bottom, left where the neighbour is blocked.
                if (!g.Walkable(x, y - 1)) Add(x, y, x + 1, y);
                if (!g.Walkable(x + 1, y)) Add(x + 1, y, x + 1, y + 1);
                if (!g.Walkable(x, y + 1)) Add(x + 1, y + 1, x, y + 1);
                if (!g.Walkable(x - 1, y)) Add(x, y + 1, x, y);
            }
            if ((y & 63) == 0 && ct.IsCancellationRequested) return null;
        }

        var contours = 0;
        var chain = new List<Vector2>(1024);
        var tmpA = new List<Vector2>(1024);
        var tmpB = new List<Vector2>(1024);
        var map = new EdgeMap(W, H);
        for (var v = 0; v < out1.Length; v++)
        {
            while (out1[v] >= 0 || out2[v] >= 0)
            {
                chain.Clear();
                var cur = v;
                chain.Add(new Vector2(cur % VW, cur / VW));
                while (true)
                {
                    int next;
                    if (out1[cur] >= 0) { next = out1[cur]; out1[cur] = -1; }
                    else if (out2[cur] >= 0) { next = out2[cur]; out2[cur] = -1; }
                    else break;
                    cur = next;
                    chain.Add(new Vector2(cur % VW, cur / VW));
                    if (cur == v) break;
                }
                if (chain.Count - 1 < minContourEdges) continue;
                contours++;
                Collapse(chain, tmpA);
                Chaikin(tmpA, tmpB);
                Chaikin(tmpB, tmpA);
                map.AddPieces(g, tmpA);
            }
            if ((v & 0xFFFF) == 0 && ct.IsCancellationRequested) return null;
        }
        var done = new EdgeMap(W, H, contours, sw.Elapsed.TotalMilliseconds);
        done.Pieces.AddRange(map.Pieces);
        for (var i = 0; i < done.Pieces.Count; i++) done.Index(i);
        return done;
    }

    /// <summary>Drops the points in the middle of straight runs (the grid makes many).</summary>
    private static void Collapse(List<Vector2> src, List<Vector2> dst)
    {
        dst.Clear();
        dst.Add(src[0]);
        for (var i = 1; i < src.Count - 1; i++)
        {
            var d0 = src[i] - src[i - 1];
            var d1 = src[i + 1] - src[i];
            if (d0 != d1) dst.Add(src[i]);
        }
        dst.Add(src[^1]);
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

    private void AddPieces(AreaGrid g, List<Vector2> pts)
    {
        for (var s = 0; s < pts.Count - 1; s += PiecePoints - 1)
        {
            var n = Math.Min(PiecePoints, pts.Count - s);
            if (n < 2) break;
            var w = new Vector3[n];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var k = 0; k < n; k++)
            {
                var p = pts[s + k];
                w[k] = new Vector3(p.X * AreaGrid.GridToWorld, p.Y * AreaGrid.GridToWorld, FootHeight(g, p));
                minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y);
                maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y);
            }
            Pieces.Add(new Piece { World = w, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY });
        }
    }

    /// <summary>Height of the walkable cells touching a corner point (the wall's foot), averaged.</summary>
    private static float FootHeight(AreaGrid g, Vector2 p)
    {
        int x = (int)MathF.Round(p.X), y = (int)MathF.Round(p.Y);
        float sum = 0; var n = 0;
        for (var dy = -1; dy <= 0; dy++)
        for (var dx = -1; dx <= 0; dx++)
            if (g.Walkable(x + dx, y + dy)) { sum += g.Z[(y + dy) * g.W + x + dx]; n++; }
        return n > 0 ? sum / n : g.HeightAt(p.X, p.Y);
    }

    private void Index(int i)
    {
        var p = Pieces[i];
        int bx = Math.Clamp((int)((p.MinX + p.MaxX) * 0.5f) / Bucket, 0, _bw - 1);
        int by = Math.Clamp((int)((p.MinY + p.MaxY) * 0.5f) / Bucket, 0, _bh - 1);
        (_buckets[by * _bw + bx] ??= new List<int>()).Add(i);
    }

    /// <summary>
    /// Pieces within <paramref name="corridor"/> cells of any of <paramref name="gridPoints"/>, into
    /// <paramref name="result"/> without duplicates. <paramref name="stamp"/> (sized to Pieces, reused) and
    /// <paramref name="stampId"/> (a fresh value per query) make the dedupe allocation-free.
    /// </summary>
    public void Query(List<Vector2> gridPoints, float corridor, List<int> result, int[] stamp, int stampId)
    {
        result.Clear();
        var reach = (int)MathF.Ceiling((corridor + PiecePoints) / Bucket);
        foreach (var q in gridPoints)
        {
            int cx = (int)q.X / Bucket, cy = (int)q.Y / Bucket;
            for (var by = Math.Max(0, cy - reach); by <= Math.Min(_bh - 1, cy + reach); by++)
            for (var bx = Math.Max(0, cx - reach); bx <= Math.Min(_bw - 1, cx + reach); bx++)
            {
                var list = _buckets[by * _bw + bx];
                if (list == null) continue;
                foreach (var i in list)
                {
                    if (stamp[i] == stampId) continue;
                    var p = Pieces[i];
                    var dx = MathF.Max(0, MathF.Max(p.MinX - q.X, q.X - p.MaxX));
                    var dy = MathF.Max(0, MathF.Max(p.MinY - q.Y, q.Y - p.MaxY));
                    if (dx * dx + dy * dy > corridor * corridor) continue;
                    stamp[i] = stampId;
                    result.Add(i);
                }
            }
        }
    }
}
