using System;
using System.Numerics;
using ImGuiNET;

namespace WhatsARoute.Rendering;

/// <summary>
/// Glowing polylines on an ImGui draw list (the look of the AI Bridge render lab, by Fable): a wide low-alpha halo,
/// a dark "ink" rim that calms the busy ground under the line, a mid halo and a crisp core. A polyline whose alpha
/// varies per point is drawn in chunks of equal quantised alpha, one AddPolyline per layer, so the cost stays linear
/// in the point count. No allocations: the caller owns the scratch buffer.
/// </summary>
public static class Glow
{
    public const int Levels = 12;
    public const float Pad = 20f;   // off-screen margin covering the widest halo

    public static readonly Vector3 Ink = new(0.02f, 0.04f, 0.05f);

    /// <summary>
    /// Five layers, widest first: a far halo (optional, the softest; 2026-10-11 the user: "the glow needs to be softer
    /// and round"), the outer halo, the ink rim, the mid halo and the core. Layers with zero alpha cost nothing.
    /// </summary>
    public readonly struct Style
    {
        public readonly float OuterW, OuterA, MidW, MidA, InkA, CoreW, CoreA, FarW, FarA;
        public readonly Vector3 GlowCol, CoreCol, NearCol;

        public Style(float outerW, float outerA, float midW, float midA, float inkA, float coreW, float coreA, Vector3 glow, Vector3 core, Vector3 near,
            float farW = 0f, float farA = 0f)
        {
            OuterW = outerW; OuterA = outerA; MidW = midW; MidA = midA; InkA = inkA; CoreW = coreW; CoreA = coreA; FarW = farW; FarA = farA;
            GlowCol = glow; CoreCol = core; NearCol = near;
        }

        public Style Scaled(float width) =>
            new(OuterW * width, OuterA, MidW * width, MidA, InkA, CoreW * width, CoreA, GlowCol, CoreCol, NearCol, FarW * width, FarA);
    }

    /// <param name="mul">Scales the per-point alpha before it is quantised into chunks (a static dimming).</param>
    /// <param name="post">Scales each chunk's alpha after quantisation: for anything continuous (a breath, a nearness
    /// ramp), which as <paramref name="mul"/> would step through the 12 levels and pop whole chunks.</param>
    public static void StrokeChunks(ImDrawListPtr dl, Vector2[] pts, float[] alpha, int count, in Style st, float mul, Vector2 disp, ref Vector2[] buf, float post = 1f)
    {
        if (count < 2 || mul * post < 0.012f) return;
        Ensure(ref buf, count);
        var i = 0;
        while (i < count - 1)
        {
            var lvl = Level(alpha[i], alpha[i + 1], mul);
            if (lvl <= 0 || !Visible(pts[i], pts[i + 1], disp)) { i++; continue; }
            var n = 0;
            buf[n++] = pts[i];
            var j = i;
            do { buf[n++] = pts[j + 1]; j++; }
            while (j < count - 1 && Level(alpha[j], alpha[j + 1], mul) == lvl && Visible(pts[j], pts[j + 1], disp));
            Layers(dl, buf, n, lvl / (float)Levels * post, st);
            i = j;
        }
    }

    public static void Layers(ImDrawListPtr dl, Vector2[] buf, int n, float a, in Style st)
    {
        Line(dl, buf, n, st.GlowCol, a * st.FarA, st.FarW);
        Line(dl, buf, n, st.GlowCol, a * st.OuterA, st.OuterW);
        Line(dl, buf, n, Ink, a * st.InkA, st.MidW * 0.8f);
        Line(dl, buf, n, st.GlowCol, a * st.MidA, st.MidW);
        Line(dl, buf, n, Vector3.Lerp(st.CoreCol, st.NearCol, a), a * st.CoreA, st.CoreW);
    }

    public static void Line(ImDrawListPtr dl, Vector2[] buf, int n, Vector3 col, float alpha, float width)
    {
        if (alpha < 0.012f || n < 2) return;
        dl.AddPolyline(ref buf[0], n, Col(col, alpha), ImDrawFlags.None, width);
    }

    public static int Level(float a0, float a1, float mul) => Math.Clamp((int)MathF.Round((a0 + a1) * 0.5f * mul * Levels), 0, Levels);

    public static bool Visible(Vector2 a, Vector2 b, Vector2 disp)
    {
        if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y)) return false;
        if (a.X < -Pad && b.X < -Pad) return false;
        if (a.Y < -Pad && b.Y < -Pad) return false;
        if (a.X > disp.X + Pad && b.X > disp.X + Pad) return false;
        if (a.Y > disp.Y + Pad && b.Y > disp.Y + Pad) return false;
        return true;
    }

    public static bool OnScreen(Vector2 p, Vector2 disp, float pad) => p.X > -pad && p.Y > -pad && p.X < disp.X + pad && p.Y < disp.Y + pad;

    public static float Smooth(float t) => t <= 0f ? 0f : t >= 1f ? 1f : t * t * (3f - 2f * t);

    /// <summary>ABGR packing, bypassing the style alpha (a faded panel must not dim the world overlay).</summary>
    public static uint Col(Vector3 c, float a)
    {
        if (!(a > 0f)) return 0u;
        if (a > 1f) a = 1f;
        return ((uint)(a * 255f + 0.5f) << 24) | ((uint)(c.Z * 255f + 0.5f) << 16) | ((uint)(c.Y * 255f + 0.5f) << 8) | (uint)(c.X * 255f + 0.5f);
    }

    public static Vector3 Rgb(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>
    /// The hue shift of the "detected" look: the route's own colours turned +85 degrees, so the default gold
    /// (glow 34, core 45 degrees) lands on a leaf green (119 / 130), the colour the game gives quest items on the
    /// ground, and a user's custom theme stays a sibling of itself. Far from the cyan edges (195) and the gold path.
    /// </summary>
    public const float DetectedHueShift = 85f;

    /// <summary>The route's colours for this frame: the settings' theme, or its detected sibling (Hue) when something was found.</summary>
    public static void Theme(WorldSettings w, bool detected, out Vector3 core, out Vector3 glow)
    {
        core = Rgb(w.PathCore.Value);
        glow = Rgb(w.PathGlow.Value);
        if (!detected) return;
        core = Hue(core, DetectedHueShift);
        glow = Hue(glow, DetectedHueShift);
    }

    /// <summary>The colour with its hue turned by <paramref name="degrees"/> (saturation and value kept). No allocation.</summary>
    public static Vector3 Hue(Vector3 c, float degrees)
    {
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        var d = max - min;
        if (d < 1e-4f || max < 1e-4f) return c;   // grey: no hue to turn
        float h;
        if (max == c.X) h = (c.Y - c.Z) / d % 6f;
        else if (max == c.Y) h = (c.Z - c.X) / d + 2f;
        else h = (c.X - c.Y) / d + 4f;
        h = (h * 60f + degrees) % 360f;
        if (h < 0f) h += 360f;
        var x = d * (1f - MathF.Abs(h / 60f % 2f - 1f));   // chroma d and the offset m = max - d: saturation and value stay
        var m = max - d;
        return (int)(h / 60f) switch
        {
            0 => new Vector3(max, x + m, m),
            1 => new Vector3(x + m, max, m),
            2 => new Vector3(m, max, x + m),
            3 => new Vector3(m, x + m, max),
            4 => new Vector3(x + m, m, max),
            _ => new Vector3(max, m, x + m),
        };
    }

    public static void Ensure<T>(ref T[] arr, int n)
    {
        if (arr.Length < n) Array.Resize(ref arr, Math.Max(n, arr.Length * 2));
    }

    /// <summary>World to screen with a camera snapshot taken once per frame (no memory reads per point).</summary>
    public static Vector2 Project(in Matrix4x4 m, Vector2 half, Vector3 v)
    {
        var c = Vector4.Transform(new Vector4(v, 1), m);
        if (c.W <= 1e-4f) return new Vector2(float.NaN, float.NaN);
        c /= c.W;
        return new Vector2((c.X + 1f) * half.X, (1f - c.Y) * half.Y);
    }
}
