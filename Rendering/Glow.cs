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

    public readonly struct Style
    {
        public readonly float OuterW, OuterA, MidW, MidA, InkA, CoreW, CoreA;
        public readonly Vector3 GlowCol, CoreCol, NearCol;

        public Style(float outerW, float outerA, float midW, float midA, float inkA, float coreW, float coreA, Vector3 glow, Vector3 core, Vector3 near)
        {
            OuterW = outerW; OuterA = outerA; MidW = midW; MidA = midA; InkA = inkA; CoreW = coreW; CoreA = coreA;
            GlowCol = glow; CoreCol = core; NearCol = near;
        }

        public Style Scaled(float width) => new(OuterW * width, OuterA, MidW * width, MidA, InkA, CoreW * width, CoreA, GlowCol, CoreCol, NearCol);
    }

    public static void StrokeChunks(ImDrawListPtr dl, Vector2[] pts, float[] alpha, int count, in Style st, float mul, Vector2 disp, ref Vector2[] buf)
    {
        if (count < 2 || mul < 0.012f) return;
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
            Layers(dl, buf, n, lvl / (float)Levels, st);
            i = j;
        }
    }

    public static void Layers(ImDrawListPtr dl, Vector2[] buf, int n, float a, in Style st)
    {
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
