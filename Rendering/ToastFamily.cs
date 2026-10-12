using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace WhatsARoute.Rendering;

/// <summary>
/// The AI Bridge's toast family, ported by hand for the surfaces where the player talks to agents (research/f8-agent-ux.md
/// sections 1 and 2): the compose panel, its receipt, the heads-up panel and the plugin's own one-line toasts. Never
/// linked: the Route plugin cannot reference the bridge's code, so the look is re-drawn here from the spec's palette.
/// Fixed colours (the same on any HUD theme), square corners, a 3 px stripe flush on the left edge, a hairline, tracked
/// faux-bold caps for titles (one AddText per glyph from a cached ASCII table: the HUD font has no tracked face), chips as
/// pills (f + 8 tall, corners h / 2), primary / secondary pills, a key cap, and a small hover label. ASCII only; nothing
/// here allocates per frame except where a clip or a wrap is cached by the caller.
/// </summary>
public static class ToastFamily
{
    public static readonly Vector3 Ink = Hex(0x15171C), Line = Hex(0x2B2E36), Text = Hex(0xF2F1EE), Muted = Hex(0x9C9B95), InkOnAccent = Hex(0x0D140F);
    public static readonly Vector3 Accent = Hex(0x00CED1), Ok = Hex(0x4ADE80), Warn = Hex(0xFBBF24), Bad = Hex(0xF87171), Neutral = Hex(0x85837D);
    public const float Stripe = 3f, PadX = 12f, TitleTrack = 0.8f, MsgTrack = 0.6f, ChipPadX = 10f, ChipGap = 6f, ChipGlyphW = 14f;

    private static readonly string[] Glyphs = Enumerable.Range(0, 128).Select(c => ((char)c).ToString()).ToArray();

    public static Vector3 Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

    public static uint Col(Vector3 c, float a) => Glow.Col(c, a);

    // ── Text ─────────────────────────────────────────────────────────

    public static float Advance(ImFontPtr font, float size, char ch) => font.GetCharAdvance(ch < 128 ? ch : '?') * (size / font.FontSize);

    /// <summary>Text one glyph at a time with <paramref name="track"/> px after each (fake letter-spacing).</summary>
    public static void Tracked(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint col, string text, float track)
    {
        var x = pos.X;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i] < 128 ? text[i] : '?';
            dl.AddText(font, size, new Vector2(x, pos.Y), col, Glyphs[ch]);
            x += Advance(font, size, ch) + track;
        }
    }

    /// <summary>Tracked and faux bold (drawn twice, 0.6 px apart): the family's title.</summary>
    public static void TitleCaps(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint col, string text, float track = TitleTrack)
    {
        Tracked(dl, font, size, pos, col, text, track);
        Tracked(dl, font, size, pos + new Vector2(0.6f, 0f), col, text, track);
    }

    public static float TrackedWidth(ImFontPtr font, float size, string text, float track)
    {
        var w = 0f;
        for (var i = 0; i < text.Length; i++) w += Advance(font, size, text[i]) + track;
        return text.Length > 0 ? w - track : 0f;
    }

    /// <summary>Tracked text clipped to <paramref name="maxW"/> with a ".." tail; the same instance when it fits (cache the result).</summary>
    public static string ClipTracked(string s, ImFontPtr font, float size, float track, float maxW)
    {
        if (TrackedWidth(font, size, s, track) <= maxW) return s;
        var tail = TrackedWidth(font, size, "..", track) + track;
        var w = 0f;
        var n = 0;
        for (; n < s.Length; n++)
        {
            var next = w + Advance(font, size, s[n]) + track;
            if (next + tail > maxW) break;
            w = next;
        }
        return n <= 0 ? ".." : s[..n].TrimEnd() + "..";
    }

    /// <summary>Plain text at <paramref name="size"/> clipped to <paramref name="maxW"/> with a ".." tail; the same instance when it fits.</summary>
    public static string Clip(ImFontPtr font, float size, string s, float maxW)
    {
        if (font.CalcTextSizeA(size, float.MaxValue, 0f, s).X <= maxW) return s;
        var tail = font.CalcTextSizeA(size, float.MaxValue, 0f, "..").X;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (font.CalcTextSizeA(size, float.MaxValue, 0f, s[..mid]).X + tail <= maxW) lo = mid; else hi = mid - 1;
        }
        return lo <= 0 ? ".." : s[..lo].TrimEnd() + "..";
    }

    public static float Width(ImFontPtr font, float size, string s) => font.CalcTextSizeA(size, float.MaxValue, 0f, s).X;

    // ── Panels ───────────────────────────────────────────────────────

    /// <summary>The family's shell: ink at 94%, a hairline, the tone's stripe flush on the left edge, square corners.</summary>
    public static void Panel(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector3 tone, float alpha, float stripe = Stripe)
    {
        dl.AddRectFilled(min, max, Col(Ink, 0.94f * alpha), 0f);
        dl.AddRect(min, max, Col(Line, alpha), 0f);
        dl.AddRectFilled(min, new Vector2(min.X + stripe, max.Y), Col(tone, alpha), 0f);
    }

    /// <summary>A 22 x 16 key cap: hairline, ink fill, the key in muted faux bold at 0.75 f.</summary>
    public static void KeyCap(ImDrawListPtr dl, ImFontPtr font, float f, Vector2 min, string key, float alpha)
    {
        var max = min + new Vector2(22f, 16f);
        dl.AddRectFilled(min, max, Col(Ink, alpha), 2f);
        dl.AddRect(min, max, Col(Line, alpha), 2f);
        var size = f * 0.75f;
        var tw = Width(font, size, key);
        var p = new Vector2(min.X + (22f - tw) * 0.5f, min.Y + (16f - size) * 0.5f);
        dl.AddText(font, size, p, Col(Muted, alpha), key);
        dl.AddText(font, size, p + new Vector2(0.6f, 0f), Col(Muted, alpha), key);
    }

    /// <summary>The 16 px close glyph (two strokes), centred on <paramref name="c"/>.</summary>
    public static void CloseGlyph(ImDrawListPtr dl, Vector2 c, float r, uint col)
    {
        dl.AddLine(new Vector2(c.X - r, c.Y - r), new Vector2(c.X + r, c.Y + r), col, 1.6f);
        dl.AddLine(new Vector2(c.X + r, c.Y - r), new Vector2(c.X - r, c.Y + r), col, 1.6f);
    }

    /// <summary>A check mark in a 10 px box centred on <paramref name="c"/>.</summary>
    public static void Check(ImDrawListPtr dl, Vector2 c, uint col, float t = 1.8f)
    {
        dl.AddLine(new Vector2(c.X - 4f, c.Y + 0.2f), new Vector2(c.X - 1.2f, c.Y + 3f), col, t);
        dl.AddLine(new Vector2(c.X - 1.2f, c.Y + 3f), new Vector2(c.X + 4.2f, c.Y - 3.2f), col, t);
    }

    /// <summary>A "!" disc, 10 px: the preselected suggestion's mark.</summary>
    public static void Bang(ImDrawListPtr dl, Vector2 c, uint disc, uint ink)
    {
        dl.AddCircleFilled(c, 5f, disc, 12);
        dl.AddLine(new Vector2(c.X, c.Y - 2.8f), new Vector2(c.X, c.Y + 0.6f), ink, 1.6f);
        dl.AddCircleFilled(new Vector2(c.X, c.Y + 2.6f), 0.9f, ink, 6);
    }

    // ── Chips and pills ──────────────────────────────────────────────

    public enum ChipState { Off, On, Warn }

    /// <summary>
    /// A chip's width: the label at 0.9 f, 10 px padding each side, plus a 20 px slot the on-state glyph (10 px + 4 gap) and
    /// the digit badge share. Constant whether on or off, so a toggle never reflows the row: the label is centred when off and
    /// the glyph + label group is centred when on (the text moves 7 px, nothing else does).
    /// </summary>
    public static float ChipWidth(ImFontPtr font, float f, string label) => Width(font, f * 0.9f, label) + ChipPadX * 2 + ChipSlotW;
    private const float ChipSlotW = 20f, ChipGlyphGap = 4f;

    /// <summary>
    /// Where a line of text at <paramref name="size"/> sits so its x-height reads centred on <paramref name="cy"/>: the font
    /// box's centre plus a small nudge (the box has more room under the baseline than caps and ascenders use above it).
    /// </summary>
    public static float TextTop(float cy, float size) => MathF.Round(cy - size * 0.5f + size * 0.06f);

    /// <summary>
    /// A chip (2.2): off = Line 55% on a Line hairline, text at 85%; hover = accent 18% on an accent 80% hairline; on =
    /// accent 85% with ink faux-bold text and a check; warn (a preselected suggestion) = warn 80% with a "!" disc. The label
    /// (or the glyph + label group) is optically centred in every state. The digit badge (which key toggles it, shown only
    /// while the overlay has keyboard focus and no field is active) is a small muted disc inside the chip's right padding,
    /// vertically centred, never on the outline. Visuals only: the caller owns the InvisibleButton and the click.
    /// </summary>
    public static void Chip(ImDrawListPtr dl, ImFontPtr font, float f, Vector2 min, float w, float h, string label, ChipState state, bool hover, string? digit, float alpha)
    {
        var max = min + new Vector2(w, h);
        var r = h * 0.5f;
        var size = f * 0.9f;
        var cy = min.Y + r;
        var ty = TextTop(cy, size);
        var tw = Width(font, size, label);
        var on = state != ChipState.Off;
        var tone = state == ChipState.Warn ? Warn : Accent;
        if (on)
        {
            dl.AddRectFilled(min, max, Col(tone, (state == ChipState.Warn ? 0.80f : 0.85f) * (hover ? 1.08f : 1f) * alpha), r);
            var ink = Col(InkOnAccent, alpha);
            const float glyphW = 10f;
            var gx = MathF.Round(min.X + (w - (glyphW + ChipGlyphGap + tw)) * 0.5f);
            var gc = new Vector2(gx + glyphW * 0.5f, cy);
            if (state == ChipState.Warn) Bang(dl, gc, Col(InkOnAccent, 0.9f * alpha), Col(tone, alpha));
            else Check(dl, gc, ink);
            var tp = new Vector2(gx + glyphW + ChipGlyphGap, ty);
            dl.AddText(font, size, tp, ink, label);
            dl.AddText(font, size, tp + new Vector2(0.6f, 0f), ink, label);
        }
        else
        {
            if (hover)
            {
                dl.AddRectFilled(min, max, Col(Accent, 0.18f * alpha), r);
                dl.AddRect(min, max, Col(Accent, 0.8f * alpha), r, ImDrawFlags.None, 1.2f);
            }
            else
            {
                dl.AddRectFilled(min, max, Col(Line, 0.55f * alpha), r);
                dl.AddRect(min, max, Col(Line, alpha), r);
            }
            dl.AddText(font, size, new Vector2(MathF.Round(min.X + (w - tw) * 0.5f), ty), Col(Text, (hover ? 1f : 0.85f) * alpha), label);
        }
        if (digit != null)
        {
            // Inside the right padding (the slot leaves 13 px free there in either state), on the chip's own fill.
            var bc = new Vector2(max.X - 8.5f, cy);
            var ds = f * 0.6f;
            dl.AddCircleFilled(bc, 5.5f, Col(on ? InkOnAccent : Ink, (on ? 0.35f : 0.9f) * alpha), 14);
            dl.AddCircle(bc, 5.5f, Col(on ? InkOnAccent : Muted, (on ? 0.5f : 0.8f) * alpha), 14, 1f);
            dl.AddText(font, ds, new Vector2(MathF.Round(bc.X - Width(font, ds, digit) * 0.5f), TextTop(cy, ds)), Col(on ? InkOnAccent : Text, (on ? 0.9f : 0.95f) * alpha), digit);
        }
    }

    public enum PillKind { Primary, Secondary }

    /// <summary>A pill's width: the label at 0.85 f, 10 px each side, plus the glyph slot for a primary pill.</summary>
    public static float PillWidth(ImFontPtr font, float f, string label, PillKind kind, string? hint = null) =>
        Width(font, f * 0.85f, label) + 20f + (kind == PillKind.Primary ? 12f : 0f) + (hint != null ? Width(font, f * 0.75f, hint) + 10f : 0f);

    /// <summary>
    /// A pill (the ask card's): primary = accent fill, a check, ink faux-bold text; secondary = Line hairline, text at 85%,
    /// an accent tint on hover (<paramref name="hoverTone"/> overrides the tint's colour, e.g. Ok for "Works now"). An optional
    /// <paramref name="hint"/> ("Enter") follows the label small and dimmed. Visuals only.
    /// </summary>
    public static void Pill(ImDrawListPtr dl, ImFontPtr font, float f, Vector2 min, float w, float h, string label, PillKind kind, bool hover, float alpha,
        string? hint = null, Vector3? hoverTone = null, bool chevron = false)
    {
        var max = min + new Vector2(w, h);
        var size = f * 0.85f;
        var cy = min.Y + h * 0.5f;
        var ty = TextTop(cy, size);
        var tx = min.X + 10f;
        if (kind == PillKind.Primary)
        {
            dl.AddRectFilled(min, max, Col(Accent, (hover ? 1f : 0.85f) * alpha), 0f);
            var ink = Col(InkOnAccent, alpha);
            if (chevron)
            {
                dl.AddLine(new Vector2(tx + 1f, cy - 4f), new Vector2(tx + 5f, cy), ink, 1.8f);
                dl.AddLine(new Vector2(tx + 5f, cy), new Vector2(tx + 1f, cy + 4f), ink, 1.8f);
            }
            else Check(dl, new Vector2(tx + 4f, cy), ink);
            var tp = new Vector2(tx + 12f, ty);
            dl.AddText(font, size, tp, ink, label);
            dl.AddText(font, size, tp + new Vector2(0.6f, 0f), ink, label);
            if (hint != null)
            {
                var hs = f * 0.75f;
                dl.AddText(font, hs, new Vector2(tp.X + Width(font, size, label) + 10f, TextTop(cy, hs)), Col(InkOnAccent, 0.7f * alpha), hint);
            }
        }
        else
        {
            var tone = hoverTone ?? Accent;
            if (hover)
            {
                dl.AddRectFilled(min, max, Col(tone, 0.18f * alpha), 0f);
                dl.AddRect(min, max, Col(tone, 0.8f * alpha), 0f, ImDrawFlags.None, 1.2f);
            }
            else
            {
                dl.AddRectFilled(min, max, Col(Line, 0.5f * alpha), 0f);
                dl.AddRect(min, max, Col(Line, alpha), 0f);
            }
            dl.AddText(font, size, new Vector2(tx, ty), Col(Text, (hover ? 1f : 0.85f) * alpha), label);
            if (hint != null)
            {
                var hs = f * 0.75f;
                dl.AddText(font, hs, new Vector2(tx + Width(font, size, label) + 10f, TextTop(cy, hs)), Col(Muted, alpha), hint);
            }
        }
    }

    // ── Hover label ──────────────────────────────────────────────────

    /// <summary>
    /// A hover in the family (the bridge's GuideHover, small): an ink panel at most <paramref name="maxW"/> wide with a
    /// hairline, the tone's stripe, an optional tracked caps title, the body wrapped at 0.85 f in the text colour and a
    /// muted foot, on the foreground draw list, placed under <paramref name="belowY"/> at <paramref name="x"/> (above
    /// <paramref name="aboveY"/> when there is no room), kept on screen. Lines are rebuilt only when a text changes.
    /// </summary>
    public sealed class Hover
    {
        private string? _title, _body, _foot;
        private float _w = -1;
        private readonly List<string> _lines = new();
        private readonly List<bool> _muted = new();
        private string _titleCaps = "";

        public void Draw(string? title, string? body, string? foot, Vector3 tone, float x, float belowY, float aboveY, float maxW = 480f)
        {
            var font = ImGui.GetFont();
            var f = ImGui.GetFontSize();
            var small = f * 0.85f;
            const float pad = 10f, gap = 2f;
            var textW = maxW - Stripe - pad * 2;
            if (!string.Equals(_title, title, StringComparison.Ordinal) || !string.Equals(_body, body, StringComparison.Ordinal)
                || !string.Equals(_foot, foot, StringComparison.Ordinal) || _w != textW)
            {
                _title = title; _body = body; _foot = foot; _w = textW;
                _titleCaps = title?.ToUpperInvariant() ?? "";
                _lines.Clear(); _muted.Clear();
                Add(font, small, body, false, textW);
                Add(font, small, foot, true, textW);
            }
            if (_lines.Count == 0 && _titleCaps.Length == 0) return;
            var need = 0f;
            foreach (var l in _lines) need = MathF.Max(need, Width(font, small, l));
            var titleH = 0f;
            if (_titleCaps.Length > 0) { need = MathF.Max(need, TrackedWidth(font, f, _titleCaps, TitleTrack)); titleH = f + (_lines.Count > 0 ? 3f : 0f); }
            var w = MathF.Min(maxW, Stripe + pad * 2 + need);
            var h = pad * 2 + titleH + _lines.Count * small + Math.Max(0, _lines.Count - 1) * gap;
            var disp = ImGui.GetIO().DisplaySize;
            var min = new Vector2(x, belowY + 6f);
            if (min.Y + h > disp.Y - 4f) min.Y = aboveY - 6f - h;
            min.X = Math.Clamp(min.X, 4f, MathF.Max(4f, disp.X - 4f - w));
            min.Y = MathF.Max(4f, min.Y);
            var max = min + new Vector2(w, h);
            var dl = ImGui.GetForegroundDrawList();
            dl.AddRectFilled(min, max, Col(Ink, 0.96f), 0f);
            dl.AddRect(min, max, Col(Line, 1f), 0f);
            dl.AddRectFilled(min, new Vector2(min.X + Stripe, max.Y), Col(tone, 1f), 0f);
            var tx = min.X + Stripe + pad;
            var y = min.Y + pad;
            if (_titleCaps.Length > 0)
            {
                TitleCaps(dl, font, f, new Vector2(tx, y), Col(Text, 1f), ClipTracked(_titleCaps, font, f, TitleTrack, w - Stripe - pad * 2));
                y += titleH;
            }
            for (var i = 0; i < _lines.Count; i++)
            {
                dl.AddText(font, small, new Vector2(tx, y), _muted[i] ? Col(Muted, 0.9f) : Col(Text, 0.95f), _lines[i]);
                y += small + gap;
            }
        }

        private void Add(ImFontPtr font, float size, string? s, bool muted, float maxW)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (var para in s.Split('\n'))
                foreach (var line in Wrap(font, size, para, maxW)) { _lines.Add(line); _muted.Add(muted); }
        }

        private static IEnumerable<string> Wrap(ImFontPtr font, float size, string s, float maxW)
        {
            if (Width(font, size, s) <= maxW) { yield return s; yield break; }
            var cur = "";
            foreach (var word in s.Split(' '))
            {
                var candidate = cur.Length == 0 ? word : cur + " " + word;
                if (Width(font, size, candidate) <= maxW) { cur = candidate; continue; }
                if (cur.Length > 0) yield return cur;
                cur = word;
            }
            if (cur.Length > 0) yield return cur;
        }
    }
}
