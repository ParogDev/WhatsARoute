using System;
using System.Collections.Generic;

namespace WhatsARoute.Route;

/// <summary>What the plugin measured at the F8 press, for <see cref="Suggestions.Compute"/>. Filled once, when the panel opens.</summary>
public sealed class SuggestionInput
{
    /// <summary>The last quest state change: quest, new state, when (local clock time), seconds ago, and whether the objective moved since.</summary>
    public string? QuestMoved;
    public int QuestMovedState;
    public DateTime QuestMovedAt;
    public double QuestMovedAgo = double.MaxValue;
    public bool ObjectiveMovedSince;
    /// <summary>The resolver's broken link (_resolved.Why) and seconds in this area.</summary>
    public string? ResolverWhy;
    public double SecondsInArea;
    /// <summary>Cut off from the goal; the exit the route marks instead.</summary>
    public bool Blocked;
    public string? BlockedLabel;
    /// <summary>The objective's age, when it started (local), and the player's net movement since, cells.</summary>
    public double StepAge;
    public DateTime StepSince;
    public double NetMovedCells;
    /// <summary>The marker off screen this long (0 when on screen or none), and the path's length to it, metres.</summary>
    public double MarkerOffscreenSec;
    public int MarkerMetres;
    /// <summary>The open large / fullscreen panel's name, or the NPC dialogue; null when none.</summary>
    public string? PanelName;
    public bool BossFight;
    public bool Town;
    public string? AreaName;
}

/// <summary>
/// The compose panel's pre-detected suggestions (research/f8-agent-ux.md 2.4), computed once when the panel opens. A
/// suggestion is preselected (Certain) only when the plugin measured it, never when it guessed from time. At most three
/// chips, in priority order; an open panel sets the scope instead of making a chip.
/// </summary>
public static class Suggestions
{
    public const int MaxChips = 3;
    public const double QuestWindowSec = 90, TargetMissingAfterSec = 60, StuckAfterSec = 240, StuckMovedCells = 60, MarkerOffSec = 10;

    public static List<SuggestionDto> Compute(SuggestionInput i)
    {
        var all = new List<SuggestionDto>();
        if (i.QuestMoved is { } q && i.QuestMovedAgo <= QuestWindowSec && !i.ObjectiveMovedSince)
            all.Add(Make("quest-moved", "Quest moved, step didn't", $"{q} {(i.QuestMovedState < 0 ? "finished" : $"went to state {i.QuestMovedState}")} at {i.QuestMovedAt:HH:mm}", true,
                ("quest", q), ("state", i.QuestMovedState), ("secondsAgo", Math.Round(i.QuestMovedAgo))));
        if (i.ResolverWhy is { } w && i.SecondsInArea >= TargetMissingAfterSec
            && (w.Contains("no tile matches", StringComparison.Ordinal) || w.Contains("targets.json has no", StringComparison.Ordinal)
                || w.Contains("not loaded near you yet", StringComparison.Ordinal)))
            all.Add(Make("target-missing", "Can't find the target", $"{w} (resolver)", true, ("why", w), ("secondsInArea", Math.Round(i.SecondsInArea))));
        if (i.Blocked)
            all.Add(Make("blocked", "No way through", $"path blocked; exit {i.BlockedLabel ?? "?"} marked", true, ("exit", i.BlockedLabel)));
        if (i.StepAge >= StuckAfterSec && i.NetMovedCells < StuckMovedCells)
            all.Add(Make("stuck", $"Stuck here {(int)(i.StepAge / 60)} min", $"this step since {i.StepSince:HH:mm}, {(int)i.NetMovedCells} m moved", false,
                ("stepAgeSec", Math.Round(i.StepAge)), ("netMovedCells", Math.Round(i.NetMovedCells))));
        if (i.MarkerOffscreenSec >= MarkerOffSec)
            all.Add(Make("marker-far", "Marker off screen", $"marker {i.MarkerMetres} m away", false,
                ("offscreenSec", Math.Round(i.MarkerOffscreenSec)), ("metres", i.MarkerMetres)));
        if (i.BossFight) all.Add(Make("boss", "Boss fight hides route", "a boss fight is on: the route hides until it ends", false));
        if (i.Town) all.Add(Make("town", "You're in town", $"in {i.AreaName ?? "town"}", false));
        if (all.Count > MaxChips) all.RemoveRange(MaxChips, all.Count - MaxChips);
        foreach (var s in all) s.Kept = s.Certain;
        return all;
    }

    private static SuggestionDto Make(string id, string label, string reason, bool certain, params (string k, object? v)[] evidence)
    {
        var ev = evidence.Length == 0 ? null : new Dictionary<string, object?>();
        foreach (var (k, v) in evidence) ev![k] = v;
        return new SuggestionDto { Id = id, Label = AgentRoute.Cut(label, 22)!, Reason = AgentRoute.Cut(reason, 60)!, Certain = certain, Evidence = ev };
    }
}
