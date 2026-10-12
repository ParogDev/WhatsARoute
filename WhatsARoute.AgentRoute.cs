using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ImGuiNET;
using Newtonsoft.Json;
using WhatsARoute.Rendering;
using WhatsARoute.Route;

namespace WhatsARoute;

/// <summary>
/// The agent route (research/f8-agent-ux.md 4; 2026-10-12, the user: "without losing the route we had, we pause it in some
/// way to be able to resume it later, and we let the agent show the route it needs to show the user"). An agent sets a
/// short route through the AI Bridge (kind route.agent, MCP route_agent); while it leads, ResolveObjective follows its current
/// step instead of the normal route. Nothing of the normal route is touched (reached steps, step shift, quest reads), so when
/// the agent route hands back the normal route resumes exactly where it was. Steps are done by the rule they carry (Near,
/// Area, Click, Manual, Flag, Dead, Agent); each step done and the way it ended go back as route.agent.progress events. The
/// card and the world switch to the owner's hue (AgentRouteView, read by StatusPanel and the path's colour pair).
/// </summary>
public sealed partial class WhatsARoute
{
    private const int ClickNearCells = 12, CombatCells = 30;
    private const double OwnerGoneSec = 10, PathFadeSec = 0.6;

    private readonly AgentRoute _agent = new();
    private readonly AgentRouteView _agentView = new();
    private bool _agentAtStep;          // the objective is the agent step's own target (not the travel to its area)
    private string _agentStepKey = "";
    private double _agentNearSince = -1, _nextAgentCheck, _nextOwnerCheck;
    private bool _agentArrived, _agentCombat, _agentOnCardWas;
    private string? _agentHlOwner;      // the session whose layer holds the step's highlight (cleared with the step)
    private string? _agentWhy;          // the last broken link (route_state)
    private Entity? _clickEntity;       // Click: the target entity, its state and clickability when first seen
    private string? _clickState;
    private bool _clickTargetable;
    private int _clickX, _clickY;
    private readonly Dictionary<string, double> _ownerGoneSince = new();

    /// <summary>Tick: commands from agents, the done rules, the time limits, progress back out.</summary>
    private void AgentTick(double now)
    {
        foreach (var cmd in _bridge.TakeAgentRoutes()) { _agent.Apply(cmd, now); _nextResolve = 0; }
        var inGame = GameController.InGame && GameController.Player != null;
        if (inGame && _agent.Any && now >= _nextAgentCheck)
        {
            _nextAgentCheck = now + 0.5;
            _agentCombat = InCombat();
            AgentCheckStep(now);
            if (now >= _nextOwnerCheck) { _nextOwnerCheck = now + 2; AgentOwners(now); }
        }
        _agent.Tick(now, !inGame || _agentCombat, _bossFight);   // never takes over on a loading screen
        if (_agent.StepKey != _agentStepKey) AgentStepChanged(now);
        foreach (var line in _agent.Say) Toast(line, 8);
        _agent.Say.Clear();
        foreach (var p in _agent.TakeProgress())
        {
            p.Area = _areaRaw;
            p.At = DateTime.UtcNow;
            p.PlayerGrid ??= _lastCell.x == int.MinValue ? null : [_lastCell.x, _lastCell.y];
            _bridge.Publish(AiBridgeLink.AgentProgressKind, p, keep: true);
            Trace("agent-route", $"{p.Event} {p.Id} step {p.StepIndex + 1}/{p.StepCount}{(p.Label != null ? ": " + p.Label : "")}{(p.Why != null ? " (" + p.Why + ")" : "")}");
        }
    }

    /// <summary>A new step (or route, or none): the old step's highlight goes, the per-step watch starts again.</summary>
    private void AgentStepChanged(double now)
    {
        _agentStepKey = _agent.StepKey;
        if (_agentHlOwner != null)
        {
            _bridge.HighlightFor(_agentHlOwner, JsonConvert.SerializeObject(new HighlightClearDto()));
            _agentHlOwner = null;
        }
        _agentNearSince = -1; _agentArrived = false; _clickEntity = null; _agentAtStep = false;
        _nextResolve = 0;
        // A step with nothing to walk to and a highlight (open a panel, click a thing in it): shown as it becomes current.
        if (_agent.Leads && _agent.Current is { Target.Length: 0, Highlight: not null } s && AgentArea(s) is null)
        {
            _agentArrived = true;
            _agent.Arrived(FireHighlight(s));
        }
    }

    /// <summary>The step's area pattern: its own, or an Area done rule's (the step leads there first).</summary>
    private static string? AgentArea(AgentRouteStepDto s) =>
        s.Area is { Length: > 0 } a ? a : s.Kind == "Area" && s.Done?.Value is { Length: > 0 } v ? v : null;

    private string? FireHighlight(AgentRouteStepDto s)
    {
        if (s.Highlight == null || _agent.Route?.Owner is not { Length: > 0 } owner) return s.Highlight == null ? null : "highlight: the route has no owner session";
        var why = _bridge.HighlightFor(owner, s.Highlight.ToString(Formatting.None));
        if (why == null) _agentHlOwner = owner;
        else _agentWhy = "highlight: " + why;
        return why == null ? null : "highlight: " + why;
    }

    /// <summary>The current step's done rule against the game now (2 Hz), and the arrival (highlight, "arrived").</summary>
    private void AgentCheckStep(double now)
    {
        if (!_agent.Leads || _agent.Current is not { } s) return;
        int? dist = null;
        if (_agentAtStep && _targetSpec == s.Target && _lastCell.x != int.MinValue)
            foreach (var g in _resolved.Goals)
            {
                var d = Math.Max(Math.Abs(g.x - _lastCell.x), Math.Abs(g.y - _lastCell.y));
                if (dist == null || d < dist) dist = d;
            }
        if (!_agentArrived && dist is { } ad && ad <= s.ArriveCells)
        {
            _agentArrived = true;
            _agent.Arrived(s.Highlight != null ? FireHighlight(s) : null);
        }
        var v = s.Done?.Value;
        switch (s.Kind)
        {
            case "Near":
                if (dist is { } nd && nd <= s.NearCells)
                {
                    if (_agentNearSince < 0) _agentNearSince = now;
                    else if (now - _agentNearSince >= 1) _agent.Advance("Near", now);
                }
                else _agentNearSince = -1;
                break;
            case "Area" when v is { Length: > 0 } && RouteBook.LikeAny(_areaRaw, v):
                _agent.Advance("Area", now);
                break;
            case "Flag" when v is { Length: > 0 } && AgentFlag(v):
                _agent.Advance("Flag", now);
                break;
            case "Dead" when v is { Length: > 0 } && SeenDead(v):
                _agent.Advance("Dead", now);
                break;
            case "Click":
                CheckClick(now, s);
                break;
        }
    }

    /// <summary>
    /// Click: the target entity (the goal nearest the player) is noted with its state; done when that state or its
    /// clickability changes, or it despawns, while the player is within 12 cells (far away it only unloaded).
    /// </summary>
    private void CheckClick(double now, AgentRouteStepDto s)
    {
        if (_lastCell.x == int.MinValue) return;
        var near = _clickEntity != null && Math.Max(Math.Abs(_clickX - _lastCell.x), Math.Abs(_clickY - _lastCell.y)) <= ClickNearCells;
        if (_clickEntity is { } e)
        {
            if (!e.IsValid) { if (near) _agent.Advance("Click", now); else _clickEntity = null; return; }
            if (near && (Sightings.StateOf(e) != _clickState || e.IsTargetable != _clickTargetable)) _agent.Advance("Click", now);
            return;
        }
        if (!_agentAtStep || _targetSpec != s.Target || _resolved.GoalEntities.Count == 0) return;
        var best = _resolved.GoalEntities.OrderBy(g => Math.Max(Math.Abs(g.X - _lastCell.x), Math.Abs(g.Y - _lastCell.y))).First();
        _clickEntity = best.Entity; _clickX = best.X; _clickY = best.Y;
        _clickState = Sightings.StateOf(best.Entity);
        _clickTargetable = best.Entity.IsTargetable;
    }

    /// <summary>Replan: standing on the agent step's goal finishes a Near step at once.</summary>
    private void AgentReached()
    {
        if (!_agentAtStep || !_agent.Leads || _agent.Current is not { Kind: "Near" } s || _targetSpec != s.Target) return;
        _agent.Advance("Near", _clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// In combat: a hostile monster that has taken damage within 30 cells (2 Hz, only while an agent route waits or leads).
    /// An agent route starts only outside it; a boss fight also pauses one that leads.
    /// </summary>
    private bool InCombat()
    {
        if (_lastCell.x == int.MinValue) return false;
        try
        {
            foreach (var e in GameController.EntityListWrapper.OnlyValidEntities)
            {
                if (e.Type != EntityType.Monster || !e.IsAlive || !e.IsHostile) continue;
                var gp = e.GridPos;
                if (Math.Max(Math.Abs(gp.X - _lastCell.x), Math.Abs(gp.Y - _lastCell.y)) > CombatCells) continue;
                if (e.GetComponent<Life>() is { } life && life.CurHP < life.MaxHP) return true;
            }
        }
        catch (Exception ex) { _agentWhy = $"combat check: {ex.Message}"; }
        return false;
    }

    /// <summary>
    /// Owner gone: the bridge's work list shows the owner's item final (its F8 press f8:&lt;Ref&gt;, else its session's Task
    /// item) for 10 s. An owner with no item at all can't be judged and is never ended here (the TTL still ends its route).
    /// </summary>
    private void AgentOwners(double now)
    {
        var items = WorkItems(now, out var why);
        if (items == null) { if (_agent.Route != null) _agentWhy = "owner check: " + why; return; }
        var routes = new List<AgentRouteDto>();
        if (_agent.Route != null) routes.Add(_agent.Route);
        if (_agent.Parked != null) routes.Add(_agent.Parked);
        routes.AddRange(_agent.QueuedRoutes);
        foreach (var r in routes)
        {
            if (r.Owner is not { Length: > 0 } owner) continue;
            var it = r.Ref is { } rf ? items.FirstOrDefault(i => i.Key == $"f8:{rf}") : null;
            it ??= items.FirstOrDefault(i => i.Key == owner);
            if (it?.Final != true) { _ownerGoneSince.Remove(owner); continue; }
            if (!_ownerGoneSince.TryGetValue(owner, out var since)) { _ownerGoneSince[owner] = now; continue; }
            if (now - since < OwnerGoneSec) continue;
            _ownerGoneSince.Remove(owner);
            _agent.OwnerGone(owner, now);
            _nextResolve = 0;
        }
    }

    /// <summary>
    /// ResolveObjective's first look: the agent route's current step while it is on the card. 0 = none (the normal route
    /// resolves), 1 = resolved into _resolved (go on as a resolved spec), 2 = no place to go (NoPlaceObjective ran).
    /// </summary>
    private int AgentObjective((int x, int y) cell, out string spec)
    {
        spec = "";
        _agentAtStep = false;
        if (!_agent.OnCard || _agent.Route is not { } r) return 0;
        if (_agent.Holding)
        {
            NoPlaceObjective("agent-hold:" + r.Id, HoldText(r));
            return 2;
        }
        if (_agent.Current is not { } step) return 0;
        _stepIndex = -1;
        if (AgentArea(step) is { } area && !RouteBook.LikeAny(_areaRaw, area)) spec = "travel:" + RouteBook.FirstOf(area);
        else if (step.Target.Length == 0)
        {
            NoPlaceObjective("agent:" + r.Id, step.Label);
            return 2;
        }
        else { spec = step.Target; _agentAtStep = true; }
        _targetSpec = spec;
        _resolver.Resolve(GameController, _book, spec, _areaRaw, _resolved);
        if (_agentAtStep && step.Label.Length > 0) _resolved.Label = step.Label;
        return 1;
    }

    private string HoldText(AgentRouteDto r)
    {
        var last = r.Steps.Count > 0 ? r.Steps[^1] : null;
        return "Stay here: " + (last?.Note is { Length: > 0 } n ? n : last?.Label is { Length: > 0 } l ? l : $"{r.Who} is reading something");
    }

    private Dictionary<QuestFlag, bool>? _agentFlags;
    private double _nextAgentFlags;

    /// <summary>A quest flag for an agent step (read every 2 s while one waits on a flag; ReadFlags reads only route.json's).</summary>
    private bool AgentFlag(string name)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_agentFlags == null || now >= _nextAgentFlags)
        {
            _nextAgentFlags = now + 2;
            try { _agentFlags = GameController.IngameState.Data.ServerData?.QuestFlags; } catch { _agentFlags = null; }
        }
        if (!Enum.TryParse<QuestFlag>(name, out var f)) { _agentWhy = $"Done.Value '{name}' is not a QuestFlag name"; return false; }
        return _agentFlags != null && _agentFlags.TryGetValue(f, out var v) && v;
    }

    /// <summary>route_state's view of the agent routes (null when there are none and nothing went wrong).</summary>
    private AgentRouteStateDto? AgentState()
    {
        if (!_agent.Any && _agent.HandBackEvent == null && _agentWhy == null) return null;
        var s = _agent.State(_clock.Elapsed.TotalSeconds);
        s.Why = _agentWhy;
        return s;
    }

    // ── The player's controls ──

    /// <summary>Next / Previous step keys while the agent route leads: its steps, not the normal route's. True when handled.</summary>
    private bool AgentStepKey(int dir)
    {
        if (!_agent.Leads) return false;
        if (dir > 0) _agent.Advance("skipped", _clock.Elapsed.TotalSeconds);
        else _agent.Back();
        _nextResolve = 0;
        return true;
    }

    private void AgentDidIt() { _agent.Advance("Manual", _clock.Elapsed.TotalSeconds); _nextResolve = 0; }
    private void AgentBackToRoute() { _agent.Leave(_clock.Elapsed.TotalSeconds); _nextResolve = 0; }
    private void AgentResumeParked() { if (_agent.ResumeParked(_clock.Elapsed.TotalSeconds)) _nextResolve = 0; }

    // ── The view: card, colours, controls ──

    /// <summary>Rebuilds <see cref="_agentView"/> (with the card's 4 Hz sample).</summary>
    private void BuildAgentView(double now)
    {
        var v = _agentView;
        v.Clear();
        v.TookOverAt = _agent.TookOverAt;
        v.HandBackAt = _agent.HandBackAt;
        v.HandBackEvent = _agent.HandBackEvent;
        if (_agent.OnCard && _agent.Route is { } r)
        {
            v.OnCard = true;
            v.HueIndex = r.Hue;
            v.Hue = AgentRouteView.HueRgb(r.Hue);
            (v.PathCore, v.PathGlow) = AgentRouteView.PathPair(v.Hue);
            v.Byline = $"{r.Who.ToUpperInvariant()}'S ROUTE{(r.Ref is { } rf ? $" - F8 #{rf}" : "")}";
            v.StepIndex = _agent.Index;
            v.StepCount = _agent.Count;
            v.StepText = $"{_agent.Index + 1} of {_agent.Count}";
            v.Reason = r.Reason;
            var mins = Math.Max(1, (int)Math.Ceiling((_agent.ExpiresAt - now) / 60));
            v.HoverFoot = $"Ends by itself in {mins} min, or when its {r.Steps.Count} step{(r.Steps.Count == 1 ? " is" : "s are")} done.";
            v.BackToRouteVisible = true;
            v.BackToRouteTooltip = $"Follow your own route again. {r.Who} is told; its route stays one click away for 10 min.";
            if (_agent.Holding)
            {
                v.Holding = true;
                v.HoldText = HoldText(r);
                v.Title = v.HoldText;
            }
            else if (_agent.Current is { } s)
            {
                v.Title = s.Label;
                v.Note = s.Note;
                v.DidItVisible = s.Kind == "Manual";
            }
        }
        else if (now - _agent.HandBackAt < 1.5 && _agent.HandBackEvent is "done" or "expired" or "ended" or "owner-gone")
        {
            v.Byline = $"{(_agent.HandBackWho ?? "Claude").ToUpperInvariant()}'S ROUTE - {(_agent.HandBackEvent == "done" ? "DONE" : "ENDED")}";
            v.HueIndex = _agent.HandBackHue;
            v.Hue = AgentRouteView.HueRgb(_agent.HandBackHue);
        }
        if (_agent.Route == null && _agent.Parked is { } p)
        {
            v.ParkedVisible = true;
            v.ParkedText = $"{p.Who}'s route ({p.Steps.Count - _agent.ParkedIndex} left)";
            v.ParkedHue = AgentRouteView.HueRgb(p.Hue);
        }
    }

    /// <summary>DrawStatus: the card's input while the agent route is on it (byline, its step, dots, note, pills, washes) and the parked pill.</summary>
    private void AgentStatus(ref StatusPanel.Input i, double now)
    {
        BuildAgentView(now);
        var v = _agentView;
        i.Byline = v.Byline.Length > 0 ? v.Byline : null;
        i.BylineHue = v.Hue;
        i.AgentOnCard = v.OnCard;
        i.ParkedText = v.ParkedVisible ? v.ParkedText : null;
        i.ParkedHue = v.ParkedHue;
        // The wash: the owner's hue when an agent route took the card, gold when it handed back (whichever was last).
        if (v.HandBackAt > v.TookOverAt) { i.WashAt = v.HandBackAt; i.WashHue = AgentRouteView.Gold; }
        else { i.WashAt = v.TookOverAt; i.WashHue = v.Hue; }
        if (!v.OnCard) return;
        i.DidIt = v.DidItVisible;
        i.BackToRoute = v.BackToRouteVisible;
        i.Manual = false; i.NoRoute = false; i.QuestTarget = false;
        if (v.Holding)
        {
            i.StepIndex = -1; i.StepCount = 0;
            i.StateOverride = $"{_agent.Route?.Who ?? "Claude"} asked you to stay here for a moment";
            return;
        }
        i.StepIndex = v.StepIndex;
        i.StepCount = v.StepCount;
        i.DoneMask = v.StepIndex <= 0 ? 0UL : (1UL << Math.Min(v.StepIndex, 63)) - 1;
        i.MetaOverride = (v.Note is { Length: > 0 } n ? n : i.Area) + "  ·  " + v.StepText;
        if (v.DidItVisible) i.StateOverride = v.Note is { Length: > 0 } dn ? dn + ", then press Did it" : "Do it, then press Did it";
        else if (_agent.Current is { Target.Length: 0 }) i.StateOverride = "Nothing to walk to: do what it says";
    }

    /// <summary>
    /// The path and marker colours (RenderBody, per frame) and the card's (DrawStatus): the route's own blended toward the
    /// owner's hue while the agent route is on the card, cross-fading over 0.6 s at take-over and hand-back.
    /// </summary>
    private void AgentColours(ref Vector3 core, ref Vector3 glow, double time)
    {
        var on = _agent.OnCard;
        if (on != _agentOnCardWas)
        {
            _agentOnCardWas = on;
            _agentView.PathSwitchedAt = time;
            if (on && _agent.Route is { } r) (_agentView.PathCore, _agentView.PathGlow) = AgentRouteView.PathPair(AgentRouteView.HueRgb(r.Hue));
        }
        var k = double.IsNegativeInfinity(_agentView.PathSwitchedAt) ? 1f : Glow.Smooth((float)((time - _agentView.PathSwitchedAt) / PathFadeSec));
        _agentView.PathBlend = on ? k : 1f - k;
        _agentView.Blend(ref core, ref glow);
    }

    private readonly ToastFamily.Hover _agentHover = new();
    private const string WhyTitle = "WHY";

    /// <summary>
    /// The card's controls (research/f8-agent-ux.md 4.1), drawn by StatusPanel on the card itself: the mouse comes from one
    /// ImGui window (###route_agent_pills, no background, no decoration) laid exactly over the card while it has controls,
    /// with InvisibleButtons on the rects the card reported (the byline: hover asks WHY; Did it; Back to route; the parked
    /// route's pill: click resumes it). Hover flags go back to the card for its next frame; tooltips are toast-family hovers
    /// under the card. A normal route card has no window: clicks there go to the game.
    /// </summary>
    private void DrawAgentControls(double now)
    {
        var s = _status;
        var v = _agentView;
        s.HoverByline = s.HoverDidIt = s.HoverBack = s.HoverParked = false;
        if (!s.HasControls || s.Height <= 0f) return;
        var pos = new Vector2(Settings.Debug.StatusX.Value, Settings.Debug.StatusY.Value);
        var size = new Vector2(MathF.Round(s.Width), MathF.Round(s.Height));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground;
        var below = pos.Y + size.Y;
        if (ImGui.Begin("###route_agent_pills", flags))
        {
            if (s.BylineMax.X > s.BylineMin.X) AgentControl("##agent_byline", s.BylineMin, s.BylineMax, out s.HoverByline);   // hover only: WHY
            if (s.HoverByline && v.Reason != null) _agentHover.Draw(WhyTitle, v.Reason, v.HoverFoot, v.Hue, pos.X, below, pos.Y);
            if (s.DidItMax.X > s.DidItMin.X && AgentControl("##agent_didit", s.DidItMin, s.DidItMax, out s.HoverDidIt)) AgentDidIt();
            if (s.HoverDidIt) _agentHover.Draw(null, "You did this step: the agent route moves on.", null, ToastFamily.Accent, pos.X, below, pos.Y);
            if (s.BackMax.X > s.BackMin.X && AgentControl("##agent_back", s.BackMin, s.BackMax, out s.HoverBack)) AgentBackToRoute();
            if (s.HoverBack) _agentHover.Draw(null, v.BackToRouteTooltip, null, ToastFamily.Neutral, pos.X, below, pos.Y);
            if (s.ParkedMax.X > s.ParkedMin.X && AgentControl("##agent_parked", s.ParkedMin, s.ParkedMax, out s.HoverParked)) AgentResumeParked();
            if (s.HoverParked) _agentHover.Draw(null, "Follow " + v.ParkedText + " again, from the step it was on.", null, v.ParkedHue, pos.X, below, pos.Y);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// <summary>An InvisibleButton over a rect the card reported; returns the click, hands back the hover.</summary>
    private static bool AgentControl(string id, Vector2 min, Vector2 max, out bool hovered)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton(id, max - min);
        hovered = ImGui.IsItemHovered();
        return ImGui.IsItemClicked();
    }

    /// <summary>{"clear":true}: the bridge clears the step highlight in the owner's layer.</summary>
    private sealed class HighlightClearDto
    {
        [JsonProperty("clear")] public bool Clear { get; set; } = true;
    }
}
