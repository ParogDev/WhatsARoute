using System;
using System.Numerics;
using ImGuiNET;

namespace WhatsARoute.Rendering;

/// <summary>
/// "Click this one" markers, in the route's gold: the same language in the world and on the Desert Map.
/// - A footprint stroke around the thing (an ellipse on the ground around an entity's Bounds; a ring around a map
///   stop), breathing slowly, with the glow layers of the path so it reads on busy art without covering anything.
/// - Four corner brackets just outside it (the HUD highlight's brackets, here around the object's bounding box; in
///   the world they rise from the base corners to part of the model's height), snapping in from outside when the
///   marker appears.
/// - One sonar ring expanding from the footprint every 2.2 s: the eye finds it even on the painted desert.
/// - A chevron bobbing above the top with the destination name in a pill: what it is, and that it is above THIS one.
/// The interior stays clear (the wheel icon, the table, the door are never covered). No allocations per frame: the
/// point buffers are reused and the label is the caller's string.
/// </summary>
public sealed class Markers
{
    private const double SnapSec = 0.35, SonarSec = 2.2, BreathSec = 2.4, BobSec = 1.3;
    private const int RingPts = 36;
    private static readonly Vector3 Ink = Glow.Ink;
    private static readonly Vector3 PillFill = new(0.04f, 0.06f, 0.07f);
    private static readonly Vector3 PillText = new(0.97f, 0.94f, 0.86f);

    private readonly Vector2[] _ring = new Vector2[RingPts + 1];
    private readonly Vector2[] _seg = new Vector2[3];

    // ── World: an entity's bounding box ──────────────────────────────

    /// <summary>
    /// "Walk here and click this": a map pin on the interactable. A landing pad on the ground at its ground point
    /// <paramref name="pos"/> (a soft gold disc the path and its comets run into, a crisp rim, a sonar ring every
    /// 2.2 s), one hairline rising from the pad's centre to just above the object's top (<paramref name="height"/>
    /// above the ground, up is -z), a chevron pointing down at it, and the name. Nothing of the object is covered.
    /// <paramref name="near"/> 0..1 (far -> at the door) brings the pad up: a dim ring from far away, a bright
    /// receiving disc on the doorstep. Projected with the frame's fresh camera; nothing is drawn off screen.
    /// </summary>
    public void DrawEntity(ImDrawListPtr dl, in Matrix4x4 m, Vector2 halfScreen, Vector2 disp, Vector3 pos, float padR, float height,
        string label, Vector3 core, Vector3 glow, double time, double appearedAt, float near)
    {
        var age = time - appearedAt;
        var snap = Glow.Smooth((float)(age / SnapSec));
        var breath = 0.5f + 0.5f * MathF.Sin((float)(time * Math.PI * 2 / BreathSec));
        var r = MathF.Max(padR, 28f);
        var lift = 0.35f + 0.65f * near;   // how present the pad is

        // Landing pad: a soft disc (two fills, the inner brighter), its rim with the path's glow layers, and a dot.
        if (!Ellipse(m, halfScreen, disp, pos, r, r)) return;
        dl.AddConvexPolyFilled(ref _ring[0], RingPts, Glow.Col(glow, (0.06f + 0.10f * near + 0.05f * breath * near) * snap));
        var st = new Glow.Style(9f, 0.10f + 0.10f * near, 4f, 0.20f + 0.12f * near, 0.30f, 1.7f, (0.6f + 0.25f * near) + 0.15f * breath, glow, core, core);
        Glow.Layers(dl, _ring, RingPts + 1, snap * lift, st);
        if (Ellipse(m, halfScreen, disp, pos, r * 0.45f, r * 0.45f))
            dl.AddConvexPolyFilled(ref _ring[0], RingPts, Glow.Col(core, (0.08f + 0.22f * near) * snap));
        var centre = Glow.Project(m, halfScreen, pos);
        dl.AddCircleFilled(centre, 2.6f, Glow.Col(core, 0.9f * snap), 12);

        // Sonar: one ring growing out of the pad and fading.
        var k = (float)(age % SonarSec / SonarSec);
        if (age > 0.3 && Ellipse(m, halfScreen, disp, pos, r * (1f + 0.6f * Glow.Smooth(k)), r * (1f + 0.6f * Glow.Smooth(k))))
            Glow.Line(dl, _ring, RingPts + 1, glow, (0.3f + 0.3f * near) * (1f - k) * snap, 1.6f);

        // The pin: one hairline from the pad to just above the object's top, then the chevron and the name.
        var top = MathF.Max(height, 60f) + 16f;
        var apex = Glow.Project(m, halfScreen, new Vector3(pos.X, pos.Y, pos.Z - top));
        if (!Glow.OnScreen(apex, disp, 40f)) return;
        var pinTop = new Vector2(apex.X, apex.Y + 4f);
        dl.AddLine(centre, pinTop, Glow.Col(Ink, 0.45f * snap), 3f);
        dl.AddLine(centre, pinTop, Glow.Col(core, 0.55f * snap), 1f);
        Chevron(dl, apex, time, snap, core, glow);
        Pill(dl, label, new Vector2(apex.X, apex.Y - 30f), disp, glow, snap);
    }

    private bool Ellipse(in Matrix4x4 m, Vector2 halfScreen, Vector2 disp, Vector3 c, float rx, float ry)
    {
        var any = false;
        for (var i = 0; i <= RingPts; i++)
        {
            var a = i * MathF.Tau / RingPts;
            var p = Glow.Project(m, halfScreen, new Vector3(c.X + rx * MathF.Cos(a), c.Y + ry * MathF.Sin(a), c.Z));
            _ring[i] = p;
            any |= Glow.OnScreen(p, disp, Glow.Pad);
        }
        return any;
    }

    // ── Screen: a Desert Map stop ────────────────────────────────────

    /// <summary>The marker around a map node at <paramref name="center"/> of <paramref name="size"/> px (the wheel stays uncovered).</summary>
    public void DrawStop(ImDrawListPtr dl, Vector2 center, float size, Vector2 disp, string label, Vector3 core, Vector3 glow, double time, double appearedAt)
    {
        var age = time - appearedAt;
        var snap = Glow.Smooth((float)(age / SnapSec));
        var breath = 0.5f + 0.5f * MathF.Sin((float)(time * Math.PI * 2 / BreathSec));
        var r0 = size * 0.5f + 7f;
        center = new Vector2(MathF.Round(center.X), MathF.Round(center.Y));

        // Ring: halo, ink rim, core.
        dl.AddCircle(center, r0, Glow.Col(glow, 0.14f * snap), 48, 9f);
        dl.AddCircle(center, r0, Glow.Col(Ink, 0.5f * snap), 48, 4.5f);
        dl.AddCircle(center, r0, Glow.Col(core, (0.8f + 0.2f * breath) * snap), 48, 2.2f);

        // Sonar.
        var k = (float)(age % SonarSec / SonarSec);
        if (age > 0.3) dl.AddCircle(center, r0 * (1f + 0.5f * Glow.Smooth(k)), Glow.Col(glow, 0.55f * (1f - k) * snap), 48, 1.6f);

        // Brackets on the bounding square, sliding in.
        var slide = (1f - snap) * 12f;
        var d = r0 + 6f + slide;
        var len = MathF.Max(12f, r0 * 0.35f);
        for (var i = 0; i < 4; i++)
        {
            var sx = i is 0 or 3 ? -1f : 1f;
            var sy = i < 2 ? -1f : 1f;
            var corner = new Vector2(center.X + sx * d, center.Y + sy * d);
            _seg[0] = new Vector2(corner.X - sx * len, corner.Y);
            _seg[1] = corner;
            _seg[2] = new Vector2(corner.X, corner.Y - sy * len);
            dl.AddPolyline(ref _seg[0], 3, Glow.Col(Ink, 0.55f * snap), ImDrawFlags.None, 5f);
            dl.AddPolyline(ref _seg[0], 3, Glow.Col(core, (0.8f + 0.2f * breath) * snap), ImDrawFlags.None, 2.2f);
        }

        var apex = new Vector2(center.X, center.Y - d - 4f);
        Chevron(dl, apex, time, snap, core, glow);
        Pill(dl, label, new Vector2(apex.X, apex.Y - 30f), disp, glow, snap);
    }

    /// <summary>A quiet ring on another unlocked stop: "a stop, not this one".</summary>
    public static void DrawGhostStop(ImDrawListPtr dl, Vector2 center, float size, Vector3 glow)
    {
        var r = size * 0.5f + 3f;
        dl.AddCircle(center, r, Glow.Col(Ink, 0.35f), 40, 2.6f);
        dl.AddCircle(center, r, Glow.Col(glow, 0.3f), 40, 1.1f);
    }

    // ── Shared parts ─────────────────────────────────────────────────

    /// <summary>A chevron pointing down at <paramref name="apex"/>, bobbing a few px.</summary>
    private void Chevron(ImDrawListPtr dl, Vector2 apex, double time, float snap, Vector3 core, Vector3 glow)
    {
        var bob = 3.5f * (0.5f + 0.5f * MathF.Sin((float)(time * Math.PI * 2 / BobSec)));
        var tip = new Vector2(MathF.Round(apex.X), MathF.Round(apex.Y - 6f - bob + (1f - snap) * 10f));
        _seg[0] = new Vector2(tip.X - 9f, tip.Y - 9f);
        _seg[1] = tip;
        _seg[2] = new Vector2(tip.X + 9f, tip.Y - 9f);
        dl.AddPolyline(ref _seg[0], 3, Glow.Col(glow, 0.25f * snap), ImDrawFlags.None, 8f);
        dl.AddPolyline(ref _seg[0], 3, Glow.Col(Ink, 0.6f * snap), ImDrawFlags.None, 5f);
        dl.AddPolyline(ref _seg[0], 3, Glow.Col(core, 0.95f * snap), ImDrawFlags.None, 2.4f);
    }

    /// <summary>The name pill centred on <paramref name="at"/> (its bottom centre), kept on screen.</summary>
    private static void Pill(ImDrawListPtr dl, string label, Vector2 at, Vector2 disp, Vector3 glow, float snap)
    {
        if (string.IsNullOrEmpty(label)) return;
        var size = ImGui.CalcTextSize(label) + new Vector2(14f, 6f);
        var pos = new Vector2(at.X - size.X * 0.5f, at.Y - size.Y);
        pos = new Vector2(MathF.Round(Math.Clamp(pos.X, 2f, MathF.Max(2f, disp.X - size.X - 2f))),
            MathF.Round(Math.Clamp(pos.Y, 2f, MathF.Max(2f, disp.Y - size.Y - 2f))));
        dl.AddRectFilled(pos, pos + size, Glow.Col(PillFill, 0.86f * snap), size.Y * 0.5f);
        dl.AddRect(pos, pos + size, Glow.Col(glow, 0.55f * snap), size.Y * 0.5f, ImDrawFlags.None, 1.2f);
        dl.AddText(new Vector2(pos.X + 7f, pos.Y + 3f), Glow.Col(PillText, 0.96f * snap), label);
    }
}
