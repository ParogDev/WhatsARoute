using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WhatsARoute.Route;

/// <summary>When an agent route step counts as done (research/f8-agent-ux.md 4.4 and 11).</summary>
public sealed class DoneRuleDto
{
    /// <summary>Flag | Near | Area | Click | Manual | Agent | Dead. Default: Near with a target, Manual without.</summary>
    public string? Kind { get; set; }
    /// <summary>Flag: a QuestFlag name. Area: an area raw-name pattern (a|b, * wildcards). Dead: a monster path part.</summary>
    public string? Value { get; set; }
    /// <summary>Near: cells from the goal (default 25), held for 1 s.</summary>
    public int? Within { get; set; }
}

/// <summary>One step of an agent route: a place to go (any route target) or a thing to do, and when it counts as done.</summary>
public sealed class AgentRouteStepDto
{
    /// <summary>A route target (TargetResolver syntax: checkpoint, waypoint, transition:, tile:, radar:, entity:, click:, item:, npc:...). Empty: a do-it step with no place.</summary>
    public string Target { get; set; } = "";
    /// <summary>The card's title for this step (up to 40).</summary>
    public string Label { get; set; } = "";
    /// <summary>The card's meta line (up to 120): why, or what to do there ("Click the lever, then press Did it").</summary>
    public string? Note { get; set; }
    public DoneRuleDto? Done { get; set; }
    /// <summary>A guide.highlight target object (item | path | text | panel+child | rect, label?, action?), shown by the bridge in the owner's layer near the goal.</summary>
    public JObject? Highlight { get; set; }
    /// <summary>The highlight fires (and "arrived" is reported) within this many cells of the step's goal (default 25).</summary>
    public int? WithinCells { get; set; }
    /// <summary>Optional: the step's area pattern; elsewhere the step leads there first (travel:).</summary>
    public string? Area { get; set; }

    public string Kind => Done?.Kind is { Length: > 0 } k ? Norm(k) : Target.Length > 0 ? "Near" : "Manual";
    public int NearCells => Math.Clamp(Done?.Within ?? 25, 1, 400);
    public int ArriveCells => Math.Clamp(WithinCells ?? 25, 1, 400);

    private static string Norm(string k) => k.ToLowerInvariant() switch
    {
        "flag" => "Flag", "near" => "Near", "area" => "Area", "click" => "Click", "manual" => "Manual", "agent" => "Agent", "dead" => "Dead", _ => k,
    };
}

/// <summary>
/// An agent's route command (pub/sub kind route.agent, MCP route_agent). The bridge stamps Owner, OwnerName and Hue from the
/// publishing session. set: a short route that leads instead of the normal one; end: the agent takes it back; step: the agent
/// marks the current step done (Done.Kind Agent, or any step it says is done).
/// </summary>
public sealed class AgentRouteDto
{
    /// <summary>set | end | step</summary>
    public string? Op { get; set; }
    /// <summary>"&lt;session&gt;:&lt;n&gt;"</summary>
    public string Id { get; set; } = "";
    /// <summary>The owning session id (stamped by the bridge).</summary>
    public string? Owner { get; set; }
    /// <summary>The owner's display name ("Claude 2").</summary>
    public string? OwnerName { get; set; }
    /// <summary>The owner's hue, 0-5 (AttnHuePalette).</summary>
    public int Hue { get; set; }
    /// <summary>The F8 press it answers, if any.</summary>
    public long? Ref { get; set; }
    public string Title { get; set; } = "";
    public string? Reason { get; set; }
    public int TtlSec { get; set; }
    /// <summary>Resume (default) | Hold</summary>
    public string? HandBack { get; set; }
    public List<AgentRouteStepDto> Steps { get; set; } = new();
    /// <summary>end: an optional one-line toast.</summary>
    public string? Text { get; set; }
    /// <summary>step: which step is done (-1 / null: the current one).</summary>
    public int? StepIndex { get; set; }

    public string OpName => (Op ?? "set").ToLowerInvariant();
    public bool Holds => string.Equals(HandBack, "Hold", StringComparison.OrdinalIgnoreCase);
    public string Who => OwnerName is { Length: > 0 } n ? n : Owner is { Length: > 0 } o ? o : "Claude";
}

/// <summary>What the plugin tells the agent (pub/sub kind route.agent.progress).</summary>
public sealed class AgentRouteProgressDto
{
    public string Id { get; set; } = "";
    public long? Ref { get; set; }
    public string? Owner { get; set; }
    /// <summary>started | queued | arrived | step-done | step-back | paused | resumed | left | done | expired | replaced | ended | owner-gone | refused</summary>
    public string Event { get; set; } = "";
    public int StepIndex { get; set; }
    public int StepCount { get; set; }
    /// <summary>The step's label (the current step, or the one just done for step-done).</summary>
    public string? Label { get; set; }
    /// <summary>step-done: how (Near | Flag | Area | Click | Manual | Agent | Dead | skipped).</summary>
    public string? By { get; set; }
    /// <summary>The current step waits on the player's Did it (the bridge derives WaitingOn = Player from it).</summary>
    public bool Manual { get; set; }
    /// <summary>queued: the place in the queue (1 = next).</summary>
    public int? Position { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string? Area { get; set; }
    public int[]? PlayerGrid { get; set; }
    /// <summary>A broken link or a refusal: "bad_steps: ...", "highlight: bridge has no AIBridge.HighlightFor".</summary>
    public string? Why { get; set; }
}

/// <summary>The agent route as route_state shows it (RouteStateDto.AgentRoute).</summary>
public sealed class AgentRouteStateDto
{
    /// <summary>leading | waiting (for combat or a boss fight to end) | paused (boss fight) | holding | none</summary>
    public string State { get; set; } = "none";
    public AgentRouteSummaryDto? Active { get; set; }
    public AgentRouteSummaryDto? Parked { get; set; }
    public List<AgentRouteSummaryDto> Queued { get; set; } = new();
    /// <summary>The last hand-back: event and seconds ago.</summary>
    public string? LastHandBack { get; set; }
    public string? Why { get; set; }
}

public sealed class AgentRouteSummaryDto
{
    public string Id { get; set; } = "";
    public string? Owner { get; set; }
    public string? OwnerName { get; set; }
    public long? Ref { get; set; }
    public string Title { get; set; } = "";
    public int StepIndex { get; set; }
    public int StepCount { get; set; }
    public string? StepLabel { get; set; }
    public string? StepTarget { get; set; }
    /// <summary>The current step's done rule: "Near 25", "Flag X", "Manual"...</summary>
    public string? DoneRule { get; set; }
    public double SecondsLeft { get; set; }
}

/// <summary>
/// The agent routes (research/f8-agent-ux.md 4, 11): one leads at a time, the same owner's push replaces it, another owner's
/// push queues. A route starts only out of combat and boss fights, pauses in a boss fight, ends by itself (TTL), and hands
/// back as the spec's table says: done (or Hold: the last step stays on the card 60 s), Back to route (parked 10 min,
/// resumable), expired, ended by the agent, owner gone. Nothing of the normal route is touched: the plugin resolves the
/// agent step instead while <see cref="Leads"/>, and the normal route resumes where it was.
/// </summary>
public sealed class AgentRoute
{
    public const int DefaultTtl = 600, MaxTtl = 1800, MinTtl = 30, MaxSteps = 8;
    public const double ParkSec = 600, HoldSec = 60;

    private sealed class Queued
    {
        public AgentRouteDto Route = null!;
        public double ExpiresAt;
    }

    public AgentRouteDto? Route { get; private set; }
    public int Index { get; private set; }
    /// <summary>False while it waits for combat or a boss fight to end before it takes over.</summary>
    public bool Started { get; private set; }
    public bool Paused { get; private set; }
    public bool Holding { get; private set; }
    public double HoldUntil { get; private set; }
    public double ExpiresAt { get; private set; }
    /// <summary>When it took over the card (the arrival flash).</summary>
    public double TookOverAt { get; private set; } = double.NegativeInfinity;

    public AgentRouteDto? Parked { get; private set; }
    public int ParkedIndex { get; private set; }
    public double ParkedUntil { get; private set; }
    private double _parkedTtlLeft;

    private readonly List<Queued> _queue = new();
    public int QueueCount => _queue.Count;
    public IEnumerable<AgentRouteDto> QueuedRoutes => _queue.Select(q => q.Route);

    /// <summary>The last hand-back (done, left, expired, ended, owner-gone) and when, for the card's byline flash.</summary>
    public string? HandBackEvent { get; private set; }
    public double HandBackAt { get; private set; } = double.NegativeInfinity;
    public string? HandBackWho { get; private set; }
    public int HandBackHue { get; private set; }

    /// <summary>The agent step is the objective (the card, path and markers are the agent's).</summary>
    public bool Leads => Route != null && Started && !Paused && !Holding;
    /// <summary>The card shows the agent route (leading, or holding its last step).</summary>
    public bool OnCard => Route != null && Started && !Paused;
    public bool Any => Route != null || Parked != null || _queue.Count > 0;
    public AgentRouteStepDto? Current => Route != null && Index < Route.Steps.Count ? Route.Steps[Index] : null;
    public int Count => Route?.Steps.Count ?? 0;
    /// <summary>The step changes (or the route does): the plugin resets its per-step watch on a new key.</summary>
    public string StepKey => Route == null ? "" : $"{Route.Id}#{Index}{(Started ? "" : "w")}{(Holding ? "h" : "")}";

    private readonly List<AgentRouteProgressDto> _out = new();
    /// <summary>Lines to toast (ended with Text, owner gone).</summary>
    public readonly List<string> Say = new();

    public List<AgentRouteProgressDto> TakeProgress()
    {
        var l = new List<AgentRouteProgressDto>(_out);
        _out.Clear();
        return l;
    }

    /// <summary>Applies a route.agent command.</summary>
    public void Apply(AgentRouteDto cmd, double now)
    {
        switch (cmd.OpName)
        {
            case "set" or "push":
                Set(cmd, now);
                break;
            case "step":
                if (Route != null && Same(cmd, Route) && !Holding && (cmd.StepIndex is null or < 0 || cmd.StepIndex == Index)) Advance("Agent", now);
                break;
            case "end" or "clear":
                End(cmd, now);
                break;
            default:
                Emit(cmd, "refused", 0, why: $"unknown op '{cmd.Op}' (set | end | step)");
                break;
        }
    }

    private static bool Same(AgentRouteDto cmd, AgentRouteDto r) =>
        cmd.Id.Length > 0 ? cmd.Id == r.Id : cmd.Owner is { Length: > 0 } o && o == r.Owner;

    private void Set(AgentRouteDto cmd, double now)
    {
        if (cmd.Steps.Count is < 1 or > MaxSteps) { Emit(cmd, "refused", 0, why: $"bad_steps: {cmd.Steps.Count} steps (1-{MaxSteps})"); return; }
        Clip(cmd);
        var ttl = Math.Clamp(cmd.TtlSec <= 0 ? DefaultTtl : cmd.TtlSec, MinTtl, MaxTtl);
        // The same owner's parked or queued route is superseded by this one.
        if (Parked is { } p && p.Owner == cmd.Owner) { Emit(p, "replaced", ParkedIndex); Parked = null; }
        _queue.RemoveAll(q => q.Route.Owner == cmd.Owner && Emit(q.Route, "replaced", 0));
        if (Route != null && Route.Owner != cmd.Owner)
        {
            _queue.Add(new Queued { Route = cmd, ExpiresAt = now + ttl });
            Emit(cmd, "queued", 0, position: _queue.Count, why: $"after {Route.Who}'s route");
            return;
        }
        var replacing = Route != null;
        if (replacing) Emit(Route!, "replaced", Index);
        var wasStarted = replacing && Started;
        Route = cmd;
        Index = 0; Paused = false; Holding = false;
        ExpiresAt = now + ttl;
        Started = wasStarted;   // the same owner's new route keeps the card (no flash); a new one waits for a calm moment
        if (Started) Emit(Route, "started", 0);
    }

    private void End(AgentRouteDto cmd, double now)
    {
        if (Route != null && Same(cmd, Route)) { HandBack("ended", now, cmd.Text); return; }
        if (Parked != null && Same(cmd, Parked)) { Emit(Parked, "ended", ParkedIndex); Parked = null; return; }
        var q = _queue.FirstOrDefault(x => Same(cmd, x.Route));
        if (q != null) { _queue.Remove(q); Emit(q.Route, "ended", 0); }
    }

    /// <summary>Per tick: start when calm, pause in a boss fight, the time limits, the queue.</summary>
    public void Tick(double now, bool combat, bool bossFight)
    {
        if (Route != null && now >= ExpiresAt) HandBack("expired", now);
        if (Holding && now >= HoldUntil) { Route = null; Holding = false; Started = false; }
        if (Parked != null && now >= ParkedUntil) { Emit(Parked, "expired", ParkedIndex, why: "parked 10 min"); Parked = null; }
        for (var i = _queue.Count - 1; i >= 0; i--)
            if (now >= _queue[i].ExpiresAt) { Emit(_queue[i].Route, "expired", 0, why: "waited in the queue past its TTL"); _queue.RemoveAt(i); }
        if (Route == null && _queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            Route = next.Route; Index = 0; Started = false; Paused = false; Holding = false;
            ExpiresAt = next.ExpiresAt;
        }
        if (Route == null) return;
        if (!Started)
        {
            if (combat || bossFight) return;
            Started = true;
            TookOverAt = now;
            Emit(Route, "started", Index);
            return;
        }
        if (Holding) return;
        if (bossFight && !Paused) { Paused = true; Emit(Route, "paused", Index); }
        else if (!bossFight && Paused) { Paused = false; Emit(Route, "resumed", Index); }
    }

    /// <summary>The current step is done: the next one, or the route hands back.</summary>
    public void Advance(string by, double now, string? why = null)
    {
        if (Route == null || Holding) return;
        Emit(Route, "step-done", Index, by: by, why: why);
        if (Index + 1 < Route.Steps.Count) { Index++; return; }
        HandBack("done", now);
    }

    /// <summary>The player's Previous step key while the agent route leads.</summary>
    public void Back()
    {
        if (Route == null || Holding || Index == 0) return;
        Index--;
        Emit(Route, "step-back", Index);
    }

    /// <summary>Back to route: the normal route returns; this one stays resumable for 10 min.</summary>
    public void Leave(double now)
    {
        if (Route == null) return;
        if (Holding) { Route = null; Holding = false; Started = false; return; }
        Emit(Route, "left", Index);
        Parked = Route; ParkedIndex = Index; ParkedUntil = now + ParkSec; _parkedTtlLeft = Math.Max(MinTtl, ExpiresAt - now);
        Mark("left", now, Route);
        Route = null; Started = false; Paused = false;
    }

    /// <summary>The parked pill: the parked route leads again at the step it was on (when no other route leads).</summary>
    public bool ResumeParked(double now)
    {
        if (Parked == null || Route != null) return false;
        Route = Parked; Index = ParkedIndex; Started = true; Paused = false; Holding = false;
        ExpiresAt = now + _parkedTtlLeft;
        TookOverAt = now;
        Parked = null;
        Emit(Route, "resumed", Index);
        return true;
    }

    /// <summary>The owner's session is gone: its routes end (active, parked, queued).</summary>
    public void OwnerGone(string owner, double now)
    {
        if (Route != null && Route.Owner == owner) { Say.Add($"{Route.Who.ToUpperInvariant()}'S ROUTE ENDED: {Route.Who} went quiet"); HandBack("owner-gone", now); }
        if (Parked != null && Parked.Owner == owner) { Emit(Parked, "owner-gone", ParkedIndex); Parked = null; }
        _queue.RemoveAll(q => q.Route.Owner == owner && Emit(q.Route, "owner-gone", 0));
    }

    /// <summary>Reports an arrival within the step's WithinCells (with the highlight's outcome in Why).</summary>
    public void Arrived(string? why) { if (Route != null) Emit(Route, "arrived", Index, why: why); }

    private void HandBack(string evt, double now, string? text = null)
    {
        if (Route == null) return;
        if (Holding)
        {
            // Holding the last step: "done" went out already; the agent's end (or owner gone) still tells it.
            if (evt != "expired") Emit(Route, evt, Index);
            if (evt == "ended" && text is { Length: > 0 } ht) Say.Add(ht);
            Route = null; Holding = false; Started = false;
            return;
        }
        if (evt == "done" && Route.Holds)
        {
            // Hold: the last step stays on the card ("Stay here: ...") until the agent ends it or 60 s pass.
            Emit(Route, "done", Index);
            Holding = true;
            HoldUntil = now + HoldSec;
            Mark("done", now, Route);
            return;
        }
        Emit(Route, evt, Index);
        if (evt == "ended" && text is { Length: > 0 } t) Say.Add(t);
        Mark(evt, now, Route);
        Route = null; Started = false; Paused = false;
    }

    private void Mark(string evt, double now, AgentRouteDto r)
    {
        HandBackEvent = evt; HandBackAt = now; HandBackWho = r.Who; HandBackHue = r.Hue;
    }

    /// <summary>Queues a progress event; returns true (so RemoveAll can emit as it removes).</summary>
    private bool Emit(AgentRouteDto r, string evt, int index, string? by = null, string? why = null, int? position = null)
    {
        var step = index >= 0 && index < r.Steps.Count ? r.Steps[index] : null;
        _out.Add(new AgentRouteProgressDto
        {
            Id = r.Id, Ref = r.Ref, Owner = r.Owner, Event = evt, StepIndex = index, StepCount = r.Steps.Count,
            Label = step?.Label, By = by, Manual = step?.Kind == "Manual" && evt is not ("done" or "left" or "expired" or "ended" or "owner-gone" or "replaced" or "refused"),
            Position = position, Why = why,
        });
        return true;
    }

    private static void Clip(AgentRouteDto r)
    {
        r.Title = Cut(r.Title, 40) ?? "";
        r.Reason = Cut(r.Reason, 120);
        foreach (var s in r.Steps)
        {
            s.Label = Cut(s.Label, 40) ?? "";
            s.Note = Cut(s.Note, 120);
            s.Target ??= "";
        }
    }

    public static string? Cut(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..(max - 2)].TrimEnd() + "..";

    public AgentRouteStateDto State(double now)
    {
        var dto = new AgentRouteStateDto
        {
            State = Route == null ? "none" : !Started ? "waiting" : Paused ? "paused" : Holding ? "holding" : "leading",
            Active = Route == null ? null : Summary(Route, Index, ExpiresAt - now),
            Parked = Parked == null ? null : Summary(Parked, ParkedIndex, ParkedUntil - now),
            LastHandBack = HandBackEvent == null ? null : $"{HandBackEvent} {now - HandBackAt:0} s ago",
        };
        foreach (var q in _queue) dto.Queued.Add(Summary(q.Route, 0, q.ExpiresAt - now));
        return dto;
    }

    private static AgentRouteSummaryDto Summary(AgentRouteDto r, int i, double left)
    {
        var s = i < r.Steps.Count ? r.Steps[i] : null;
        return new AgentRouteSummaryDto
        {
            Id = r.Id, Owner = r.Owner, OwnerName = r.OwnerName, Ref = r.Ref, Title = r.Title, StepIndex = i, StepCount = r.Steps.Count,
            StepLabel = s?.Label, StepTarget = s?.Target,
            DoneRule = s == null ? null : s.Kind switch
            {
                "Near" => $"Near {s.NearCells}",
                "Flag" or "Area" or "Dead" => $"{s.Kind} {s.Done?.Value}",
                _ => s.Kind,
            },
            SecondsLeft = Math.Round(Math.Max(0, left)),
        };
    }
}
