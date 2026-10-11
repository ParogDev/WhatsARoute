using System;
using System.Numerics;
using ImGuiNET;
using WhatsARoute.Navigation;

namespace WhatsARoute.Rendering;

/// <summary>
/// Draws one route: a warm light trail from the player's feet toward the objective (fading in at the feet and out at
/// the draw distance, comets showing the direction), the terrain edges along it in cool cyan, and the whole route on
/// the large map. A detected objective (RouteFrame.Detected: a quest item found on the ground, which the player could
/// walk away from) is drawn in the theme's green sibling (Glow.Theme), breathing, with short quick comets, so it reads
/// as "a found thing, go grab it" rather than the planned road. The camera is snapshotted once per frame and points
/// are projected with it; every buffer is reused.
/// </summary>
public sealed class RouteRenderer
{
    // The ribbon fades in over the first 160 world units from the feet, as Smooth squared: under 3% for the first
    // 40 units (the feet and the model's base), a quarter at 80 (where the torso projects onto the ground behind the
    // feet with the camera's ~39 degree pitch), full only once clear of the model. 60 units with a plain smoothstep
    // clipped the character (2026-10-11, the user: "the line coming out of us should be more faded / it shouldn't
    // clip our model without heavily fading at least"). Core, halos and comets all take this alpha.
    private const float FadeInWorld = 160f;
    private const float EdgeFull = 450f, EdgeFade = 450f;   // edges: full within, then fade over (world units)
    private const float PulseSpeed = 240f, PulseSpacing = 180f, PulseTail = 70f;
    // Detected (a quest item found on the ground): shorter, denser, quicker comets, a trickle running to the thing
    // found.
    private const float DetSpeed = 320f, DetSpacing = 110f, DetTail = 44f;
    private const double DetBreathSec = 1.6;                  // the detected ribbon breathes: a beacon, not a road
    private const float ApproachWorld = 300f;                 // the last stretch of the path that lights up near the goal
    private const float EdgeQuietNear = 150f, EdgeQuietFade = 250f;   // edges within this of the goal step back (near 1)
    // The comet loop stops at the ribbon's end anyway; this cap only bounds the draw calls (5 per comet) when the
    // draw distance is set long: 40 covers 4400 units of a detected route, 7200 of a planned one.
    private const int MaxPulses = 40;
    private const float RingR = 8f;

    private Vector2[] _pts = new Vector2[512], _buf = new Vector2[512], _buf2 = new Vector2[512], _map = new Vector2[1024];
    private float[] _along = new float[512], _alpha = new float[512], _bufA = new float[512], _bufS = new float[512];
    private string? _labelSrc, _labelText;

    /// <param name="player">The player's position at draw time (fresh when FreshView has it): the path starts at the live feet.</param>
    /// <param name="goal">The point the route leads to (the marker's pad, or the tile goal); null when unknown.</param>
    /// <param name="near">0 far from the goal .. 1 on the doorstep: the approach emphasis (see DrawPath, Comets, DrawEdges).</param>
    /// <param name="core">The frame's path colours, resolved once by the caller (Glow.Theme: the settings' theme, or its
    /// detected sibling), so the ribbon, the goal marker and the large-map line agree.</param>
    public void Draw(RouteFrame f, EdgeMap? edges, WhatsARouteSettings s, Matrix4x4 m, Vector2 half, Vector3 player, Vector3? goal, float near,
        Vector3 core, Vector3 glow, bool largeMapOpen, Vector2 mapCenter, float mapScale, double time)
    {
        if (!f.Ready) return;
        var dl = ImGui.GetBackgroundDrawList();
        if ((dl.Flags & ImDrawListFlags.AntiAliasedLines) == 0) dl.Flags |= ImDrawListFlags.AntiAliasedLines;
        var disp = ImGui.GetIO().DisplaySize;
        var world = !(largeMapOpen && s.World.HideWithLargeMap);
        if (world && s.World.ShowEdges && edges != null) DrawEdges(dl, f, edges, s, m, half, player, goal, near, disp);
        if (world && s.World.ShowPath) DrawPath(dl, f, s, m, half, player, near, core, glow, disp, time);
        if (largeMapOpen && s.Map.ShowOnLargeMap) DrawMap(dl, f, s, player, mapCenter, mapScale);
    }

    // ── Path ────────────────────────────────────────────────────────

    /// <summary>
    /// Approach emphasis: far from the goal the ribbon is calm. As the player closes in (near 0 -> 1) the ribbon
    /// widens a little, its last ~300 units get a brighter, wider glow that runs into the pad, and the comets speed
    /// up and bunch together so they visibly pour into it. The eye is led to the spot to click, not to every wall.
    /// </summary>
    private void DrawPath(ImDrawListPtr dl, RouteFrame f, WhatsARouteSettings s, Matrix4x4 m, Vector2 half, Vector3 player, float near, Vector3 core, Vector3 glow,
        Vector2 disp, double time)
    {
        var w = f.Path;
        if (w.Count < 2) return;
        // Start at the player's live position: the closest point on the first few segments.
        var seg = 0; var best = float.MaxValue; var start = w[0];
        var p2 = new Vector2(player.X, player.Y);
        for (var i = 0; i < Math.Min(w.Count - 1, 12); i++)
        {
            var c = Closest(w[i], w[i + 1], player);
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
        {
            var fadeIn = Glow.Smooth(_along[i] / FadeInWorld);
            _alpha[i] = fadeIn * fadeIn * (1f - Glow.Smooth((_along[i] - (far - fadeOut)) / fadeOut));
        }

        var pw = s.World.PathWidth.Value;
        // Softer than before: a 30 px far halo at 4.5% under the 16 px outer one, the ink rim and the mid halo lighter,
        // the core thinner and not fully opaque, so the profile falls off in four steps instead of a hard bright core
        // with a dark edge (2026-10-11, the user: "the glow needs to be softer and round").
        var st = new Glow.Style(16f, 0.10f, 7f, 0.20f, 0.22f, 2.2f, 0.80f, glow, core, core, 30f, 0.045f).Scaled(pw * (1f + 0.3f * near));
        // The breath and the nearness below go in after quantisation (post): as mul they would step through the 12
        // alpha levels and pop whole chunks instead of ramping.
        var breath = f.Detected ? 0.84f + 0.16f * MathF.Sin((float)(time * Math.PI * 2 / DetBreathSec)) : 1f;
        Glow.StrokeChunks(dl, _pts, _alpha, n, st, 1f, disp, ref _buf, breath);
        if (near > 0.02f && acc < far)
        {
            // The last stretch into the goal: a wider warm glow and a hot core, growing with nearness. It ramps in
            // over its first half and carries the ribbon's own alpha (the fade at the feet when the goal is close).
            var hint = 0;
            var s0 = acc - ApproachWorld;
            var tail = Sub(n, s0, acc, ref hint);
            if (tail >= 2)
            {
                for (var i = 0; i < tail; i++) _bufA[i] *= Glow.Smooth((_bufS[i] - s0) / (ApproachWorld * 0.5f));
                var hot = Vector3.Lerp(core, Vector3.One, 0.5f);
                var ts = new Glow.Style(22f * pw, 0.22f, 0f, 0f, 0f, 3.2f * pw, 0.55f, glow, hot, hot);
                Glow.StrokeChunks(dl, _buf, _bufA, tail, ts, 1f, disp, ref _buf2, near);
            }
        }
        if (s.World.ShowComets) Comets(dl, n, acc, time, glow, disp, near, f.Detected);

        if (f.Goal is { } g && acc < far && !f.HasGoalBox)   // an entity goal gets the marker instead (Markers.DrawEntity)
        {
            var c = Glow.Project(m, half, g);
            if (Glow.OnScreen(c, disp, 40f)) { EndMarker(dl, c, time, glow, core); Label(dl, f.Label, c, disp, glow); }
        }
    }

    /// <summary>
    /// Each comet is a round head (a soft halo disc and a bright dot: round whichever way the path bends) with a tail
    /// that tapers behind it in three pieces, thinner and fainter toward the back, so it never ends square. The
    /// planned route gets long calm comets; a detected objective short quick ones, twice as dense. Five draw calls
    /// per comet (four before: the head's glow stroke became a disc).
    /// </summary>
    private void Comets(ImDrawListPtr dl, int count, float end, double time, Vector3 glow, Vector2 disp, float near, bool detected)
    {
        var tailLen = detected ? DetTail : PulseTail;
        if (end < tailLen) return;
        var white = new Vector3(1f, 0.98f, 0.9f);
        // Near the goal the comets run faster and closer together: a stream pouring into the pad.
        var speed = (detected ? DetSpeed : PulseSpeed) * (1f + 1.1f * near);
        var spacing = (detected ? DetSpacing : PulseSpacing) * (1f - 0.45f * near);
        var phase = (float)(time * speed % spacing);
        var hint = 0;
        for (var i = 0; i < MaxPulses; i++)
        {
            var sPos = phase + i * spacing;
            if (sPos - tailLen > end) break;
            var env = AlphaAt(MathF.Min(sPos, end), count);
            if (sPos > end) env *= MathF.Max(0f, 1f - (sPos - end) / tailLen);
            env *= 1f + near * Glow.Smooth(1f - (end - sPos) / ApproachWorld);   // brighter as they arrive
            if (env < 0.03f) continue;
            var n = Sub(count, sPos - tailLen, sPos - tailLen * 0.6f, ref hint);
            if (n >= 2 && Glow.Visible(_buf[0], _buf[n - 1], disp)) Glow.Line(dl, _buf, n, white, 0.08f * env, 2.2f);
            n = Sub(count, sPos - tailLen * 0.6f, sPos - tailLen * 0.28f, ref hint);
            if (n >= 2 && Glow.Visible(_buf[0], _buf[n - 1], disp)) Glow.Line(dl, _buf, n, white, 0.16f * env, 4f);
            n = Sub(count, sPos - tailLen * 0.28f, sPos, ref hint);
            if (n < 2 || !Glow.Visible(_buf[0], _buf[n - 1], disp)) continue;
            Glow.Line(dl, _buf, n, white, 0.26f * env, 5.5f);
            var head = _buf[n - 1];
            dl.AddCircleFilled(head, 7f, Glow.Col(glow, 0.20f * env), 16);
            dl.AddCircleFilled(head, 2.8f, Glow.Col(white, 0.92f * env), 12);
        }
    }

    /// <summary>The ribbon's alpha at the first point at or past <paramref name="s"/> along it (_along is ascending: a binary search).</summary>
    private float AlphaAt(float s, int count)
    {
        int lo = 1, hi = count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_along[mid] >= s) hi = mid; else lo = mid + 1;
        }
        return _alpha[lo];
    }

    /// <summary>
    /// The piece of the projected path between two distances along it, into _buf, with each point's ribbon alpha in
    /// _bufA and its distance along the path in _bufS.
    /// </summary>
    private int Sub(int count, float s0, float s1, ref int hint)
    {
        if (s0 < 0) s0 = 0;
        if (s1 > _along[count - 1]) s1 = _along[count - 1];
        if (s1 - s0 < 0.5f) return 0;
        Glow.Ensure(ref _buf, count + 2);
        Glow.Ensure(ref _bufA, count + 2);
        Glow.Ensure(ref _bufS, count + 2);
        var i = Math.Clamp(hint, 0, count - 2);
        while (i > 0 && _along[i] > s0) i--;
        while (i < count - 2 && _along[i + 1] <= s0) i++;
        hint = i;
        var n = 0;
        _buf[n] = LerpAt(i, s0, out _bufA[n]); _bufS[n++] = s0;
        var j = i + 1;
        while (j < count - 1 && _along[j] < s1) { _buf[n] = _pts[j]; _bufA[n] = _alpha[j]; _bufS[n++] = _along[j++]; }
        _buf[n] = LerpAt(j - 1, s1, out _bufA[n]); _bufS[n++] = s1;
        return n;
    }

    private Vector2 LerpAt(int i, float s, out float alpha)
    {
        var d = _along[i + 1] - _along[i];
        var t = d > 1e-4f ? Math.Clamp((s - _along[i]) / d, 0f, 1f) : 0f;
        alpha = _alpha[i] + (_alpha[i + 1] - _alpha[i]) * t;
        return Vector2.Lerp(_pts[i], _pts[i + 1], t);
    }

    private static void EndMarker(ImDrawListPtr dl, Vector2 c, double time, Vector3 glow, Vector3 core)
    {
        var breath = 0.5f + 0.5f * MathF.Sin((float)(time * Math.PI * 2 / 2.4));
        var r = RingR + 0.8f * breath;
        dl.AddCircleFilled(c, r + 7f, Glow.Col(glow, 0.12f), 24);   // the ribbon's square end dissolves into this disc
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

    /// <summary>
    /// Terrain edges fade with distance from the player as before; near the goal they also step back around it
    /// (within ~400 units of the goal, by up to 80% on the doorstep), so the cyan walls stop competing with the pad.
    /// </summary>
    private void DrawEdges(ImDrawListPtr dl, RouteFrame f, EdgeMap edges, WhatsARouteSettings s, Matrix4x4 m, Vector2 half, Vector3 player, Vector3? goal, float near, Vector2 disp)
    {
        var core = Glow.Rgb(s.World.EdgeCore.Value);
        var glow = Glow.Rgb(s.World.EdgeGlow.Value);
        var st = new Glow.Style(10f, 0.10f, 4.5f, 0.22f, 0.25f, 1.6f, 0.85f, glow, core, Vector3.Lerp(core, Vector3.One, 0.6f));
        var p2 = new Vector2(player.X, player.Y);
        var g2 = goal is { } gp ? new Vector2(gp.X, gp.Y) : default;
        var stepBack = goal != null ? 0.8f * near : 0f;
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
                var w2 = new Vector2(w[i].X, w[i].Y);
                var a = 1f - Glow.Smooth((Vector2.Distance(w2, p2) - EdgeFull) / EdgeFade);
                if (stepBack > 0f) a *= 1f - stepBack * (1f - Glow.Smooth((Vector2.Distance(w2, g2) - EdgeQuietNear) / EdgeQuietFade));
                _alpha[i] = a;
                any |= _alpha[i] > 0.02f;
            }
            if (any) Glow.StrokeChunks(dl, _pts, _alpha, w.Length, st, 1f, disp, ref _buf);
        }
    }

    // ── Large map ───────────────────────────────────────────────────

    private void DrawMap(ImDrawListPtr dl, RouteFrame f, WhatsARouteSettings s, Vector3 player, Vector2 center, float scale)
    {
        var g = f.MapPath;
        if (g.Count < 2 || scale <= 0) return;
        Glow.Ensure(ref _map, g.Count);
        var pg = new Vector2(player.X, player.Y) / AreaGrid.GridToWorld;
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
        if (f.Detected) col = Glow.Hue(col, Glow.DetectedHueShift);   // the same green as the world ribbon
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
