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
/// toasts); this plugin draws only the one-line receipt. The state lives in <see cref="ComposeState"/>; the panel itself is
/// drawn in the toast family by WhatsARoute.Compose.cs (DrawCompose, DrawReceipt). This file opens it, reads what the
/// suggestions need and sends.
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
        if (_quests.Count == 0) return;   // a failed or empty read: no quest counts as finished on it
        // A quest that finished drops out of the open quests: the commonest "quest moved, step didn't" (state -1 = finished).
        foreach (var id in _f8Quests.Keys)
            if (!_quests.ContainsKey(id)) _f8QuestMove = (id, -1, now, DateTime.Now, _lastObjective);
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
        _composeAvoid = null;
        try
        {
            // The NPC dialogue's rect: the panel shifts below it rather than cover it (f8-agent-ux.md 6).
            if (GameController.IngameState.IngameUi.NpcDialog is { IsVisible: true } npc)
            {
                var r = npc.GetClientRect();
                _composeAvoid = (new Vector2(r.Left, r.Top), new Vector2(r.Right, r.Bottom));
            }
        }
        catch { /* no dialogue rect: nothing to avoid */ }
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
    /// The IngameUi properties that hold an Element, as (humanised name, open-instance getter delegate): resolved by
    /// reflection once per IngameUi class (a HUD update can change the class, never within a run), then called as plain
    /// delegates (CreateDelegate: cheap to bind, unlike compiling an expression per property).
    /// </summary>
    private static class PanelGetters<TUi> where TUi : class
    {
        private static (Type type, (string name, Func<TUi, ExileCore2.PoEMemory.Element> get)[] getters)? _cache;

        public static (string name, Func<TUi, ExileCore2.PoEMemory.Element> get)[] For(TUi ui)
        {
            var type = ui.GetType();
            if (_cache is { } c && c.type == type) return c.getters;
            var list = new List<(string, Func<TUi, ExileCore2.PoEMemory.Element>)>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0 || !typeof(ExileCore2.PoEMemory.Element).IsAssignableFrom(prop.PropertyType)) continue;
                if (prop.GetGetMethod() is not { } getter || getter.DeclaringType is not { } declaring) continue;
                // A getter declared below TUi (on the runtime subclass) binds to its own type and is called through a cast.
                // A property that won't bind (a constraint MakeGenericMethod rejects, an Invoke that throws) is skipped:
                // it names nothing, the other panels still bind.
                Func<TUi, ExileCore2.PoEMemory.Element>? get;
                try
                {
                    get = declaring.IsAssignableFrom(typeof(TUi))
                        ? Delegate.CreateDelegate(typeof(Func<TUi, ExileCore2.PoEMemory.Element>), getter, throwOnBindFailure: false) as Func<TUi, ExileCore2.PoEMemory.Element>
                        : typeof(TUi).IsAssignableFrom(declaring)
                            ? BindSubMethod.MakeGenericMethod(declaring).Invoke(null, new object[] { getter }) as Func<TUi, ExileCore2.PoEMemory.Element>
                            : null;
                }
                catch (Exception) { get = null; }
                if (get != null) list.Add((Humanise(prop.Name), get));
            }
            var built = list.ToArray();
            _cache = (type, built);
            return built;
        }

        private static readonly MethodInfo BindSubMethod = typeof(PanelGetters<TUi>).GetMethod(nameof(BindSub), BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>
        /// A getter declared on a subclass of TUi: an open-instance delegate on TSub. A UI object that isn't a TSub (a
        /// sibling subclass) has no such panel: null, never a cast exception.
        /// </summary>
        private static Func<TUi, ExileCore2.PoEMemory.Element>? BindSub<TSub>(MethodInfo getter) where TSub : class, TUi
        {
            if (Delegate.CreateDelegate(typeof(Func<TSub, ExileCore2.PoEMemory.Element>), getter, throwOnBindFailure: false)
                is not Func<TSub, ExileCore2.PoEMemory.Element> get) return null;
            return ui => ui is TSub sub ? get(sub) : null!;
        }
    }

    /// <summary>
    /// The open big panel's name (the bridge's HlBigPanels idea: the NPC dialogue, IngameUi.LargePanels, FullscreenPanels).
    /// The name is the IngameUi property that holds the panel: the getters are compiled once per IngameUi class
    /// (PanelGetters), the address -> name map is built once per UI object and again when the open panel isn't in it
    /// (a panel element the game recreated), so a press on a known panel does no reflection and no getter calls.
    /// </summary>
    private string? BigPanelName()
    {
        _composePanelEl = null;
        try
        {
            var ui = GameController.IngameState.IngameUi;
            if (ui.NpcDialog is { IsVisible: true } d) { _composePanelEl = d; return "NPC dialogue" + (d.NpcName is { Length: > 0 } n ? $" ({n})" : ""); }
            ExileCore2.PoEMemory.Element? open = null;
            foreach (var p in ui.FullscreenPanels.Concat(ui.LargePanels))
                if (p is { Address: not 0, IsVisibleLocal: true }) { open = p; break; }
            if (open == null) return null;
            _composePanelEl = open;
            if (_panelNames == null || _panelNamesUi != ui.Address || !_panelNames.ContainsKey(open.Address))
            {
                _panelNamesUi = ui.Address;
                _panelNames = new Dictionary<long, string>();
                foreach (var (name, get) in PanelGetters<ExileCore2.PoEMemory.MemoryObjects.IngameUIElements>.For(ui))
                {
                    try
                    {
                        if (get(ui) is { Address: not 0 } e) _panelNames.TryAdd(e.Address, name);
                    }
                    catch { /* a property that can't be read now names nothing */ }
                }
            }
            return _panelNames.TryGetValue(open.Address, out var found) ? found
                : $"panel {open.GetType().Name} at {open.Address:X} (no IngameUi property of {ui.GetType().Name} holds it)";
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
            if (!worksOnly) AwaitClaim(sent);
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
    private readonly Dictionary<long, double> _unclaimed = new();   // press -> when it went out, until the dispatcher's first reply
    private const double ClaimWaitSec = 20;

    // The shared MCP server hosts the dispatcher and runs only inside the Claude app: no app, no dispatcher (2026-10-12,
    // press #1065 sat unanswered). Fail at the broken link: the card keeps saying so until a dispatcher answers the press,
    // with either bridge (the new one's strip only says "nobody listening yet"; this line says what to do).
    private static string NoDispatcher(long seq) =>
        $"#{seq} waits: no Claude dispatcher is listening (the shared MCP server is down, e.g. the Claude app is closed). " +
        @"Open the Claude app (any Code session starts it) or run tools\mcp-http-start.ps1 in a terminal inside it; the press is handled then";

    private static string NotClaimed(long seq) =>
        $"#{seq} not picked up after {ClaimWaitSec:0} s: no Claude dispatcher answered. " +
        @"Claude app closed? Open it, or run tools\mcp-http-start.ps1 in a terminal inside it. Otherwise MCP feedback_dispatcher says why";

    /// <summary>A press that went out: watched until the dispatcher's first reply; with nobody listening, said at once.</summary>
    private void AwaitClaim(PublishResult sent)
    {
        var now = _clock.Elapsed.TotalSeconds;
        _unclaimed[sent.Seq] = now;
        if (sent.Subscribers.Count == 0) { _replyPending[sent.Seq] = (NoDispatcher(sent.Seq), now); Toast(NoDispatcher(sent.Seq), 900); }
    }

    /// <summary>
    /// Tick: the dispatcher's and the handler's replies. With a bridge that draws progress (it has AIBridge.WorkList) they
    /// are its job: nothing is drawn here (the spec's one look for agent voices). With an older bridge, the old line above
    /// the card: an answer ~25 s, a press still being worked on keeps its "on it" line until its own answer.
    /// </summary>
    private void HandleReplies(double now)
    {
        _bridge.Refresh(GameController.PluginBridge, now);
        foreach (var seq in _bridge.Flushed) { Toast($"Sent to Claude (#{seq}) now that the AI Bridge is back", 8); _unclaimed[seq] = now; }
        foreach (var r in _bridge.TakeReplies())
        {
            _unclaimed.Remove(r.Ref);
            if (_bridge.DrawsProgress)
            {
                // The bridge draws it; a "no dispatcher" line of ours goes now that one answered.
                if (_replyPending.Remove(r.Ref) && _replyPending.Count == 0) _toastUntil = 0;
                continue;
            }
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
        foreach (var seq in _unclaimed.Where(kv => now - kv.Value >= ClaimWaitSec).Select(kv => kv.Key).ToList())
        {
            _unclaimed.Remove(seq);
            if (!_replyPending.ContainsKey(seq) && !_replyDone.Contains(seq)) { _replyPending[seq] = (NotClaimed(seq), now); Toast(NotClaimed(seq), 900); }
        }
        foreach (var old in _replyPending.Where(kv => now - kv.Value.since > 2700).Select(kv => kv.Key).ToList()) _replyPending.Remove(old);
        if (_replyPending.Count > 0 && now >= _toastUntil) Toast(_replyPending[_replyPending.Keys.Max()].text, 900);
    }
}
