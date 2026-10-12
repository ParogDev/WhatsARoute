using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using WhatsARoute.Rendering;
using WhatsARoute.Route;

namespace WhatsARoute;

/// <summary>
/// F8: the compose panel (research/f8-agent-ux.md 2). F8 opens it under the route card with what the plugin noticed
/// preselected (Route/Suggestions.cs); the player picks what is wrong (chips), what it is about (scope), optionally a note,
/// where to look and hints, and sends with one click or Enter. The press goes out as a "feedback" event (Event "f8") through
/// the AI Bridge; the MCP's dispatcher starts a session for it. The bridge draws the press's progress (its work strip and
/// toasts); this plugin draws only the one-line receipt. The state lives in <see cref="ComposeState"/>; the panel here is
/// a plain ImGui version until the styled drawer replaces it.
/// </summary>
public sealed partial class WhatsARoute
{
    private const double WorkEverySec = 2, MarkerCheckSec = 0.5;
    private const float WorldPerMetre = 10.87f;

    private readonly ComposeState _compose = new();

    // What the suggestions read, tracked as the game goes (F8Tick, F8Frame).
    private string _f8Area = "", _f8Objective = "";
    private double _f8AreaSince, _f8ObjectiveSince, _nextF8Quests;
    private DateTime _f8ObjectiveClock = DateTime.Now;
    private (int x, int y) _f8ObjectiveCell = (int.MinValue, 0);
    private readonly Dictionary<string, int> _f8Quests = new();
    private (string quest, int state, double at, DateTime clock, string objective)? _f8QuestMove;
    private double _markerOffSince = -1, _nextMarkerCheck;

    // The bridge's work list, read every 2 s while something needs it (the verify row, agent route owners).
    private List<WorkItemDto>? _work;
    private string? _workWhy;
    private double _workAt = double.NegativeInfinity;

    /// <summary>Tick: the readings behind the suggestions (area time, objective age and movement, the last quest change).</summary>
    private void F8Tick(double now)
    {
        if (!GameController.InGame) return;
        if (_areaRaw != _f8Area) { _f8Area = _areaRaw; _f8AreaSince = now; }
        if (_lastObjective != _f8Objective)
        {
            _f8Objective = _lastObjective;
            _f8ObjectiveSince = now;
            _f8ObjectiveClock = DateTime.Now;
            _f8ObjectiveCell = _lastCell;
        }
        else if (_f8ObjectiveCell.x == int.MinValue) _f8ObjectiveCell = _lastCell;
        if (now < _nextF8Quests) return;
        _nextF8Quests = now + 1;
        if (_f8Quests.Count > 0)
            foreach (var (id, st) in _quests)
                if (!_f8Quests.TryGetValue(id, out var was) || was != st) _f8QuestMove = (id, st, now, DateTime.Now, _lastObjective);
        if (_quests.Count == 0) return;
        _f8Quests.Clear();
        foreach (var kv in _quests) _f8Quests[kv.Key] = kv.Value;
    }

    /// <summary>Render (2 Hz): is the marker on screen? Off screen for 10 s makes the marker-far suggestion.</summary>
    private void F8Frame(double now)
    {
        if (now < _nextMarkerCheck) return;
        _nextMarkerCheck = now + MarkerCheckSec;
        Vector3? goal = _frame.HasGoalBox ? _frame.GoalPos : _frame.Goal;
        if (goal is not { } g || !_frame.Ready) { _markerOffSince = -1; return; }
        bool on;
        try
        {
            var s = GameController.IngameState.Camera.WorldToScreen(g);
            var disp = ImGui.GetIO().DisplaySize;
            on = s.X >= 0 && s.Y >= 0 && s.X <= disp.X && s.Y <= disp.Y;
        }
        catch { on = true; }
        if (on) _markerOffSince = -1;
        else if (_markerOffSince < 0) _markerOffSince = now;
    }

    /// <summary>The bridge's work items, at most every 2 s; null (with why) when the bridge has no work list.</summary>
    private List<WorkItemDto>? WorkItems(double now, out string? why)
    {
        if (now - _workAt >= WorkEverySec)
        {
            _workAt = now;
            _work = _bridge.WorkList(out _workWhy);
        }
        why = _workWhy;
        return _work;
    }

    /// <summary>The panel's open state follows F8 (RenderBody toggles _feedbackOpen); opening computes the suggestions.</summary>
    private void DrawFeedback()
    {
        var now = _clock.Elapsed.TotalSeconds;
        F8Frame(now);
        if (_feedbackOpen && !_compose.Open) OpenCompose(now);
        else if (!_feedbackOpen && _compose.Open) _compose.Close();
        if (_compose.Open) DrawCompose(now);
        DrawReceipt(now);
    }

    private void OpenCompose(double now)
    {
        var panel = BigPanelName();
        var area = GameController.Area.CurrentArea;
        var input = new SuggestionInput
        {
            ResolverWhy = _resolved.Why,
            SecondsInArea = now - _f8AreaSince,
            Blocked = _blockedField != null,
            BlockedLabel = _blockedField != null ? _resolved.Label : null,
            StepAge = now - _f8ObjectiveSince,
            StepSince = _f8ObjectiveClock,
            NetMovedCells = _f8ObjectiveCell.x == int.MinValue || _lastCell.x == int.MinValue ? 0
                : Math.Sqrt(Math.Pow(_lastCell.x - _f8ObjectiveCell.x, 2) + Math.Pow(_lastCell.y - _f8ObjectiveCell.y, 2)),
            MarkerOffscreenSec = _markerOffSince < 0 ? 0 : now - _markerOffSince,
            MarkerMetres = (int)MathF.Round(_pathLen / WorldPerMetre),
            PanelName = panel,
            BossFight = _bossFight,
            Town = area is { IsTown: true } or { IsHideout: true },
            AreaName = _areaName,
        };
        if (_f8QuestMove is { } qm)
        {
            input.QuestMoved = qm.quest;
            input.QuestMovedState = qm.state;
            input.QuestMovedAt = qm.clock;
            input.QuestMovedAgo = now - qm.at;
            input.ObjectiveMovedSince = qm.objective != _lastObjective;
        }
        var manual = Settings.Route.Mode.Value != "Route";
        var what = manual ? "MANUAL TARGET" : (_resolved.Label.Length > 0 ? _resolved.Label : _targetSpec);
        var context = $"{(_areaName.Length > 0 ? _areaName : _areaRaw)} - {what}".ToUpperInvariant();
        _compose.OpenWith(now, context, Suggestions.Compute(input), panel, _frame.HasGoalBox || _resolved.Goals.Count > 0);
        _workAt = double.NegativeInfinity;   // the verify row reads the work list now
        UpdateVerify(now);
    }

    /// <summary>The verify row: the most recent F8 item of this HUD in LiveFix or Verifying (the bridge's work list).</summary>
    private void UpdateVerify(double now)
    {
        var items = WorkItems(now, out _);
        var it = items?.Where(i => i.Kind == "F8" && i.Seq != null && !i.Final && i.Stage is "LiveFix" or "Verifying")
            .OrderByDescending(i => i.Seq).FirstOrDefault();
        _compose.Verify = it == null ? null : new ComposeState.VerifyItem(it.Seq!.Value, it.Stage, it.Text);
    }

    private Dictionary<long, string>? _panelNames;
    private long _panelNamesUi;

    /// <summary>
    /// The open big panel's name (the bridge's HlBigPanels idea: the NPC dialogue, IngameUi.LargePanels, FullscreenPanels).
    /// The name is the IngameUi property that holds the panel, mapped once per UI object by reflection (only at an F8 press).
    /// </summary>
    private string? BigPanelName()
    {
        try
        {
            var ui = GameController.IngameState.IngameUi;
            if (ui.NpcDialog is { IsVisible: true } d) return "NPC dialogue" + (d.NpcName is { Length: > 0 } n ? $" ({n})" : "");
            ExileCore2.PoEMemory.Element? open = null;
            foreach (var p in ui.FullscreenPanels.Concat(ui.LargePanels))
                if (p is { Address: not 0, IsVisibleLocal: true }) { open = p; break; }
            if (open == null) return null;
            if (_panelNames == null || _panelNamesUi != ui.Address)
            {
                _panelNamesUi = ui.Address;
                _panelNames = new Dictionary<long, string>();
                foreach (var prop in ui.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0 || !typeof(ExileCore2.PoEMemory.Element).IsAssignableFrom(prop.PropertyType)) continue;
                    try
                    {
                        if (prop.GetValue(ui) is ExileCore2.PoEMemory.Element e && e.Address != 0) _panelNames.TryAdd(e.Address, Humanise(prop.Name));
                    }
                    catch { /* a property that can't be read now names nothing */ }
                }
            }
            return _panelNames.TryGetValue(open.Address, out var name) ? name : $"panel {open.GetType().Name} at {open.Address:X}";
        }
        catch (Exception ex) { return $"a panel (name not read: {ex.Message})"; }
    }

    /// <summary>"InventoryPanel" -> "Inventory", "PassiveSkillTree" -> "Passive Skill Tree".</summary>
    private static string Humanise(string prop)
    {
        foreach (var suffix in new[] { "Panel", "Element", "Window" })
            if (prop.Length > suffix.Length && prop.EndsWith(suffix, StringComparison.Ordinal)) { prop = prop[..^suffix.Length]; break; }
        var sb = new System.Text.StringBuilder(prop.Length + 4);
        for (var i = 0; i < prop.Length; i++)
        {
            if (i > 0 && char.IsUpper(prop[i]) && !char.IsUpper(prop[i - 1])) sb.Append(' ');
            sb.Append(prop[i]);
        }
        return sb.ToString();
    }

    // ── Sending ──

    /// <summary>
    /// Sends the press: feedback.jsonl and a "feedback" event (Event "f8"), kept for the bridge when it is absent. A
    /// "works" press alone is not kept (nothing to do later). The receipt is the local echo; the bridge tracks the rest.
    /// </summary>
    private void SendPress(bool works, string? verdict = null, long? refSeq = null)
    {
        var now = _clock.Elapsed.TotalSeconds;
        RouteStateDto? state = null;
        try { state = BuildState(); state.MapPath.Clear(); } catch { /* the press still goes out */ }
        var dto = new FeedbackDto
        {
            Kind = "feedback", Event = "f8", Area = _areaRaw, AreaName = _areaName, Target = _targetSpec, Label = _resolved.Label,
            StepIndex = _stepIndex, PlayerGrid = _lastCell.x == int.MinValue ? null : [_lastCell.x, _lastCell.y], State = state,
        };
        _compose.Fill(dto, works, verdict, refSeq);
        var fileSeq = _feedback.Write(dto);
        var worksOnly = dto.Flags is ["works"] && dto.Note == null && verdict == null;
        var sent = _bridge.Publish("feedback", dto, keep: !worksOnly);
        var summary = _compose.Summary(dto);
        if (sent is { Ok: true })
        {
            _compose.Sent(now, $"SENT #{sent.Seq} - {summary}", false);
            // An older bridge draws no progress: say here when no dispatcher listens (the new one's strip says "nobody listening yet").
            if (!_bridge.DrawsProgress && sent.Subscribers.Count == 0 && !worksOnly)
                Toast($"Saved (#{sent.Seq}), but no Claude dispatcher is listening right now: it is handled once one runs (open any Claude Code session)", 12);
            if (dto.WatchNext == true) StartWatchNext(sent.Seq);
        }
        else
        {
            var why = _bridge.Available ? "THE AI BRIDGE DID NOT TAKE IT" : "THE AI BRIDGE IS NOT LOADED";
            _compose.Sent(now, worksOnly ? $"NOT SENT - {why}" : $"KEPT FOR CLAUDE #{(fileSeq > 0 ? fileSeq.ToString() : "-")} - {why}", true);
            if (dto.WatchNext == true) StartWatchNext(null);
        }
        _feedbackOpen = false;
    }

    /// <summary>
    /// "Watch what I do next": a heads-up capture (60 s, or the heads-up key again) whose result goes out as a
    /// feedback.capture event with Ref = the press. Without a seq (the press was kept) it goes out as a plain heads-up.
    /// </summary>
    private void StartWatchNext(long? seq)
    {
        if (_capture != null) EndHeadsUp("press");
        _headsUpNote = seq is { } s ? $"watch-next for F8 #{s}" : "watch-next for an F8 press kept for the bridge";
        StartHeadsUp("Other");
        if (_capture is not { } c) return;
        c.Ref = seq;
        c.TimeoutSec = 60;
    }

    /// <summary>The F8 replies (feedback.reply). The bridge draws them (work strip and toasts); only an older bridge leaves them to the card's line.</summary>
    private readonly SortedDictionary<long, (string text, double since)> _replyPending = new();   // press -> its "on it" line, until its answer (45 min at most)
    private readonly HashSet<long> _replyDone = new();

    /// <summary>
    /// Tick: the dispatcher's and the handler's replies. With a bridge that draws progress (it has AIBridge.WorkList) they
    /// are its job: nothing is drawn here (the spec's one look for agent voices). With an older bridge, the old line above
    /// the card: an answer ~25 s, a press still being worked on keeps its "on it" line until its own answer.
    /// </summary>
    private void HandleReplies(double now)
    {
        _bridge.Refresh(GameController.PluginBridge, now);
        foreach (var seq in _bridge.Flushed) Toast($"Sent to Claude (#{seq}) now that the AI Bridge is back", 8);
        foreach (var r in _bridge.TakeReplies())
        {
            if (_bridge.DrawsProgress) { _replyPending.Remove(r.Ref); continue; }
            var text = r.Text is { Length: > 0 } t ? t : r.Status is { Length: > 0 } st ? st : r.Stage ?? "";
            if (r.Final)
            {
                _replyDone.Add(r.Ref);
                _replyPending.Remove(r.Ref);
                Toast(text, r.Failed ? 30 : 25);
            }
            else if (!_replyDone.Contains(r.Ref))
            {
                _replyPending[r.Ref] = (text, now);
                Toast(text, 900);   // until the answer replaces it (15 min cap)
            }
        }
        foreach (var old in _replyPending.Where(kv => now - kv.Value.since > 2700).Select(kv => kv.Key).ToList()) _replyPending.Remove(old);
        if (_replyPending.Count > 0 && now >= _toastUntil) Toast(_replyPending[_replyPending.Keys.Max()].text, 900);
    }

    // ── The panel (plain ImGui; the styled drawer reads the same ComposeState) ──

    private static readonly Vector4 ChipOff = new(0.169f, 0.180f, 0.212f, 0.55f), ChipOn = new(0f, 0.808f, 0.820f, 0.85f),
        ChipWarn = new(0.984f, 0.749f, 0.141f, 0.80f), ChipHover = new(0f, 0.808f, 0.820f, 0.30f), InkText = new(0.051f, 0.078f, 0.059f, 1f);

    private void DrawCompose(double now)
    {
        var c = _compose;
        if (now - _workAt >= WorkEverySec) UpdateVerify(now);
        var y = Settings.Debug.StatusY.Value + (Settings.Debug.ShowStatus.Value ? _status.Height + 12 + _agentControlsH : 110);
        ImGui.SetNextWindowPos(new Vector2(Settings.Debug.StatusX.Value, y), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(480, 0), new Vector2(480, 2000));
        ImGui.SetNextWindowBgAlpha(0.94f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove;
        if (!ImGui.Begin("###route_f8", flags)) { ImGui.End(); return; }
        var guard = c.Guarded(now);
        var send = false; bool works = false; string? verdict = null; long? refSeq = null;

        ImGui.TextUnformatted("[F8] FEEDBACK");
        ImGui.SameLine();
        ImGui.TextDisabled(c.Context.Length > 52 ? c.Context[..50] + ".." : c.Context);
        ImGui.SameLine(ImGui.GetWindowWidth() - 30);
        if (ImGui.SmallButton("x##f8close")) _feedbackOpen = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Close (Esc). Nothing is sent.");

        if (c.Verify is { } v)
        {
            ImGui.TextUnformatted(v.Lead);
            ImGui.SameLine();
            if (ImGui.Button("Works now##f8v") && !guard) { send = true; verdict = "works"; refSeq = v.Seq; }
            ImGui.SameLine();
            if (ImGui.Button("Still wrong##f8v") && !guard) { send = true; verdict = "still-wrong"; refSeq = v.Seq; }
        }

        if (c.Suggestions.Count > 0)
        {
            ImGui.TextDisabled("LOOKS LIKE");
            for (var i = 0; i < c.Suggestions.Count; i++)
            {
                var s = c.Suggestions[i];
                if (i > 0) ImGui.SameLine();
                if (ChipButton((s.Certain ? "* " : "o ") + s.Label, $"s{i}", s.Kept, s.Certain && s.Kept, c.DigitOf(ComposeState.ChipKind.Suggestion, i), guard))
                    c.ToggleSuggestion(i);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.Reason);
            }
        }

        ImGui.TextDisabled("WHAT'S WRONG");
        for (var i = 0; i < ComposeState.FlagChips.Length; i++)
        {
            var f = ComposeState.FlagChips[i];
            if (i % 4 != 0) ImGui.SameLine();
            if (ChipButton(f.Label, $"f{i}", c.FlagOn(f.Id), false, c.DigitOf(ComposeState.ChipKind.Flag, i), guard)) c.ToggleFlag(f.Id);
        }

        ImGui.TextDisabled("ABOUT");
        c.HoverScope = null;
        for (var i = 0; i < c.ScopeChips.Count; i++)
        {
            var sc = c.ScopeChips[i];
            ImGui.SameLine();
            if (ChipButton(sc.Label, $"c{i}", c.Scope == sc.Id, false, c.DigitOf(ComposeState.ChipKind.Scope, i), guard)) c.SetScope(sc.Id);
            if (ImGui.IsItemHovered()) c.HoverScope = sc.Id;
        }

        ImGui.SetNextItemWidth(460);
        if (ImGui.InputTextWithHint("##f8note", "Say more (optional)", ref c.Note, ComposeState.NoteMax, ImGuiInputTextFlags.EnterReturnsTrue) && !guard) send = true;
        ImGui.SetNextItemWidth(460);
        if (ImGui.InputTextWithHint("##f8watch", "Where should Claude look? e.g. \"only the panel open now\"", ref c.WatchFor, ComposeState.WatchForMax, ImGuiInputTextFlags.EnterReturnsTrue) && !guard) send = true;
        for (var i = 0; i < c.HintChips.Count; i++)
        {
            var h = c.HintChips[i];
            if (i > 0) ImGui.SameLine();
            if (ChipButton(h.Label, $"h{i}", c.HintOn(h.Id), false, c.DigitOf(ComposeState.ChipKind.Hint, i), guard)) c.ToggleHint(h.Id);
        }

        if (ImGui.Button("Works here##f8works") && !guard) { send = true; works = true; }
        ImGui.SameLine(ImGui.GetWindowWidth() - 120);
        if (ImGui.Button("> Send   Enter##f8send") && !guard) send = true;

        // Keys once the panel has focus (the first click gives it): Enter sends, Esc closes, digits toggle chips.
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !guard)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) _feedbackOpen = false;
            else if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) send = true;
            else if (!ImGui.GetIO().WantTextInput)
                for (var d = 0; d <= 9; d++)
                    if (ImGui.IsKeyPressed(ImGuiKey._0 + d)) { c.PressDigit(d); break; }
        }
        ImGui.End();
        if (send && _feedbackOpen) SendPress(works, verdict, refSeq);
    }

    /// <summary>A chip as a toggle button: accent when on, warn for a preselected suggestion, the digit badge in front.</summary>
    private static bool ChipButton(string label, string id, bool on, bool warn, string? digit, bool guard)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, warn ? ChipWarn : on ? ChipOn : ChipOff);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, on ? (warn ? ChipWarn : ChipOn) : ChipHover);
        ImGui.PushStyleColor(ImGuiCol.Text, on ? InkText : new Vector4(0.949f, 0.945f, 0.933f, 0.85f));
        var clicked = ImGui.Button($"{(digit != null && !ImGui.GetIO().WantTextInput ? digit + " " : "")}{(on ? "[x] " : "")}{label}##chip{id}");
        ImGui.PopStyleColor(3);
        return clicked && !guard;
    }

    /// <summary>The receipt: one line where the panel was, accent (or warn when kept), held 1.2 s, fading 0.3 s.</summary>
    private void DrawReceipt(double now)
    {
        var a = _compose.ReceiptAlpha(now);
        if (a <= 0f || _compose.ReceiptText is not { } text) return;
        var dl = ImGui.GetForegroundDrawList();
        var size = ImGui.GetFontSize() * 0.9f;
        var ts = ImGui.GetFont().CalcTextSizeA(size, float.MaxValue, 0f, text);
        var y = Settings.Debug.StatusY.Value + (Settings.Debug.ShowStatus.Value ? _status.Height + 12 + _agentControlsH : 110);
        var min = new Vector2(Settings.Debug.StatusX.Value, y);
        var max = min + new Vector2(MathF.Max(480, ts.X + 30), ts.Y + 14);
        var tone = _compose.ReceiptWarn ? new Vector3(0.984f, 0.749f, 0.141f) : new Vector3(0f, 0.808f, 0.820f);
        dl.AddRectFilled(min, max, Glow.Col(new Vector3(0.082f, 0.090f, 0.110f), 0.94f * a));
        dl.AddRectFilled(min, new Vector2(min.X + 3, max.Y), Glow.Col(tone, a));
        dl.AddText(ImGui.GetFont(), size, min + new Vector2(15, 7), Glow.Col(_compose.ReceiptWarn ? tone : new Vector3(0.949f, 0.945f, 0.933f), a), text);
    }
}
