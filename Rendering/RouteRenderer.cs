using System;
using System.Numerics;
using ImGuiNET;
using WhatsARoute.Navigation;

namespace WhatsARoute.Rendering;

/// <summary>
/// Draws one route: a warm light trail from the player's feet toward the objective (fading in at the feet and out at
/// the draw distance, comets showing the direction), the terrain edges along it in cool cyan, and the whole route on
/// the large map. The camera is snapshotted once per frame and points are projected with it; every buffer is reused.
/// </summary>
public sealed class RouteRenderer
{
    private const float FadeInWorld = 60f;
    private const float EdgeFull = 450f, EdgeFade = 450f;   // edges: full within, then fade over (world units)
    private const float PulseSpeed = 240f, PulseSpacing = 180f, PulseTail = 70f, PulseHead = 18f;
    private const int MaxPulses = 24;
    private const float RingR = 8f;

    private Vector2[] _pts = new Vector2[512], _buf = new Vector2[512], _map = new Vector2[1024];
    private float[] _along = new float[512], _alpha = new float[512];
    private string? _labelSrc, _labelText;

    public void Draw(RouteFrame f, EdgeMap? edges, WhatsARouteSettings s, Matrix4x4 m, Vector2 half, bool largeMapOpen,
        Vector2 mapCenter, float mapScale, double time)
    {
        if (!f.Ready) return;
        var dl = ImGui.GetBackgroundDrawList();
        if ((dl.Flags & ImDrawListFlags.AntiAliasedLines) == 0) dl.Flags |= ImDrawListFlags.AntiAliasedLines;
        var disp = ImGui.GetIO().DisplaySize;
        var world = !(largeMapOpen && s.World.HideWithLargeMap);
        if (world && s.World.ShowEdges && edges != null) DrawEdges(dl, f, edges, s, m, half, disp);
        if (world && s.World.ShowPath) DrawPath(dl, f, s, m, half, disp, time);
        if (largeMapOpen && s.Map.ShowOnLargeMap) DrawMap(dl, f, s, mapCenter, mapScale);
    }

    // ── Path ────────────────────────────────────────────────────────

    private void DrawPath(ImDrawListPtr dl, RouteFrame f, WhatsARouteSettings s, Matrix4x4 m, Vector2 half, Vector2 disp, double time)
    {
        var w = f.Path;
        if (w.Count < 2) return;
        // Start at the player's live position: the closest point on the first few segments.
        var seg = 0; var best = float.MaxValue; var start = w[0];
        var p2 = new Vector2(f.Player.X, f.Player.Y);
        for (var i = 0; i < Math.Min(w.Count - 1, 12); i++)
        {
            var c = Closest(w[i], w[i + 1], f.Player);
            var d = Vector2.DistanceSquared(new Vector2(c.X, c.Y), p2);
            if (d < best) { best = d; seg = i; start = c; }
        }
        var far = (float)s.World.DrawDistance.Value;
        Glow.Ensure(ref _pts, w.Count + 2);
        Glow.Ensure(ref _along, w.Count + 2);
        Glow.Ensure(ref _alpha, w.Count + 2);
        var n = 0;
        float acc = 0;
        var prev = start;
        _pts[n] = Glow.Project(m, half, start); _along[n++] = 0;
        for (var i = seg + 1; i < w.Count && acc < far; i++)
        {
            acc += Vector2.Distance(new Vector2(w[i].X, w[i].Y), new Vector2(prev.X, prev.Y));
            prev = w[i];
            _pts[n] = Glow.Project(m, half, w[i]);
            _along[n++] = acc;
        }
        if (n < 2) return;
        var fadeOut = far * 0.25f;
        for (var i = 0; i < n; i++)
            _alpha[i] = Glow.Smooth(_along[i] / FadeInWorld) * (1f - Glow.Smooth((_along[i] - (far - fadeOut)) / fadeOut));

        var core = Glow.Rgb(s.World.PathCore.Value);
        var glow = Glow.Rgb(s.World.PathGlow.Value);
        var st = new Glow.Style(18f, 0.13f, 8f, 0.26f, 0.35f, 2.6f, 0.95f, glow, core, core).Scaled(s.World.PathWidth.Value);
        Glow.StrokeChunks(dl, _pts, _alpha, n, st, 1f, disp, ref _buf);
        if (s.World.ShowComets) Comets(dl, n, acc, time, glow, disp);

        if (f.Goal is { } g && acc < far)
        {
            var c = Glow.Project(m, half, g);
            if (Glow.OnScreen(c, disp, 40f)) { EndMarker(dl, c, time, glow, core); Label(dl, f.Label, c, disp, glow); }
        }
    }

    private void Comets(ImDrawListPtr dl, int count, float end, double time, Vector3 glow, Vector2 disp)
    {
        if (end < PulseHead * 2) return;
        var white = new Vector3(1f, 0.98f, 0.9f);
        var phase = (float)(time * PulseSpeed % PulseSpacing);
        var hint = 0;
        for (var i = 0; i < MaxPulses; i++)
        {
            var sPos = phase + i * PulseSpacing;
            if (sPos - PulseTail > end) break;
            var env = AlphaAt(MathF.Min(sPos, end), count);
            if (sPos > end) env *= MathF.Max(0f, 1f - (sPos - end) / PulseTail);
            if (env < 0.03f) continue;
            var n = Sub(count, sPos - PulseTail, sPos, ref hint);
            if (n >= 2 && Glow.Visible(_buf[0], _buf[n - 1], disp)) Glow.Line(dl, _buf, n, white, 0.30f * env, 6.5f);
            n = Sub(count, sPos - PulseHead, sPos, ref hint);
            if (n < 2 || !Glow.Visible(_buf[0], _buf[n - 1], disp)) continue;
            Glow.Line(dl, _buf, n, glow, 0.28f * env, 10f);
            Glow.Line(dl, _buf, n, white, 0.90f * env, 3.4f);
            dl.AddCircleFilled(_buf[n - 1], 2.8f, Glow.Col(white, 0.95f * env), 12);
        }
    }

    private float AlphaAt(float s, int count)
    {
        for (var i = 1; i < count; i++)
            if (_along[i] >= s) return _alpha[i];
        return _alpha[count - 1];
    }

    /// <summary>The piece of the projected path between two distances along it, into _buf.</summary>
    private int Sub(int count, float s0, float s1, ref int hint)
    {
        if (s0 < 0) s0 = 0;
        if (s1 > _along[count - 1]) s1 = _along[count - 1];
        if (s1 - s0 < 0.5f) return 0;
        Glow.Ensure(ref _buf, count + 2);
        var i = Math.Clamp(hint, 0, count - 2);
        while (i > 0 && _along[i] > s0) i--;
        while (i < count - 2 && _along[i + 1] <= s0) i++;
        hint = i;
        var n = 0;
        _buf[n++] = LerpAt(i, s0);
        var j = i + 1;
        while (j < count - 1 && _along[j] < s1) _buf[n++] = _pts[j++];
        _buf[n++] = LerpAt(j - 1, s1);
        return n;
    }

    private Vector2 LerpAt(int i, float s)
    {
        var d = _along[i + 1] - _along[i];
        var t = d > 1e-4f ? Math.Clamp((s - _along[i]) / d, 0f, 1f) : 0f;
        return Vector2.Lerp(_pts[i], _pts[i + 1], t);
    }

    private static void EndMarker(ImDrawListPtr dl, Vector2 c, double time, Vector3 glow, Vector3 core)
    {
        var breath = 0.5f + 0.5f * MathF.Sin((float)(time * Math.PI * 2 / 2.4));
        var r = RingR + 0.8f * breath;
        dl.AddCircle(c, r + 2.5f, Glow.Col(glow, 0.16f), 32, 3.5f);
        dl.AddCircle(c, r, Glow.Col(Glow.Ink, 0.30f), 32, 3.2f);
        dl.AddCircle(c, r, Glow.Col(core, 0.78f + 0.2f * breath), 32, 1.6f);
        dl.AddCircleFilled(c, 2.2f, Glow.Col(core, 0.9f), 12);
    }

    private void Label(ImDrawListPtr dl, string label, Vector2 c, Vector2 disp, Vector3 glow)
    {
        if (string.IsNullOrEmpty(label)) return;
        if (label != _labelSrc) { _labelSrc = label; _labelText = label.Length > 40 ? label[..37] + "..." : label; }
        var text = _labelText!;
        var size = ImGui.CalcTextSize(text) + new Vector2(12f, 6f);
        var pos = new Vector2(c.X - size.X * 0.5f, c.Y - RingR - 10f - size.Y);
        pos = new Vector2(MathF.Round(Math.Clamp(pos.X, 2f, MathF.Max(2f, disp.X - size.X - 2f))),
            MathF.Round(Math.Clamp(pos.Y, 2f, MathF.Max(2f, disp.Y - size.Y - 2f))));
        dl.AddRectFilled(pos, pos + size, Glow.Col(new Vector3(0.04f, 0.06f, 0.07f), 0.8f), size.Y * 0.5f);
        dl.AddRect(pos, pos + size, Glow.Col(glow, 0.4f), size.Y * 0.5f);
        dl.AddText(new Vector2(pos.X + 6f, pos.Y + 3f), Glow.Col(new Vector3(0.95f, 0.92f, 0.84f), 0.95f), text);
    }

    // ── Edges ───────────────────────────────────────────────────────

    private void DrawEdges(ImDrawListPtr dl, RouteFrame f, EdgeMap edges, WhatsARouteSettings s, Matrix4x4 m, Vector2 half, Vector2 disp)
    {
        var core = Glow.Rgb(s.World.EdgeCore.Value);
        var glow = Glow.Rgb(s.World.EdgeGlow.Value);
        var st = new Glow.Style(10f, 0.10f, 4.5f, 0.22f, 0.25f, 1.6f, 0.85f, glow, core, Vector3.Lerp(core, Vector3.One, 0.6f));
        var p2 = new Vector2(f.Player.X, f.Player.Y);
        foreach (var idx in f.EdgePieces)
        {
            if (idx >= edges.Pieces.Count) continue;
            var w = edges.Pieces[idx].World;
            Glow.Ensure(ref _pts, w.Length);
            Glow.Ensure(ref _alpha, w.Length);
            var any = false;
            for (var i = 0; i < w.Length; i++)
            {
                _pts[i] = Glow.Project(m, half, w[i]);
                var d = Vector2.Distance(new Vector2(w[i].X, w[i].Y), p2);
                _alpha[i] = 1f - Glow.Smooth((d - EdgeFull) / EdgeFade);
                any |= _alpha[i] > 0.02f;
            }
            if (any) Glow.StrokeChunks(dl, _pts, _alpha, w.Length, st, 1f, disp, ref _buf);
        }
    }

    // ── Large map ───────────────────────────────────────────────────

    private void DrawMap(ImDrawListPtr dl, RouteFrame f, WhatsARouteSettings s, Vector2 center, float scale)
    {
        var g = f.MapPath;
        if (g.Count < 2 || scale <= 0) return;
        Glow.Ensure(ref _map, g.Count);
        var pg = new Vector2(f.Player.X, f.Player.Y) / AreaGrid.GridToWorld;
        var cos = MathF.Cos(38.7f * MathF.PI / 180f);
        var sin = MathF.Sin(38.7f * MathF.PI / 180f);
        var ph = -f.PlayerUnclampedHeight;
        for (var i = 0; i < g.Count; i++)
        {
            var d = g[i] - pg;
            var dz = (ph + f.MapPathZ[i]) / AreaGrid.GridToWorld;
            _map[i] = center + scale * new Vector2((d.X - d.Y) * cos, (dz - (d.X + d.Y)) * sin);
        }
        var c = s.Map.Color.Value;
        var col = Glow.Rgb(c);
        dl.AddPolyline(ref _map[0], g.Count, Glow.Col(Glow.Ink, 0.55f * c.A / 255f), ImDrawFlags.None, s.Map.Width.Value + 2.5f);
        dl.AddPolyline(ref _map[0], g.Count, Glow.Col(col, c.A / 255f), ImDrawFlags.None, s.Map.Width.Value);
        dl.AddCircleFilled(_map[g.Count - 1], s.Map.Width.Value * 2.2f, Glow.Col(col, c.A / 255f), 16);
    }

    private static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = new Vector2(b.X - a.X, b.Y - a.Y);
        var len2 = ab.LengthSquared();
        var t = len2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(new Vector2(p.X - a.X, p.Y - a.Y), ab) / len2, 0, 1);
        return Vector3.Lerp(a, b, t);
    }
}
