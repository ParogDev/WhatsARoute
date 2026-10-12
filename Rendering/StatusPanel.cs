using System;
using System.Numerics;
using ImGuiNET;

namespace WhatsARoute.Rendering;

/// <summary>
/// The objective card at the top-left: what you are going to, where that stands in the area's steps, how far it is,
/// and, only when something is not plain routing, one short line in the player's language (cut off from the goal,
/// waiting for it to load, in another area, the terrain still loading, a break). In the route's own look: a dark
/// translucent card, an accent bar and a pin in the path's gold, the objective large and warm, the area and steps
/// small and muted. Strings and text sizes are rebuilt only when an input changes (the plugin samples them a few
/// times per second); a frame is a handful of draw calls and one eased size.
/// </summary>
public sealed class StatusPanel
{
    public enum Tone { Normal, Busy, Info, Warn, Break }

    /// <summary>What the panel reads. The strings are the plugin's own (compared by value, never copied per frame).</summary>
    public struct Input
    {
        public string Area;             // the area's display name
        public string Label;            // the objective as the resolver named it
        public string GoalLabel;        // the objective itself while the route leads to an exit instead (cut off from it)
        public bool Manual;             // Manual mode: the target is the user's, not a route step
        public bool NoRoute;            // Route mode, but route.json has no steps for this area (fallback target)
        public bool QuestTarget;        // the quest's own target in its area (no step)
        public int StepIndex, StepCount;   // among the steps shown (optional ones are skipped when they are off)
        public ulong DoneMask;          // bit per shown step
        public string? Note;            // the current step's note from route.json
        public string? Why;             // the broken link, raw
        public string? AreaWhy;         // why the area grid is not there, raw
        public bool GridReady, FieldReady, PathReady, Blocked, FromMemory, HasTravelTo, ViaTravelMap, ViaPortal, TravelMapOpen, StopChosen;
        public string? PortalKey;       // the player's own portal binding ("Shift+T"), from the game config
        public bool DoorAhead;          // a closed door on the path ahead: click it
        public bool UseItem;            // the objective is an item to use from the inventory: no path
        public bool TextOnly;           // the quest text says what to do and there is no place to go (witness, summon, survive)
        public bool BossFight;          // a unique boss alive near you: the world drawing is hidden
        public string? TravelHub;      // what to take in town after the portal: "the Desert Map" (Act 2), else "the waypoint"
        public float PathLen;           // world units along the whole route
        public int PathCells;
        public string? Detail;          // the dev dump, when wanted (drawn under the card, wrapped)
    }

    private const float WorldPerMetre = 10.87f;   // one grid cell
    private const double FadeSec = 0.35;
    private const float PadX = 11f, PadY = 8f, Bar = 3f, PinR = 3.6f, DotR = 2.7f, DotGap = 9f, MinW = 210f;
    private static readonly Vector3 CardFill = new(0.04f, 0.06f, 0.07f);
    private static readonly Vector3 TextWarm = new(0.97f, 0.94f, 0.86f);
    private static readonly Vector3 TextDim = new(0.66f, 0.70f, 0.74f);
    private static readonly Vector3 TextMuted = new(0.50f, 0.55f, 0.60f);
    private static readonly Vector3 ToneInfo = new(0.72f, 0.82f, 0.90f);
    private static readonly Vector3 ToneBreak = new(0.90f, 0.52f, 0.46f);

    private Input _last;
    private bool _any;
    private float _fontSize;   // the HUD's font size at the last layout: a size change re-measures
    private float _maxW;

    // composed
    private string _title = "", _meta = "", _dist = "", _state = "";
    private Tone _tone;
    private bool _dotsShown;
    private int _dotIndex, _dotCount;
    private ulong _dotDone;
    private float _titleSize, _metaSize, _stateSize;
    private Vector2 _titleW, _distW, _metaW, _stateW, _detailW;
    private float _targetW, _targetH, _w, _h;
    private double _titleAt, _stateAt, _shownAt, _lastTime;
    private int _metres;

    /// <summary>Takes the plugin's state; rebuilds the card only when something it shows changed.</summary>
    public void Update(in Input i, float maxWidth, double now)
    {
        var metres = i.PathReady ? Metres(i.PathLen) : -1;
        if (_any && metres == _metres && Same(i, _last) && _fontSize == ImGui.GetFontSize() && _maxW == maxWidth) return;
        if (!_any) _shownAt = now;
        var titleBefore = _title; var stateBefore = _state; var toneBefore = _tone;
        _metres = metres;
        _last = i;
        _any = true;
        Compose(i);
        Measure(maxWidth);
        if (!string.Equals(titleBefore, _title, StringComparison.Ordinal)) _titleAt = now;
        if (!string.Equals(stateBefore, _state, StringComparison.Ordinal) || toneBefore != _tone) _stateAt = now;
    }

    /// <summary>Draws the card at <paramref name="pos"/>; <paramref name="core"/> / <paramref name="glow"/> are the path's colours.</summary>
    public void Draw(ImDrawListPtr dl, Vector2 pos, Vector3 core, Vector3 glow, double time)
    {
        if (!_any) return;
        var dt = (float)Math.Clamp(time - _lastTime, 0, 0.1);
        _lastTime = time;
        if (_w <= 0f) { _w = _targetW; _h = _targetH; }
        var ease = 1f - MathF.Exp(-dt * 14f);
        _w += (_targetW - _w) * ease;
        _h += (_targetH - _h) * ease;
        var appear = Glow.Smooth((float)((time - _shownAt) / FadeSec));
        var titleSnap = Glow.Smooth((float)((time - _titleAt) / FadeSec));
        var stateSnap = Glow.Smooth((float)((time - _stateAt) / FadeSec));
        var font = ImGui.GetFont();
        pos = new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y));
        var min = pos;
        var max = pos + new Vector2(MathF.Round(_w), MathF.Round(_h));

        // Card: fill, hairline, accent bar (breathing while the route is still working something out).
        dl.AddRectFilled(min, max, Glow.Col(CardFill, 0.84f * appear), 6f);
        dl.AddRect(min, max, Glow.Col(glow, 0.30f * appear), 6f, ImDrawFlags.None, 1f);
        var busy = _tone == Tone.Busy ? 0.55f + 0.45f * (0.5f + 0.5f * MathF.Sin((float)(time * Math.PI * 2 / 1.6))) : 1f;
        var barCol = _tone switch { Tone.Break => ToneBreak, Tone.Info => ToneInfo, _ => glow };
        dl.AddRectFilled(new Vector2(min.X, min.Y + 6f), new Vector2(min.X + Bar, max.Y - 6f), Glow.Col(barCol, 0.9f * busy * appear), 1.5f);

        // Title row: the pin, the objective, the distance on the right.
        var x = min.X + PadX;
        var y = min.Y + PadY;
        var cy = y + _titleSize * 0.5f;
        dl.AddCircle(new Vector2(x + PinR + 1f, cy), PinR + 1.6f, Glow.Col(glow, 0.35f * appear), 16, 2.2f);
        dl.AddCircle(new Vector2(x + PinR + 1f, cy), PinR, Glow.Col(core, 0.9f * appear), 16, 1.3f);
        dl.AddCircleFilled(new Vector2(x + PinR + 1f, cy), 1.5f, Glow.Col(core, 0.95f * appear), 8);
        var tx = x + PinR * 2f + 9f;
        var slide = (1f - titleSnap) * 5f;
        dl.AddText(font, _titleSize, new Vector2(tx, y + slide), Glow.Col(TextWarm, (0.35f + 0.65f * titleSnap) * appear), _title);
        if (_dist.Length > 0)
            dl.AddText(font, _titleSize, new Vector2(max.X - PadX - _distW.X, y), Glow.Col(core, 0.95f * appear), _dist);
        y += _titleSize + 3f;

        // Meta row: the area (and how the target was chosen), the step dots on the right.
        dl.AddText(font, _metaSize, new Vector2(tx, y), Glow.Col(TextDim, 0.9f * appear), _meta);
        if (_dotsShown)
        {
            var dy = y + _metaSize * 0.5f;
            var dx = max.X - PadX - DotR;
            for (var k = _dotCount - 1; k >= 0; k--, dx -= DotGap)
            {
                var c = new Vector2(dx, dy);
                if (k == _dotIndex)
                {
                    dl.AddCircle(c, DotR + 1.8f, Glow.Col(glow, 0.35f * appear), 12, 1.6f);
                    dl.AddCircleFilled(c, DotR, Glow.Col(core, 0.95f * appear), 12);
                }
                else if ((_dotDone & (1UL << k)) != 0) dl.AddCircleFilled(c, DotR - 0.4f, Glow.Col(TextMuted, 0.75f * appear), 12);
                else dl.AddCircle(c, DotR - 0.4f, Glow.Col(TextMuted, 0.7f * appear), 12, 1.1f);
            }
        }
        y += _metaSize;

        // State row(s): only when there is something to say.
        if (_state.Length > 0)
        {
            y += 5f;
            var col = _tone switch { Tone.Warn => glow, Tone.Break => ToneBreak, Tone.Info => ToneInfo, Tone.Busy => ToneInfo, _ => TextDim };
            dl.AddText(font, _stateSize, new Vector2(tx, y + (1f - stateSnap) * 4f), Glow.Col(col, (0.3f + 0.7f * stateSnap) * appear), _state, _targetW - PadX - (tx - min.X));
        }

        // Detail, under the card: the dev dump, small and muted, wrapped to the card's width.
        if (_last.Detail is { Length: > 0 } d)
            dl.AddText(font, _metaSize, new Vector2(min.X + 6f, max.Y + 5f), Glow.Col(TextMuted, 0.9f * appear), d, _targetW - 12f);
    }

    // ── Composition ──────────────────────────────────────────────────

    private void Compose(in Input i)
    {
        _title = i.Blocked && i.GoalLabel.Length > 0 ? i.GoalLabel : i.Label.Length > 0 ? i.Label : "Objective";
        _dotsShown = !i.Manual && !i.QuestTarget && i.StepIndex >= 0 && i.StepCount is > 0 and <= 12;
        _dotIndex = i.StepIndex; _dotCount = i.StepCount; _dotDone = i.DoneMask;
        var area = i.Area.Length > 0 ? i.Area : "Loading area";
        _meta = i.Manual ? area + "  ·  manual target"
            : i.NoRoute ? area + "  ·  no route written yet"
            : i.QuestTarget ? area + "  ·  quest"
            : i.StepIndex >= 0 && i.StepCount > 12 ? area + "  ·  step " + (i.StepIndex + 1) + " of " + i.StepCount
            : area;
        var (state, tone) = StateFor(i);
        _state = state; _tone = tone;
        _dist = !i.PathReady ? "" : i.PathCells <= 4 ? "here" : _metres >= 1000 ? (_metres / 1000f).ToString("0.0") + " km" : _metres + " m";
    }

    /// <summary>The plugin's states in the player's words. The raw text stays in Detail for the "Detailed status" toggle.</summary>
    private static (string, Tone) StateFor(in Input i)
    {
        if (!i.GridReady)
            return i.AreaWhy is { } aw && aw.StartsWith("reading terrain:", StringComparison.Ordinal)
                ? ("Could not read the area's terrain", Tone.Break)
                : ("Reading the area's terrain", Tone.Busy);
        if (i.BossFight) return ("Boss fight: the route is hidden until it's over", Tone.Info);
        if (i.UseItem) return ("Open your inventory and right-click it", Tone.Info);
        if (i.TextOnly) return ("Nothing to walk to: do what the quest says", Tone.Info);
        if (i.Blocked)
        {
            // The player's words, and always what to do next (2026-10-11, the user: "cut off from it, and no exit found
            // on this side yet ... it's like developer talk. It feels like not advancing").
            if (i.PathReady) return ("No way through here yet: take the marked exit", Tone.Info);
            if (i.Why is { } bw && bw.Contains("no exit seen", StringComparison.Ordinal)) return ("The way there isn't open yet: keep exploring", Tone.Info);
            return ("No way through here yet: looking for another way", Tone.Busy);
        }
        if (i.Why is { } w)
        {
            if (Has(w, "loaded near you yet")) return ("Not in sight yet: keep moving, it shows up as you get closer", Tone.Info);
            if (Has(w, "building the area grid") || Has(w, "terrain not loaded")) return ("Reading the area's terrain", Tone.Busy);
            if (Has(w, "no exit to ")) return ("Exit not found yet: keep exploring", Tone.Info);
            if (Has(w, "no tile matches") || Has(w, "targets.json has no") || Has(w, "Radar")) return ("Not on this layout's map: keep exploring", Tone.Warn);
            if (Has(w, "you are not on a") || Has(w, "after the step's filters")) return (i.Note is { Length: > 0 } n ? n : "Nothing to do here for this step: keep going", Tone.Info);
            if (Has(w, "cannot reach the goal")) return ("No path there from here yet: keep exploring", Tone.Info);
            if (Has(w, "none within")) return ("Can't walk right up to it: get as close as you can", Tone.Info);
            if (Has(w, "descent stuck")) return ("The route got stuck here", Tone.Break);
            if (Has(w, "unknown target kind")) return ("route.json: unknown target kind", Tone.Break);
            if (Has(w, "QuestFlags")) return ("Could not read the quest flags", Tone.Break);
            if (Has(w, "UncompletedQuests")) return ("Could not read the quest list", Tone.Break);
            if (w.StartsWith("tick:", StringComparison.Ordinal)) return ("Something broke while planning", Tone.Break);
            if (w.StartsWith("render:", StringComparison.Ordinal)) return ("Something broke while drawing", Tone.Break);
            if (w.StartsWith("cost map:", StringComparison.Ordinal)) return ("Could not build the cost map", Tone.Break);
            if (Has(w, "Render component")) return ("Waiting for your character", Tone.Busy);
            return ("No route right now: keep exploring", Tone.Warn);
        }
        if (i.ViaPortal && !i.TravelMapOpen)
        {
            var hub = i.TravelHub ?? "the Desert Map";
            return (i.PortalKey is { Length: > 0 } k ? $"Open a portal ({k}), then take {hub}" : $"Open a portal to town, then take {hub}", Tone.Info);
        }
        if (i.TravelMapOpen && i.StopChosen) return ("Pick the marked stop", Tone.Info);   // also an island on Makoru's map
        if (i.HasTravelTo)
        {
            if (i.TravelMapOpen) return ("That destination is not on this map yet", Tone.Info);
            return i.ViaTravelMap ? ("In another area: take the Desert Map", Tone.Info) : ("In another area: take this exit", Tone.Info);
        }
        if (!i.FieldReady || !i.PathReady) return ("Finding the way", Tone.Busy);
        if (i.DoorAhead) return ("Open the door ahead", Tone.Info);
        if (i.PathCells <= 4) return ("", Tone.Normal);
        if (i.FromMemory) return ("Seen earlier: leading you back to it", Tone.Info);
        return ("", Tone.Normal);
    }

    private static bool Has(string s, string part) => s.Contains(part, StringComparison.Ordinal);

    private static int Metres(float worldLen)
    {
        var m = worldLen / WorldPerMetre;
        return m < 100 ? (int)MathF.Round(m / 5f) * 5 : (int)MathF.Round(m / 10f) * 10;
    }

    // ── Layout ───────────────────────────────────────────────────────

    private void Measure(float maxWidth)
    {
        var font = ImGui.GetFont();
        _fontSize = ImGui.GetFontSize();
        _maxW = maxWidth;
        _titleSize = MathF.Round(_fontSize * 1.2f);
        _metaSize = MathF.Round(_fontSize * 0.88f);
        _stateSize = MathF.Round(_fontSize * 0.95f);
        var textX = PadX + PinR * 2f + 9f;
        var inner = MathF.Max(80f, maxWidth - textX - PadX);

        _distW = _dist.Length > 0 ? font.CalcTextSizeA(_titleSize, float.MaxValue, 0f, _dist) : Vector2.Zero;
        var titleRoom = inner - (_dist.Length > 0 ? _distW.X + 12f : 0f);
        _title = Fit(font, _titleSize, _title, titleRoom);
        _titleW = font.CalcTextSizeA(_titleSize, float.MaxValue, 0f, _title);
        var dotsW = _dotsShown ? _dotCount * DotGap + 10f : 0f;
        _meta = Fit(font, _metaSize, _meta, inner - dotsW);
        _metaW = font.CalcTextSizeA(_metaSize, float.MaxValue, 0f, _meta);

        var rowW = MathF.Max(_titleW.X + (_dist.Length > 0 ? _distW.X + 12f : 0f), _metaW.X + dotsW);
        var stateFlat = _state.Length > 0 ? font.CalcTextSizeA(_stateSize, float.MaxValue, 0f, _state).X : 0f;
        var w = Math.Clamp(textX + MathF.Max(rowW, stateFlat) + PadX, MinW, maxWidth);
        _stateW = _state.Length > 0 ? font.CalcTextSizeA(_stateSize, float.MaxValue, w - textX - PadX, _state) : Vector2.Zero;
        _detailW = _last.Detail is { Length: > 0 } d ? font.CalcTextSizeA(_metaSize, float.MaxValue, w - 12f, d) : Vector2.Zero;
        _targetW = w;
        _targetH = PadY + _titleSize + 3f + _metaSize + (_state.Length > 0 ? 5f + _stateW.Y : 0f) + PadY;
    }

    /// <summary>The text, or its head with an ellipsis, within <paramref name="room"/> px. Runs only when the text changed.</summary>
    private static string Fit(ImFontPtr font, float size, string text, float room)
    {
        if (text.Length == 0 || font.CalcTextSizeA(size, float.MaxValue, 0f, text).X <= room) return text;
        var lo = 0; var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (font.CalcTextSizeA(size, float.MaxValue, 0f, text[..mid] + "...").X <= room) lo = mid;
            else hi = mid - 1;
        }
        return lo <= 0 ? "..." : text[..lo].TrimEnd() + "...";
    }

    private static bool Same(in Input a, in Input b) =>
        string.Equals(a.Area, b.Area, StringComparison.Ordinal) && string.Equals(a.Label, b.Label, StringComparison.Ordinal)
        && string.Equals(a.GoalLabel, b.GoalLabel, StringComparison.Ordinal) && a.Manual == b.Manual && a.NoRoute == b.NoRoute && a.QuestTarget == b.QuestTarget
        && a.StepIndex == b.StepIndex && a.StepCount == b.StepCount && a.DoneMask == b.DoneMask
        && string.Equals(a.Note, b.Note, StringComparison.Ordinal) && string.Equals(a.Why, b.Why, StringComparison.Ordinal) && string.Equals(a.AreaWhy, b.AreaWhy, StringComparison.Ordinal)
        && a.GridReady == b.GridReady && a.FieldReady == b.FieldReady && a.PathReady == b.PathReady && a.Blocked == b.Blocked && a.FromMemory == b.FromMemory
        && a.HasTravelTo == b.HasTravelTo && a.ViaTravelMap == b.ViaTravelMap && a.ViaPortal == b.ViaPortal && string.Equals(a.PortalKey, b.PortalKey, StringComparison.Ordinal) && a.TravelMapOpen == b.TravelMapOpen && a.StopChosen == b.StopChosen && a.DoorAhead == b.DoorAhead && a.UseItem == b.UseItem && a.TextOnly == b.TextOnly && a.BossFight == b.BossFight
        && (a.PathCells <= 4) == (b.PathCells <= 4) && string.Equals(a.Detail, b.Detail, StringComparison.Ordinal);
}
