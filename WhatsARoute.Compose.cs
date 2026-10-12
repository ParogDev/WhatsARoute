using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using WhatsARoute.Rendering;
using WhatsARoute.Route;

namespace WhatsARoute;

/// <summary>
/// The F8 compose panel drawn in the toast family (research/f8-agent-ux.md 2; the look ported by hand in
/// Rendering/ToastFamily.cs, never linked to the bridge). Anchored under the route card, 480 wide, an ImGui window
/// (###route_f8, no title bar, no background: the panel is on its draw list) that takes the mouse over its rectangle only.
/// Title row: the F8 key cap, FEEDBACK in tracked faux-bold caps, the context run (AREA - STEP) in dimmed caps clipped with
/// ".." (hover shows it whole), the x. Then the verify row (an earlier press of this HUD in LiveFix / Verifying), LOOKS LIKE
/// (the plugin's suggestions: a preselected one in the warn look with a "!" disc, hover shows its reason), WHAT'S WRONG (the
/// flag chips, multi-select), ABOUT (the scope, single select; hovering Marker pulses the marker, hovering Open panel frames
/// the panel), the note and watch-for fields, the quick hints, and the footer: Works here (secondary) and Send (primary,
/// Enter). Chips show their digit while the overlay has keyboard focus and no text field is active; the digit toggles them.
/// Keys: Enter sends, Esc closes, Tab moves note -> watch-for; a click guard of 0.5 s after opening (a double F8 cannot send).
/// Send collapses the panel over 0.25 s into the one-line receipt (SENT #12 - WRONG PLACE - THIS STEP, tracked caps, accent
/// stripe; KEPT FOR CLAUDE in the warn tone when the bridge is absent), held 1.2 s, fading 0.3 s. The panel is calm: no pulse.
/// The heads-up panel (F7, 2.7) is its small sibling: HEADS UP, one row of intent chips, the note, Start watching (Enter).
/// The plugin's own one-line toasts (route reloaded, the bridge back) take the family's look too (DrawToastStyled).
/// State lives in ComposeState; this file draws and sends. No per-frame allocation in the steady state: digits, clipped
/// texts and hover lines are cached until their source changes.
/// </summary>
public sealed partial class WhatsARoute
{
    private const float ComposeW = 480f, ComposeTextX = 25f, ComposeRight = 460f, ComposeChipH = 8f, ComposeRowGap = 6f, ComposeSectionGap = 10f, ComposePadY = 10f;
    private const string ComposeTitle = "FEEDBACK", HeadsUpTitle = "HEADS UP", ComposeLooksLike = "LOOKS LIKE", ComposeWhatsWrong = "WHAT'S WRONG", ComposeAbout = "ABOUT";
    private const string ComposeCloseTip = "Close (Esc). Nothing is sent.", ComposeNoteHint = "Say more (optional)",
        ComposeWatchHint = "Where should Claude look? e.g. \"only the panel open now\"", HeadsUpNoteHint = "What is it? e.g. 'give the shark fin to Kaimana'",
        ComposeWorksHere = "Works here", ComposeSend = "Send", ComposeEnter = "Enter", ComposeWorksNow = "Works now", ComposeStillWrong = "Still wrong",
        HeadsUpStart = "Start watching", HeadsUpCloseTip = "Close (Esc). Nothing is watched.";

    private readonly ToastFamily.Hover _composeHover = new();
    private float _composeH;                 // the panel's last height: the receipt collapses from it
    private Vector2 _composePos;             // where it was drawn last (the receipt sits there)
    private double _composeDigitsFor = -1;   // ComposeState.OpenedAt the digit tables were built for
    private string?[] _digitSug = [], _digitFlag = [], _digitScope = [], _digitHint = [];
    private string? _ctxSrc, _ctxOut;        // the context run, clipped
    private float _ctxW = -1;
    private bool _composeFocusWatch;         // Tab in the note: focus the watch-for field next frame
    private ExileCore2.PoEMemory.Element? _composePanelEl;   // the open big panel at the press (the Open panel scope's frame on hover)
    private (Vector2 min, Vector2 max)? _composeAvoid;        // the NPC dialogue's rect at the press: the panel never covers it
    private string _headsUpIntent = "Other";
    private string? _f7CtxSrc, _f7CtxOut;    // the heads-up panel's context run (the area), clipped
    private float _f7CtxW = -1;

    /// <summary>Where the panel's top-left goes: under the route card (its eased height), or 110 px down without the card.</summary>
    private Vector2 ComposeAnchor()
    {
        var y = Settings.Debug.StatusY.Value + (Settings.Debug.ShowStatus.Value ? _status.Height + 12f : 110f);
        return new Vector2(Settings.Debug.StatusX.Value, y);
    }

    // ── The compose panel ──

    private void DrawCompose(double now)
    {
        var c = _compose;
        if (now - _workAt >= WorkEverySec) UpdateVerify(now);
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var chipH = f + ComposeChipH;
        var io = ImGui.GetIO();
        if (_composeDigitsFor != c.OpenedAt) ComposeDigits(c);

        // Layout first: the shell needs the height. Chip rows wrap at the inner width.
        var innerW = ComposeRight - ComposeTextX;
        var rowsSug = c.Suggestions.Count > 0 ? ChipRows(font, f, c.Suggestions, innerW, 0f) : 0;
        var rowsFlag = ChipRows(font, f, ComposeState.FlagChips, innerW, 0f);
        var aboutW = ToastFamily.TrackedWidth(font, small, ComposeAbout, ToastFamily.MsgTrack) + 10f;
        var rowsScope = ChipRows(font, f, c.ScopeChips, innerW, aboutW);
        var rowsHint = ChipRows(font, f, c.HintChips, innerW, 0f);
        var h = ComposePadY + f;
        if (c.Verify != null) h += ComposeSectionGap + chipH;
        if (rowsSug > 0) h += ComposeSectionGap + small + 4f + rowsSug * chipH + (rowsSug - 1) * ComposeRowGap;
        h += ComposeSectionGap + small + 4f + rowsFlag * chipH + (rowsFlag - 1) * ComposeRowGap;
        h += ComposeSectionGap + rowsScope * chipH + (rowsScope - 1) * ComposeRowGap;
        h += ComposeSectionGap + chipH + ComposeRowGap + chipH + ComposeRowGap + rowsHint * chipH + (rowsHint - 1) * ComposeRowGap;
        h += ComposeSectionGap + chipH + 2f + ComposePadY;
        h = MathF.Round(h);

        // Place: under the card; never over the NPC dialogue (below it then), kept on screen.
        var pos = ComposeAnchor();
        if (_composeAvoid is { } av && pos.X < av.max.X && pos.X + ComposeW > av.min.X && pos.Y < av.max.Y && pos.Y + h > av.min.Y) pos.Y = av.max.Y + 8f;
        pos.Y = MathF.Max(4f, MathF.Min(pos.Y, io.DisplaySize.Y - h - 4f));
        pos = new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y));
        _composePos = pos; _composeH = h;

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(ComposeW, h), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground;
        var shown = ImGui.Begin("###route_f8", flags);
        try
        {
            if (!shown) return;
            var dl = ImGui.GetWindowDrawList();
            var min = pos;
            var max = pos + new Vector2(ComposeW, h);
            ToastFamily.Panel(dl, min, max, ToastFamily.Accent, 1f);
            var x0 = min.X + ComposeTextX;
            var xMax = min.X + ComposeRight;
            var guard = c.Guarded(now);
            var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            var digits = focused && !io.WantTextInput;
            var send = false; var works = false; string? verdict = null; long? refSeq = null;
            var hoverBelow = max.Y; var hoverAbove = min.Y;

            // Title row: the key cap, FEEDBACK, the context run, the x.
            var y = min.Y + ComposePadY;
            var hy = y + f * 0.5f;
            ToastFamily.KeyCap(dl, font, f, new Vector2(x0, hy - 8f), "F8", 1f);
            var x = x0 + 22f + 8f;
            ToastFamily.TitleCaps(dl, font, f, new Vector2(x, y), ToastFamily.Col(ToastFamily.Text, 1f), ComposeTitle);
            x += ToastFamily.TrackedWidth(font, f, ComposeTitle, ToastFamily.TitleTrack) + 14f;
            var rx = xMax;
            {
                var bx = new Vector2(rx - 16f, hy - 8f);
                ImGui.SetCursorScreenPos(bx);
                ImGui.InvisibleButton("##f8close", new Vector2(16f, 16f));
                var hov = ImGui.IsItemHovered();
                if (ImGui.IsItemClicked() && !guard) _feedbackOpen = false;
                if (hov) { dl.AddRectFilled(bx, bx + new Vector2(16f, 16f), ToastFamily.Col(ToastFamily.Line, 1f), 0f); _composeHover.Draw(null, ComposeCloseTip, null, ToastFamily.Neutral, min.X, hoverBelow, hoverAbove); }
                ToastFamily.CloseGlyph(dl, new Vector2(rx - 8f, hy), 3.5f, ToastFamily.Col(hov ? ToastFamily.Text : ToastFamily.Muted, hov ? 1f : 0.8f));
                rx -= 16f + 10f;
            }
            if (rx - x > 40f && c.Context.Length > 0)
            {
                var room = rx - x;
                if (!ReferenceEquals(_ctxSrc, c.Context) || _ctxW != room) { _ctxSrc = c.Context; _ctxW = room; _ctxOut = ToastFamily.ClipTracked(c.Context, font, small, ToastFamily.MsgTrack, room); }
                ToastFamily.Tracked(dl, font, small, new Vector2(x, hy - small * 0.5f), ToastFamily.Col(ToastFamily.Muted, 1f), _ctxOut!, ToastFamily.MsgTrack);
                if (!ReferenceEquals(_ctxOut, c.Context))
                {
                    ImGui.SetCursorScreenPos(new Vector2(x, hy - small * 0.5f));
                    ImGui.InvisibleButton("##f8ctx", new Vector2(room, small));
                    if (ImGui.IsItemHovered()) _composeHover.Draw(null, c.Context, null, ToastFamily.Neutral, min.X, hoverBelow, hoverAbove);
                }
            }
            y += f;

            // The verify row: "About #11's fix:" and two pills that send at once.
            if (c.Verify is { } v)
            {
                y += ComposeSectionGap;
                var size = f * 0.9f;
                dl.AddText(font, size, new Vector2(x0, y + (chipH - size) * 0.5f), ToastFamily.Col(ToastFamily.Text, 0.9f), v.Lead);
                var px = x0 + ToastFamily.Width(font, size, v.Lead) + 12f;
                if (ComposePill(dl, font, f, ref px, y, "##f8vworks", ComposeWorksNow, ToastFamily.PillKind.Secondary, guard, ToastFamily.Ok, null, false, min.X, hoverBelow, hoverAbove,
                        "Sends: this fix works. The press is closed as confirmed by you."))
                { send = true; verdict = "works"; refSeq = v.Seq; }
                if (ComposePill(dl, font, f, ref px, y, "##f8vwrong", ComposeStillWrong, ToastFamily.PillKind.Secondary, guard, ToastFamily.Bad, null, false, min.X, hoverBelow, hoverAbove,
                        "Sends: still wrong, with whatever you pick below. A new press starts from the earlier answer."))
                { send = true; verdict = "still-wrong"; refSeq = v.Seq; }
                y += chipH;
            }

            // LOOKS LIKE: the plugin's suggestions.
            if (c.Suggestions.Count > 0)
            {
                y += ComposeSectionGap;
                ToastFamily.Tracked(dl, font, small, new Vector2(x0, y), ToastFamily.Col(ToastFamily.Muted, 1f), ComposeLooksLike, ToastFamily.MsgTrack);
                y += small + 4f;
                x = x0;
                for (var i = 0; i < c.Suggestions.Count; i++)
                {
                    var s = c.Suggestions[i];
                    var state = !s.Kept ? ToastFamily.ChipState.Off : s.Certain ? ToastFamily.ChipState.Warn : ToastFamily.ChipState.On;
                    if (ComposeChip(dl, font, f, ref x, ref y, xMax, x0, ComposeIds.Sug[i], s.Label, state, digits ? _digitSug[i] : null, guard, out var hov)) c.ToggleSuggestion(i);
                    if (hov) _composeHover.Draw(null, s.Reason, s.Certain ? "Measured by the route, so it is preselected. Turn it off if it is not the problem." : "A guess from the clock: kept only if you turn it on.", s.Certain ? ToastFamily.Warn : ToastFamily.Neutral, min.X, hoverBelow, hoverAbove);
                }
                y += chipH;
            }

            // WHAT'S WRONG: the flag chips.
            y += ComposeSectionGap;
            ToastFamily.Tracked(dl, font, small, new Vector2(x0, y), ToastFamily.Col(ToastFamily.Muted, 1f), ComposeWhatsWrong, ToastFamily.MsgTrack);
            y += small + 4f;
            x = x0;
            for (var i = 0; i < ComposeState.FlagChips.Length; i++)
            {
                var fc = ComposeState.FlagChips[i];
                if (ComposeChip(dl, font, f, ref x, ref y, xMax, x0, ComposeIds.Flag[i], fc.Label, c.FlagOn(fc.Id) ? ToastFamily.ChipState.On : ToastFamily.ChipState.Off, digits ? _digitFlag[i] : null, guard, out _))
                    c.ToggleFlag(fc.Id);
            }
            y += chipH;

            // ABOUT: the scope (one of them), with the marker pulse and the panel frame on hover.
            y += ComposeSectionGap;
            ToastFamily.Tracked(dl, font, small, new Vector2(x0, y + (chipH - small) * 0.5f), ToastFamily.Col(ToastFamily.Muted, 1f), ComposeAbout, ToastFamily.MsgTrack);
            x = x0 + aboutW;
            c.HoverScope = null;
            for (var i = 0; i < c.ScopeChips.Count; i++)
            {
                var sc = c.ScopeChips[i];
                if (ComposeChip(dl, font, f, ref x, ref y, xMax, x0 + aboutW, ComposeIds.Scope[i], sc.Label, c.Scope == sc.Id ? ToastFamily.ChipState.On : ToastFamily.ChipState.Off, digits ? _digitScope[i] : null, guard, out var hov))
                    c.SetScope(sc.Id);
                if (hov) c.HoverScope = sc.Id;
            }
            y += chipH;
            ComposeScopeEffects(now);

            // The note and the watch-for fields, then the quick hints.
            y += ComposeSectionGap;
            ComposeInputStyle(f, chipH);
            ImGui.SetCursorScreenPos(new Vector2(x0, y));
            ImGui.SetNextItemWidth(innerW);
            if (ImGui.InputTextWithHint("##f8note", ComposeNoteHint, ref c.Note, ComposeState.NoteMax, ImGuiInputTextFlags.EnterReturnsTrue) && !guard) send = true;
            if (ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Tab)) _composeFocusWatch = true;
            y += chipH + ComposeRowGap;
            ImGui.PopStyleVar(3); ImGui.PopStyleColor(6);
            ImGui.SetWindowFontScale(0.9f);
            ComposeInputStyle(f * 0.9f, chipH);
            ImGui.SetCursorScreenPos(new Vector2(x0, y));
            ImGui.SetNextItemWidth(innerW);
            if (_composeFocusWatch) { ImGui.SetKeyboardFocusHere(); _composeFocusWatch = false; }
            if (ImGui.InputTextWithHint("##f8watch", ComposeWatchHint, ref c.WatchFor, ComposeState.WatchForMax, ImGuiInputTextFlags.EnterReturnsTrue) && !guard) send = true;
            ImGui.PopStyleVar(3); ImGui.PopStyleColor(6);
            ImGui.SetWindowFontScale(1f);
            y += chipH + ComposeRowGap;
            x = x0;
            for (var i = 0; i < c.HintChips.Count; i++)
            {
                var hc = c.HintChips[i];
                if (ComposeChip(dl, font, f, ref x, ref y, xMax, x0, ComposeIds.Hint[i], hc.Label, c.HintOn(hc.Id) ? ToastFamily.ChipState.On : ToastFamily.ChipState.Off, digits ? _digitHint[i] : null, guard, out var hov))
                    c.ToggleHint(hc.Id);
                if (hov && hc.Id == "watch-next") _composeHover.Draw(null, "After Send the route watches what you do for 60 s (or until F7) and sends that too.", null, ToastFamily.Neutral, min.X, hoverBelow, hoverAbove);
            }
            y += chipH;

            // Footer: Works here (left), Send + Enter (right).
            y += ComposeSectionGap;
            var wx = x0;
            if (ComposePill(dl, font, f, ref wx, y, "##f8works", ComposeWorksHere, ToastFamily.PillKind.Secondary, guard, ToastFamily.Ok, null, false, min.X, hoverBelow, hoverAbove,
                    "Sends a thumbs up for this step. No session starts for it."))
            { send = true; works = true; }
            var sendW = ToastFamily.PillWidth(font, f, ComposeSend, ToastFamily.PillKind.Primary, ComposeEnter);
            var sx = xMax - sendW;
            if (ComposePill(dl, font, f, ref sx, y, "##f8send", ComposeSend, ToastFamily.PillKind.Primary, guard, null, ComposeEnter, true, min.X, hoverBelow, hoverAbove,
                    "Sends what you picked to Claude (Enter does too). The bridge's strip at the top tracks it from here."))
                send = true;

            // Keys once the panel has focus (the first click gives it): Enter sends, Esc closes, digits toggle chips.
            if (focused && !guard)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.Escape)) _feedbackOpen = false;
                else if (!io.WantTextInput && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))) send = true;
                else if (!io.WantTextInput)
                    for (var d = 0; d <= 9; d++)
                        if (ImGui.IsKeyPressed(ImGuiKey._0 + d)) { c.PressDigit(d); break; }
            }
            if (send && _feedbackOpen) SendPress(works, verdict, refSeq);
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
    }

    /// <summary>The input fields' look: the chip's off fill, a Line border, square, text and a muted hint; height = the chip's.</summary>
    private static void ComposeInputStyle(float fontPx, float h)
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(ToastFamily.Line, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(ToastFamily.Accent, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(ToastFamily.Accent, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(ToastFamily.Line, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(ToastFamily.Text, 1f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Vector4(ToastFamily.Muted, 0.9f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, MathF.Max(0f, (h - fontPx) * 0.5f)));
    }

    /// <summary>The digit each chip answers to (1-9, 0), built once per opening (ComposeState.DigitOf allocates).</summary>
    private void ComposeDigits(ComposeState c)
    {
        _composeDigitsFor = c.OpenedAt;
        _digitSug = new string?[c.Suggestions.Count];
        for (var i = 0; i < _digitSug.Length; i++) _digitSug[i] = c.DigitOf(ComposeState.ChipKind.Suggestion, i);
        _digitFlag = new string?[ComposeState.FlagChips.Length];
        for (var i = 0; i < _digitFlag.Length; i++) _digitFlag[i] = c.DigitOf(ComposeState.ChipKind.Flag, i);
        _digitScope = new string?[c.ScopeChips.Count];
        for (var i = 0; i < _digitScope.Length; i++) _digitScope[i] = c.DigitOf(ComposeState.ChipKind.Scope, i);
        _digitHint = new string?[c.HintChips.Count];
        for (var i = 0; i < _digitHint.Length; i++) _digitHint[i] = c.DigitOf(ComposeState.ChipKind.Hint, i);
    }

    /// <summary>How many rows a chip group takes at the inner width (the first row shortened by <paramref name="lead"/>).</summary>
    private static int ChipRows(ImFontPtr font, float f, IReadOnlyList<ComposeState.Chip> chips, float innerW, float lead)
    {
        var rows = 1; var x = lead;
        for (var i = 0; i < chips.Count; i++)
        {
            var w = ToastFamily.ChipWidth(font, f, chips[i].Label);
            if (x > lead && x + w > innerW) { rows++; x = 0f; }
            x += w + ToastFamily.ChipGap;
        }
        return rows;
    }

    private static int ChipRows(ImFontPtr font, float f, List<SuggestionDto> chips, float innerW, float lead)
    {
        var rows = 1; var x = lead;
        for (var i = 0; i < chips.Count; i++)
        {
            var w = ToastFamily.ChipWidth(font, f, chips[i].Label);
            if (x > lead && x + w > innerW) { rows++; x = 0f; }
            x += w + ToastFamily.ChipGap;
        }
        return rows;
    }

    /// <summary>One chip in a flowing row (wraps at <paramref name="xMax"/> back to <paramref name="x0"/>): an InvisibleButton under the family's chip look.</summary>
    private static bool ComposeChip(ImDrawListPtr dl, ImFontPtr font, float f, ref float x, ref float y, float xMax, float x0, string id, string label,
        ToastFamily.ChipState state, string? digit, bool guard, out bool hovered)
    {
        var w = ToastFamily.ChipWidth(font, f, label);
        var h = f + ComposeChipH;
        if (x > x0 && x + w > xMax) { x = x0; y += h + ComposeRowGap; }
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGui.InvisibleButton(id, new Vector2(w, h));
        hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked() && !guard;
        ToastFamily.Chip(dl, font, f, new Vector2(x, y), w, h, label, state, hovered, digit, 1f);
        x += w + ToastFamily.ChipGap;
        return clicked;
    }

    /// <summary>A pill with its InvisibleButton and hover; advances <paramref name="x"/> past it.</summary>
    private bool ComposePill(ImDrawListPtr dl, ImFontPtr font, float f, ref float x, float y, string id, string label, ToastFamily.PillKind kind, bool guard,
        Vector3? hoverTone, string? hint, bool chevron, float hoverX, float hoverBelow, float hoverAbove, string tip)
    {
        var w = ToastFamily.PillWidth(font, f, label, kind, hint);
        var h = f + ComposeChipH;
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGui.InvisibleButton(id, new Vector2(w, h));
        var hov = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked() && !guard;
        ToastFamily.Pill(dl, font, f, new Vector2(x, y), w, h, label, kind, hov, 1f, hint, hoverTone, chevron);
        if (hov) _composeHover.Draw(null, tip, null, hoverTone ?? ToastFamily.Neutral, hoverX, hoverBelow, hoverAbove);
        x += w + 8f;
        return clicked;
    }

    /// <summary>
    /// Hovering the Marker scope pulses the current marker (one extra sonar ring per 0.8 s, Markers.HoverPulseUntil, drawn by
    /// the world pass); hovering Open panel draws a thin neutral frame around the open panel. Both die with the hover (2.5).
    /// </summary>
    private void ComposeScopeEffects(double now)
    {
        var c = _compose;
        if (c.HoverScope == "marker") _markers.HoverPulseUntil = now + 0.1;
        if (c.HoverScope == "panel" && _composePanelEl is { } el)
        {
            try
            {
                if (el.Address != 0 && el.IsVisible)
                {
                    var r = el.GetClientRect();
                    var dl = ImGui.GetForegroundDrawList();
                    dl.AddRect(new Vector2(r.Left - 2f, r.Top - 2f), new Vector2(r.Right + 2f, r.Bottom + 2f), ToastFamily.Col(ToastFamily.Neutral, 0.9f), 0f, ImDrawFlags.None, 1.2f);
                }
            }
            catch { /* the panel closed under the hover: no frame */ }
        }
    }

    /// <summary>
    /// After Send: the panel collapses over 0.25 s into the one-line receipt at the same place (SENT #12 - ..., tracked
    /// caps, the accent stripe; KEPT FOR CLAUDE in the warn tone when the bridge did not take it), held 1.2 s, fading 0.3 s.
    /// </summary>
    private void DrawReceipt(double now)
    {
        var c = _compose;
        if (c.Open) return;
        var a = c.ReceiptAlpha(now);
        if (a <= 0f || c.ReceiptText is not { } text) return;
        var collapse = c.Collapse(now);
        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var size = f * 0.9f;
        var receiptH = MathF.Round(size + 14f);
        var h = receiptH + (_composeH - receiptH) * Glow.Smooth(collapse);
        var min = _composePos;
        var max = min + new Vector2(ComposeW, h);
        var tone = c.ReceiptWarn ? ToastFamily.Warn : ToastFamily.Accent;
        ToastFamily.Panel(dl, min, max, tone, a);
        var ta = a * (1f - collapse);
        if (ta > 0.01f)
        {
            var t = ToastFamily.ClipTracked(text, font, size, ToastFamily.TitleTrack, ComposeRight - ComposeTextX + 10f);
            ToastFamily.TitleCaps(dl, font, size, new Vector2(min.X + 15f, min.Y + 7f), ToastFamily.Col(c.ReceiptWarn ? tone : ToastFamily.Text, ta), t);
        }
    }

    // ── The heads-up panel (F7): the small sibling ──

    /// <summary>
    /// The heads-up panel in the family (2.7): the F7 key cap, HEADS UP, the area as the context run, the x; one row of
    /// intent chips (single select), the note field, Start watching (Enter). Returns true: it drew in place of the plain panel.
    /// </summary>
    private bool DrawHeadsUpStyled(double now)
    {
        var font = ImGui.GetFont();
        var f = ImGui.GetFontSize();
        var small = f * 0.85f;
        var chipH = f + ComposeChipH;
        var io = ImGui.GetIO();
        var innerW = ComposeRight - ComposeTextX;
        var rows = 1; var fx = 0f;
        for (var i = 0; i < Intents.Length; i++)
        {
            var w = ToastFamily.ChipWidth(font, f, Intents[i].label);
            if (fx > 0f && fx + w > innerW) { rows++; fx = 0f; }
            fx += w + ToastFamily.ChipGap;
        }
        var h = MathF.Round(ComposePadY + f + ComposeSectionGap + small + 4f + rows * chipH + (rows - 1) * ComposeRowGap + ComposeSectionGap + chipH + ComposeSectionGap + chipH + 2f + ComposePadY);
        var pos = ComposeAnchor();
        if (_compose.Open) pos.Y += _composeH + 8f;   // both open: under the compose panel
        pos.Y = MathF.Max(4f, MathF.Min(pos.Y, io.DisplaySize.Y - h - 4f));
        pos = new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(ComposeW, h), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground;
        var shown = ImGui.Begin("###route_f7", flags);
        try
        {
            if (!shown) return true;
            var dl = ImGui.GetWindowDrawList();
            var min = pos;
            var max = pos + new Vector2(ComposeW, h);
            ToastFamily.Panel(dl, min, max, ToastFamily.Accent, 1f);
            var x0 = min.X + ComposeTextX;
            var xMax = min.X + ComposeRight;
            var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            var start = false;

            var y = min.Y + ComposePadY;
            var hy = y + f * 0.5f;
            ToastFamily.KeyCap(dl, font, f, new Vector2(x0, hy - 8f), "F7", 1f);
            var x = x0 + 30f;
            ToastFamily.TitleCaps(dl, font, f, new Vector2(x, y), ToastFamily.Col(ToastFamily.Text, 1f), HeadsUpTitle);
            x += ToastFamily.TrackedWidth(font, f, HeadsUpTitle, ToastFamily.TitleTrack) + 14f;
            var rx = xMax;
            {
                var bx = new Vector2(rx - 16f, hy - 8f);
                ImGui.SetCursorScreenPos(bx);
                ImGui.InvisibleButton("##f7close", new Vector2(16f, 16f));
                var hov = ImGui.IsItemHovered();
                if (ImGui.IsItemClicked()) _headsUpOpen = false;
                if (hov) { dl.AddRectFilled(bx, bx + new Vector2(16f, 16f), ToastFamily.Col(ToastFamily.Line, 1f), 0f); _composeHover.Draw(null, HeadsUpCloseTip, null, ToastFamily.Neutral, min.X, max.Y, min.Y); }
                ToastFamily.CloseGlyph(dl, new Vector2(rx - 8f, hy), 3.5f, ToastFamily.Col(hov ? ToastFamily.Text : ToastFamily.Muted, hov ? 1f : 0.8f));
                rx -= 26f;
            }
            if (rx - x > 40f && _areaName.Length > 0)
            {
                if (!ReferenceEquals(_f7CtxSrc, _areaName) || _f7CtxW != rx - x) { _f7CtxSrc = _areaName; _f7CtxW = rx - x; _f7CtxOut = ToastFamily.ClipTracked(_areaName.ToUpperInvariant(), font, small, ToastFamily.MsgTrack, rx - x); }
                ToastFamily.Tracked(dl, font, small, new Vector2(x, hy - small * 0.5f), ToastFamily.Col(ToastFamily.Muted, 1f), _f7CtxOut!, ToastFamily.MsgTrack);
            }
            y += f + ComposeSectionGap;
            ToastFamily.Tracked(dl, font, small, new Vector2(x0, y), ToastFamily.Col(ToastFamily.Muted, 1f), "ABOUT TO", ToastFamily.MsgTrack);
            y += small + 4f;
            x = x0;
            for (var i = 0; i < Intents.Length; i++)
            {
                var (intent, label) = Intents[i];
                if (ComposeChip(dl, font, f, ref x, ref y, xMax, x0, ComposeIds.Intent[i], label, _headsUpIntent == intent ? ToastFamily.ChipState.On : ToastFamily.ChipState.Off, null, false, out _))
                    _headsUpIntent = intent;
            }
            y += chipH + ComposeSectionGap;
            ComposeInputStyle(f, chipH);
            ImGui.SetCursorScreenPos(new Vector2(x0, y));
            ImGui.SetNextItemWidth(innerW);
            if (ImGui.InputTextWithHint("##f7note", HeadsUpNoteHint, ref _headsUpNote, 300, ImGuiInputTextFlags.EnterReturnsTrue)) start = true;
            ImGui.PopStyleVar(3); ImGui.PopStyleColor(6);
            y += chipH + ComposeSectionGap;
            var sendW = ToastFamily.PillWidth(font, f, HeadsUpStart, ToastFamily.PillKind.Primary, ComposeEnter);
            var sx = xMax - sendW;
            if (ComposePill(dl, font, f, ref sx, y, "##f7start", HeadsUpStart, ToastFamily.PillKind.Primary, false, null, ComposeEnter, true, min.X, max.Y, min.Y,
                    "Starts watching now: what changes in the next 60 s (or until F7 again) goes to Claude."))
                start = true;
            if (focused)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.Escape)) _headsUpOpen = false;
                else if (!io.WantTextInput && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))) start = true;
            }
            if (start && _headsUpOpen) StartHeadsUp(_headsUpIntent);
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        return true;
    }

    // ── The plugin's own one-line toast ──

    /// <summary>
    /// The plugin's line above the card (route reloaded, the bridge back, a hand-back) in the family's look: ink, hairline,
    /// a neutral stripe, the text in sentence case at 0.9 f wrapped to the card's width, fading over its last second. Returns
    /// true: it drew in place of the old rounded blue toast.
    /// </summary>
    private bool DrawToastStyled(double now)
    {
        if (_toast is not { } text) return true;
        var a = (float)Math.Clamp(_toastUntil - now, 0, 1);
        var dl = ImGui.GetBackgroundDrawList();
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize() * 0.9f;
        var w = MathF.Max(240f, MathF.Min(ComposeW, Settings.Debug.StatusWidth.Value));
        var wrap = w - 15f - 12f;
        var ts = font.CalcTextSizeA(size, float.MaxValue, wrap, text);
        var h = ts.Y + 14f;
        var min = new Vector2(Settings.Debug.StatusX.Value, MathF.Max(4f, Settings.Debug.StatusY.Value - h - 10f));
        var max = min + new Vector2(w, h);
        ToastFamily.Panel(dl, min, max, ToastFamily.Neutral, a);
        dl.AddText(font, size, new Vector2(min.X + 15f, min.Y + 7f), ToastFamily.Col(ToastFamily.Text, 0.95f * a), text, wrap);
        return true;
    }

    /// <summary>Widget ids, one literal per chip, so a frame allocates none.</summary>
    private static class ComposeIds
    {
        public static readonly string[] Sug = ["##f8s0", "##f8s1", "##f8s2", "##f8s3"];
        public static readonly string[] Flag = ["##f8f0", "##f8f1", "##f8f2", "##f8f3", "##f8f4", "##f8f5", "##f8f6", "##f8f7", "##f8f8", "##f8f9", "##f8f10", "##f8f11"];
        public static readonly string[] Scope = ["##f8c0", "##f8c1", "##f8c2", "##f8c3", "##f8c4", "##f8c5"];
        public static readonly string[] Hint = ["##f8h0", "##f8h1", "##f8h2", "##f8h3"];
        public static readonly string[] Intent = ["##f7i0", "##f7i1", "##f7i2", "##f7i3", "##f7i4", "##f7i5", "##f7i6", "##f7i7"];
    }
}
