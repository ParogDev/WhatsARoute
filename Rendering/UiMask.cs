using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ExileCore2;
using ExileCore2.PoEMemory;
using ImGuiNET;

namespace WhatsARoute.Rendering;

/// <summary>
/// Keeps the world drawing off the game's own UI: the minimap, the quest tracker's rows, the orbs/flasks/skill bar
/// clusters and the small always-on widgets. While the Esc menu is up (its own game state, not an IngameUi panel)
/// the world drawing hides entirely.
/// <para>
/// How: the occluder rects are read 4x per second and the screen minus them is cut into a few free rectangles. The
/// world geometry is drawn ONCE (Begin/End around it); End clips that run to the first free rect and re-emits its
/// index range under each other free rect's scissor. No geometry is rebuilt: the copies are index memcpys, and the
/// scissor cuts glow halos cleanly at the UI's edge. Only free rects that meet the run's vertex bbox get a copy.
/// </para>
/// </summary>
public sealed class UiMask
{
    private const float Pad = 4f;                 // breathing room around each element
    private const double CheckEvery = 0.5, FullEvery = 10, EscapeEvery = 0.1;

    private readonly List<Vector4> _occ = new(48), _free = new(64), _prev = new(48);
    private readonly List<Vector4> _scratch = new(48);
    private readonly List<Element> _wrappers = new(16);   // the tracker's full-width boxes, for the signature
    private readonly List<Element> _poking = new(8);      // side-panel parts that reach outside the panel, for the signature
    private double _nextCheck, _nextFull, _nextEscape;
    private int _sig;
    private Vector2 _disp;
    private short[] _idx = new short[4096];
    private int _cmdStart, _idxStart, _vtxStart;
    private uint _vtxOffset;

    /// <summary>The Esc menu is open: draw nothing in the world.</summary>
    public bool Paused { get; private set; }
    public int Occluders => _occ.Count;
    public int FreeRects => _free.Count;
    public int LastCopies { get; private set; }
    public string? Why { get; private set; }

    /// <summary>
    /// Reading a UI rect walks the element's parents (~10 us and ~6 KB for a tracker row), so the full scan (~1 ms) runs
    /// only when a cheap signature changes (what is shown: tracker rows, minimap, large map) or every few seconds for
    /// what the signature can't see (a widget appearing, a reworded objective). The free rects are rebuilt only when
    /// the occluders actually changed.
    /// </summary>
    public void Refresh(GameController gc, double now, Vector2 disp)
    {
        if (now >= _nextEscape) { _nextEscape = now + EscapeEvery; Paused = gc.Game.IsEscapeState; }
        if (now < _nextCheck && disp == _disp) return;
        _nextCheck = now + CheckEvery;
        int sig;
        try { sig = Signature(gc, disp); }
        catch (Exception ex) { sig = 0; Why = $"ui mask signature: {ex.Message}"; }
        if (sig == _sig && disp == _disp && now < _nextFull) return;
        _sig = sig;
        _nextFull = now + FullEvery;
        _disp = disp;
        _prev.Clear(); _prev.AddRange(_occ);
        try { Scan(gc, disp); Why = null; }
        catch (Exception ex) { _occ.Clear(); Why = $"ui mask: {ex.Message} (drawing over the UI until the next scan)"; }
        if (!Same(_prev, _occ) || _free.Count == 0) BuildFree(disp);
        Scans++;
    }

    public int Scans { get; private set; }

    private int Signature(GameController gc, Vector2 disp)
    {
        var ui = gc.IngameState.IngameUi;
        var h = new HashCode();
        h.Add(disp);
        h.Add(ui.Map?.LargeMap?.IsVisibleLocal ?? false);
        h.Add(ui.Map?.SmallMiniMap?.IsVisibleLocal ?? false);
        h.Add(ui.QuestTracker?.IsVisibleLocal ?? false);
        h.Add(ui.OpenLeftPanel is { } lp && lp.IsVisibleLocal ? lp.Address : 0);    // the side panel shown (stash, inventory...), or none
        h.Add(ui.OpenRightPanel is { } rp && rp.IsVisibleLocal ? rp.Address : 0);
        foreach (var e in _poking) h.Add(e.IsVisibleLocal);   // the stash tab list opened or closed
        foreach (var w in _wrappers)
        {
            h.Add(w.ChildCount);
            foreach (var c in w.Children) h.Add(c != null && c.IsVisibleLocal);   // a quest expanded or collapsed
        }
        return h.ToHashCode();
    }

    private static bool Same(List<Vector4> a, List<Vector4> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (Vector4.DistanceSquared(a[i], b[i]) > 0.25f) return false;
        return true;
    }

    /// <summary>Debug: the occluders in red, on the foreground (not masked).</summary>
    public void DrawOutlines()
    {
        var dl = ImGui.GetForegroundDrawList();
        foreach (var o in _occ) dl.AddRect(new Vector2(o.X, o.Y), new Vector2(o.Z, o.W), 0xC03040FF, 0f, ImDrawFlags.None, 1.5f);
    }

    // ── Occluders ───────────────────────────────────────────────────

    private void Scan(GameController gc, Vector2 disp)
    {
        _occ.Clear();
        _wrappers.Clear();
        _poking.Clear();
        var ui = gc.IngameState.IngameUi;
        var screen = disp.X * disp.Y;

        // Side panels (stash, inventory, social, skills...): ExileCore2's LargePanels leave them out, so the big-panel
        // check never hid the route for them (2026-10-11, the user: "Drawing is visible through stash tab panels").
        // OpenLeftPanel / OpenRightPanel resolve to whichever one is shown.
        foreach (var side in new[] { ui.OpenLeftPanel, ui.OpenRightPanel })
            if (side is { } s && s.IsVisible)
            {
                var r = s.GetClientRect();
                var frame = new Vector4(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
                Add(frame);
                Poking(s, frame, disp.X, disp.Y, 0);
            }

        if (ui.Map?.SmallMiniMap is { } mini && mini.IsVisible) Add(mini.GetClientRect());
        // The area info at the top right (area, monster level, league) while the large map is open.
        foreach (var words in new[] { ui.Map?.BlueWords, ui.Map?.OrangeWords })
            if (words is { } w && w.IsVisible)
            {
                var r = w.GetClientRect();
                var box = new Vector4(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
                Parts(w, float.MaxValue, ref box, 0);
                Add(box);
            }

        // Quest tracker: its entries are full-width boxes around right-aligned text. Go down through the full-width
        // wrappers and keep the first narrower element (title, objective line, icon), then merge them per entry.
        if (ui.QuestTracker is { } qt && qt.IsVisible)
        {
            _scratch.Clear();
            _wrappers.Add(qt);
            Narrow(qt, qt.Width * 0.9f, 0);
            MergeInto(_scratch, _occ);
        }

        // Orbs, flasks, skill bar: each visible child of GameUI is a cluster; its own rect includes a wide art plate,
        // so the cluster's rect is the bbox of its smaller parts. Fullscreen wrappers are walked through.
        if (ui.GameUI is { } game && game.IsVisible)
            foreach (var c in game.Children)
                Cluster(c, screen, 0);

        // Small always-on widgets that are direct children (buff row, top-centre bar, counters). Fullscreen layers,
        // chat (its box is up even with no lines) and the panels handled above are skipped.
        var skip = new[] { ui.GameUI?.Address ?? 0, ui.Map?.Address ?? 0, ui.MapSideUI?.Address ?? 0, ui.ChatPanel?.Address ?? 0, ui.QuestTracker?.Address ?? 0 };
        foreach (var c in ui.Children)
        {
            if (c == null || !c.IsVisibleLocal || Array.IndexOf(skip, c.Address) >= 0) continue;
            var r = c.GetClientRect();
            var area = r.Width * r.Height;
            if (area < 9f || area > 0.15f * screen) continue;
            if (HasTexture(c, "EnemyHealthBars", 0)) continue;   // the rare/boss bar frame: health bars stay drawable-over
            Cluster(c, screen, 0);
        }
    }

    private static bool HasTexture(Element e, string part, int depth)
    {
        if (e.TextureName is { } t && t.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        if (depth >= 2) return false;
        foreach (var c in e.Children)
            if (c != null && HasTexture(c, part, depth + 1)) return true;
        return false;
    }

    private void Narrow(Element e, float fullWidth, int depth)
    {
        if (depth > 12) return;
        foreach (var c in e.Children)
        {
            if (c == null || !c.IsVisibleLocal) continue;
            // Unscaled Width (one read) for the wrapper test; the screen rect (a walk up the parents) only for what's kept.
            if (c.Width >= fullWidth && c.ChildCount > 0) { _wrappers.Add(c); Narrow(c, fullWidth, depth + 1); continue; }
            var r = c.GetClientRect();
            if (r.Width < 3 || r.Height < 3) continue;
            // A row's text can run past its own box (right-aligned objective lines do): take the parts too.
            var box = new Vector4(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
            Parts(c, float.MaxValue, ref box, 0);
            _scratch.Add(box);
        }
    }

    private void Cluster(Element c, float screen, int depth)
    {
        if (c == null || !c.IsVisibleLocal || depth > 3) return;
        var r = c.GetClientRect();
        var area = r.Width * r.Height;
        if (area > 0.25f * screen) { foreach (var k in c.Children) Cluster(k, screen, depth + 1); return; }
        var box = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        Parts(c, 0.9f * area, ref box, 0);
        if (box.X < box.Z) Add(box);
        else if (c.ChildCount == 0 && area >= 9f && area < 0.05f * screen) Add(r);   // a plain widget; a big childless one is art
    }

    private static void Parts(Element e, float maxArea, ref Vector4 box, int depth)
    {
        if (depth > 6) return;
        foreach (var c in e.Children)
        {
            if (c == null || !c.IsVisibleLocal) continue;
            var r = c.GetClientRect();
            var area = r.Width * r.Height;
            if (area < 9f) { Parts(c, maxArea, ref box, depth + 1); continue; }
            if (area >= maxArea) { Parts(c, maxArea, ref box, depth + 1); continue; }   // the cluster's art plate
            box = new Vector4(MathF.Min(box.X, r.X), MathF.Min(box.Y, r.Y), MathF.Max(box.Z, r.X + r.Width), MathF.Max(box.W, r.Y + r.Height));
        }
    }

    /// <summary>
    /// Parts of a side panel drawn outside its frame: the stash's tab list opens to the right of the stash, over the
    /// world (2026-10-11, the user's screenshot: the route ran through it). The first element that reaches past the
    /// frame is masked whole and not walked further; hidden ones are kept for the signature, so opening the list
    /// rescans within <see cref="CheckEvery"/> seconds.
    /// </summary>
    private void Poking(Element e, Vector4 frame, float screenW, float screenH, int depth)
    {
        if (depth > 7) return;
        var n = Math.Min(e.ChildCount, 60);
        for (var i = 0; i < n; i++)
        {
            var c = e.Children[i];
            if (c == null) continue;
            var cr = c.GetClientRect();
            // On screen only: the stash's tab strip scrolls thousands of pixels off its left edge (clipped by the game).
            var r = new Vector4(Math.Max(cr.X, 0), Math.Max(cr.Y, 0), Math.Min(cr.X + cr.Width, screenW), Math.Min(cr.Y + cr.Height, screenH));
            if (r.Z <= r.X || r.W <= r.Y) continue;
            if (r.X < frame.X - Pad || r.Z > frame.Z + Pad)
            {
                _poking.Add(c);
                if (c.IsVisibleLocal) Add(r);
                continue;
            }
            if (c.IsVisibleLocal) Poking(c, frame, screenW, screenH, depth + 1);
        }
    }

    private void Add(ExileCore2.Shared.RectangleF r) => Add(new Vector4(r.X, r.Y, r.X + r.Width, r.Y + r.Height));
    /// <summary>
    /// Padded and grown outward to whole pixels. The renderer turns each scissor into whole pixels, and two free rects
    /// meeting at y=297.7 left row 297 under neither: a 1 px seam across the screen through every line (2026-10-12,
    /// the user's screenshot). Whole-pixel occluders give whole-pixel free rects.
    /// </summary>
    private void Add(Vector4 r) => _occ.Add(new Vector4(MathF.Floor(r.X - Pad), MathF.Floor(r.Y - Pad), MathF.Ceiling(r.Z + Pad), MathF.Ceiling(r.W + Pad)));

    /// <summary>Rects that overlap once padded become their bbox (a tracker entry's title, objective and icon).</summary>
    private void MergeInto(List<Vector4> src, List<Vector4> dst)
    {
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < src.Count && !changed; i++)
                for (var j = i + 1; j < src.Count; j++)
                {
                    var a = src[i]; var b = src[j];
                    if (a.X > b.Z + 2 * Pad || b.X > a.Z + 2 * Pad || a.Y > b.W + 2 * Pad || b.Y > a.W + 2 * Pad) continue;
                    src[i] = new Vector4(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.Z, b.Z), MathF.Max(a.W, b.W));
                    src.RemoveAt(j);
                    changed = true;
                    break;
                }
        }
        foreach (var r in src) Add(r);
    }

    // ── Free rects ──────────────────────────────────────────────────

    /// <summary>The screen minus the occluders, as few rects as a greedy grid merge gives (rows first, then down).</summary>
    private void BuildFree(Vector2 disp)
    {
        _free.Clear();
        if (_occ.Count == 0) { _free.Add(new Vector4(0, 0, disp.X, disp.Y)); return; }
        var xs = new List<float> { 0, disp.X };
        var ys = new List<float> { 0, disp.Y };
        foreach (var o in _occ)
        {
            xs.Add(Math.Clamp(o.X, 0, disp.X)); xs.Add(Math.Clamp(o.Z, 0, disp.X));
            ys.Add(Math.Clamp(o.Y, 0, disp.Y)); ys.Add(Math.Clamp(o.W, 0, disp.Y));
        }
        xs.Sort(); ys.Sort();
        Dedup(xs); Dedup(ys);
        int nx = xs.Count - 1, ny = ys.Count - 1;
        var used = new bool[nx, ny];
        for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
                used[i, j] = Blocked((xs[i] + xs[i + 1]) * 0.5f, (ys[j] + ys[j + 1]) * 0.5f);
        for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
            {
                if (used[i, j]) continue;
                var i2 = i;
                while (i2 + 1 < nx && !used[i2 + 1, j]) i2++;
                var j2 = j;
                while (j2 + 1 < ny)
                {
                    var ok = true;
                    for (var k = i; k <= i2 && ok; k++) ok = !used[k, j2 + 1];
                    if (!ok) break;
                    j2++;
                }
                for (var b = j; b <= j2; b++)
                    for (var a = i; a <= i2; a++) used[a, b] = true;
                _free.Add(new Vector4(xs[i], ys[j], xs[i2 + 1], ys[j2 + 1]));
            }
    }

    private bool Blocked(float x, float y)
    {
        foreach (var o in _occ)
            if (x > o.X && x < o.Z && y > o.Y && y < o.W) return true;
        return false;
    }

    private static void Dedup(List<float> v)
    {
        for (var i = v.Count - 1; i > 0; i--)
            if (v[i] - v[i - 1] < 0.5f) v.RemoveAt(i);
    }

    // ── Clipping a draw run ─────────────────────────────────────────

    /// <summary>Starts a run of world geometry on <paramref name="dl"/>: everything drawn until End is masked.</summary>
    public void Begin(ImDrawListPtr dl)
    {
        dl.AddDrawCmd();   // the run starts in a command of its own
        _cmdStart = dl.CmdBuffer.Size - 1;
        _idxStart = dl.IdxBuffer.Size;
        _vtxStart = dl.VtxBuffer.Size;
        _vtxOffset = dl.CmdBuffer[_cmdStart].VtxOffset;
    }

    public void End(ImDrawListPtr dl)
    {
        LastCopies = 0;
        var idxEnd = dl.IdxBuffer.Size;
        var count = idxEnd - _idxStart;
        var last = dl.CmdBuffer.Size - 1;
        if (count <= 0) return;
        if (_free.Count == 1 && _free[0] == new Vector4(0, 0, _disp.X, _disp.Y)) { dl.AddDrawCmd(); return; }
        // 16-bit indices: past 64k vertices ImGui starts a new vertex offset inside the run, and copied indices would
        // point at the wrong vertices. Leave such a frame unmasked rather than draw garbage.
        if (dl.CmdBuffer[last].VtxOffset != _vtxOffset) { Why = "ui mask: the route drew over 64k vertices this frame (unmasked)"; dl.AddDrawCmd(); return; }

        // The run's bbox: only free rects it meets need a copy.
        var lo = new Vector2(float.MaxValue); var hi = new Vector2(float.MinValue);
        for (var v = _vtxStart; v < dl.VtxBuffer.Size; v++)
        {
            var p = dl.VtxBuffer[v].pos;
            lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p);
        }

        var first = -1;
        for (var f = 0; f < _free.Count; f++)
        {
            var r = _free[f];
            if (r.X >= hi.X || r.Z <= lo.X || r.Y >= hi.Y || r.W <= lo.Y) continue;
            if (first < 0)
            {
                first = f;
                continue;
            }
            if (LastCopies == 0)
            {
                if (_idx.Length < count) _idx = new short[Math.Max(count, _idx.Length * 2)];
                Marshal.Copy(dl.IdxBuffer.Data + _idxStart * 2, _idx, 0, count);
            }
            dl.PushClipRect(new Vector2(r.X, r.Y), new Vector2(r.Z, r.W), true);
            dl.PrimReserve(count, 0);
            Marshal.Copy(_idx, 0, dl._IdxWritePtr, count);
            dl._IdxWritePtr += count * 2;
            dl.PopClipRect();
            LastCopies++;
        }

        // The original run takes the first free rect (or nothing at all when it lies wholly under the UI).
        for (var c = _cmdStart; c <= last; c++)
        {
            var cmd = dl.CmdBuffer[c];
            if (first < 0) { cmd.ElemCount = 0; continue; }
            var r = _free[first];
            ref var clip = ref cmd.ClipRect;
            clip = new Vector4(MathF.Max(clip.X, r.X), MathF.Max(clip.Y, r.Y), MathF.Min(clip.Z, r.Z), MathF.Min(clip.W, r.W));
        }
        dl.AddDrawCmd();   // later drawing (other plugins) starts fresh under the normal clip rect
    }
}
