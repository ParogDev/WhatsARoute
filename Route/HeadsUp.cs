using System;
using System.Collections.Generic;
using System.Linq;

namespace WhatsARoute.Route;

/// <summary>
/// What changed around one thing the player did, for mapping it into the route: quest states, quest flags, quest items
/// in the inventory, NPC dialogs, and the state of the things near them (a lever pulled, a door opened, an altar used).
/// Published as a "heads-up" feedback event: the F8 dispatcher hands it to a fresh session that folds it into route.json.
/// </summary>
public sealed class HeadsUpDto
{
    /// <summary>What the player said they were about to do: Talk, PickUp, Use, Click, Enter, Defeat, Other.</summary>
    public string Intent { get; set; } = "";
    public string? Note { get; set; }
    public string? StartArea { get; set; }
    public string? EndArea { get; set; }
    public double Seconds { get; set; }
    /// <summary>"press" (F7 again), "settled" (quiet a few seconds after the first change), "timeout".</summary>
    public string EndedBy { get; set; } = "";
    /// <summary>"QuestId: 52 -> 51 'new state text' [Kind: subject]", or "QuestId: done".</summary>
    public List<string> QuestChanges { get; set; } = new();
    public List<string> FlagsSet { get; set; } = new();
    public List<string> FlagsCleared { get; set; } = new();
    /// <summary>"Shark Fin (Metadata/Items/QuestItems/...)".</summary>
    public List<string> ItemsGained { get; set; } = new();
    public List<string> ItemsLost { get; set; } = new();
    /// <summary>NPCs whose dialog opened, in order.</summary>
    public List<string> Dialogs { get; set; } = new();
    /// <summary>Things loaded at both ends whose state or clickability changed: "path 'name' @x,y: before -> after".</summary>
    public List<string> EntityChanges { get; set; } = new();
    /// <summary>Things that appeared or vanished near the player meanwhile (a portal, an NPC who came).</summary>
    public List<string> EntitiesAppeared { get; set; } = new();
    public List<string> EntitiesGone { get; set; } = new();
    /// <summary>The trace lines in between, in order (area, quest, flag, npc-dialog, item, objective...).</summary>
    public List<string> Events { get; set; } = new();
    /// <summary>The objective the route showed when it started and when it ended.</summary>
    public string? ObjectiveBefore { get; set; }
    public string? ObjectiveAfter { get; set; }
}

/// <summary>One near thing at a moment: what it is, where, its StateMachine states and whether it can be clicked.</summary>
public readonly record struct NearThing(string Path, string? Name, int X, int Y, string? State, bool Targetable)
{
    public string Key => $"{Path}@{X},{Y}";
    public string Show => $"{Path}{(Name is { Length: > 0 } n ? $" '{n}'" : "")} @{X},{Y}";
}

/// <summary>A running heads-up: the snapshot it started from and what happened since.</summary>
public sealed class HeadsUpCapture
{
    public const double SettleSeconds = 6, MaxAfterFirstChange = 40, Timeout = 180;

    public string Intent = "";
    public string? Note;
    public string StartArea = "";
    public double StartedAt;
    public double? FirstChangeAt;
    public double LastChangeAt;
    public Dictionary<string, int> Quests = new();
    public HashSet<ExileCore2.Shared.Enums.QuestFlag> Flags = new();
    public HashSet<string> Items = new();
    public Dictionary<string, NearThing> Near = new();
    public readonly List<string> Events = new();
    public readonly List<string> Dialogs = new();
    public string? ObjectiveBefore;
    /// <summary>An F8 press's "Watch what I do next": its bridge seq (the capture goes out as feedback.capture with this Ref).</summary>
    public long? Ref;
    /// <summary>Ends by itself after this long (F7: 180 s; an F8 watch-next: 60 s).</summary>
    public double TimeoutSec = Timeout;

    /// <summary>A change the capture waits on (quest, flag, item, dialog): it ends a few quiet seconds after the last one.</summary>
    public void Changed(double now)
    {
        FirstChangeAt ??= now;
        LastChangeAt = now;
    }

    /// <summary>"settled" or "timeout" when it should end now, else null.</summary>
    public string? ShouldEnd(double now)
    {
        if (now - StartedAt >= TimeoutSec) return "timeout";
        if (FirstChangeAt is { } f && (now - LastChangeAt >= SettleSeconds || now - f >= MaxAfterFirstChange)) return "settled";
        return null;
    }

    public static void Diff(HeadsUpDto dto, Dictionary<string, NearThing> before, Dictionary<string, NearThing> after)
    {
        foreach (var (k, b) in before)
        {
            if (!after.TryGetValue(k, out var a)) { dto.EntitiesGone.Add(b.Show); continue; }
            if (a.State != b.State || a.Targetable != b.Targetable)
                dto.EntityChanges.Add($"{b.Show}: {b.State ?? "-"}{(b.Targetable ? "" : " (not clickable)")} -> {a.State ?? "-"}{(a.Targetable ? "" : " (not clickable)")}");
        }
        foreach (var (k, a) in after)
            if (!before.ContainsKey(k)) dto.EntitiesAppeared.Add(a.Show + (a.State != null ? $" [{a.State}]" : ""));
        // Keep the event readable: a crowd of decorations coming into view is not what the player did.
        Trim(dto.EntitiesAppeared, 40);
        Trim(dto.EntitiesGone, 40);
        Trim(dto.EntityChanges, 60);
    }

    private static void Trim(List<string> list, int max)
    {
        if (list.Count <= max) return;
        var more = list.Count - max;
        list.RemoveRange(max, more);
        list.Add($"... and {more} more");
    }
}
