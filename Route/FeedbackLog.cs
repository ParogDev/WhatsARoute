using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace WhatsARoute.Route;

/// <summary>One line of feedback.jsonl: the player's verdict on the route, or a trace event (area, quest, objective).</summary>
public sealed class FeedbackDto
{
    /// <summary>feedback | trace</summary>
    public string Kind { get; set; } = "trace";
    public int Seq { get; set; }
    public DateTime At { get; set; }
    /// <summary>
    /// feedback: f8 (a compose panel press: Flags and the fields below) | heads-up; older presses: works | wrong-place |
    /// not-advancing | missing-step | note. feedback.capture: capture (Ref = the press). trace: area | quest | objective |
    /// route-reloaded | flag | flag-cleared | npc-dialog | item-gained | item-lost | heads-up-start | door | boss-fight | ...
    /// </summary>
    public string Event { get; set; } = "";
    public string? Note { get; set; }
    public string? Area { get; set; }
    public string? AreaName { get; set; }
    public string? Target { get; set; }
    public string? Label { get; set; }
    public int StepIndex { get; set; } = -1;
    public int[]? PlayerGrid { get; set; }
    /// <summary>quest: "QuestId:StateId text" for each quest that changed.</summary>
    public List<string>? Quests { get; set; }
    /// <summary>feedback only: the plugin's whole state at the press (as WhatsARoute.GetState, without the map path).</summary>
    public RouteStateDto? State { get; set; }
    /// <summary>heads-up only: what changed around what the player did (F7). feedback.capture: the watch-next capture.</summary>
    public HeadsUpDto? Capture { get; set; }

    // ── An F8 press (Event "f8", the compose panel; research/f8-agent-ux.md 2 and 8.2). Null on traces and old events. ──

    /// <summary>What is wrong (2.3 ids): wrong-place, not-advancing, ... something-else, works. Never empty on a press.</summary>
    public List<string>? Flags { get; set; }
    /// <summary>Every suggestion the panel showed, kept by the player or not (a rejected one is information too).</summary>
    public List<SuggestionDto>? Suggested { get; set; }
    /// <summary>step | panel | area | marker | route.</summary>
    public string? Scope { get; set; }
    /// <summary>The open panel's name when Scope is panel ("Inventory", "NPC dialogue (Makoru)").</summary>
    public string? ScopePanel { get; set; }
    /// <summary>Where the player says Claude should look (free text, up to 200).</summary>
    public string? WatchFor { get; set; }
    /// <summary>panel-only | path-ok | watch-next.</summary>
    public List<string>? Hints { get; set; }
    /// <summary>A feedback.capture event with Ref = this press's bridge seq follows (up to ~60 s, wait up to 90 s).</summary>
    public bool? WatchNext { get; set; }
    /// <summary>The verify row: the earlier press this one is about. feedback.capture: the press the capture belongs to.</summary>
    public long? Ref { get; set; }
    /// <summary>The verify row: works | still-wrong.</summary>
    public string? Verdict { get; set; }
}

/// <summary>A thing the plugin noticed at the press (research/f8-agent-ux.md 2.4), shown as a chip.</summary>
public sealed class SuggestionDto
{
    /// <summary>quest-moved | target-missing | blocked | stuck | marker-far | boss | town</summary>
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Why the plugin thinks so, in a line (the chip's hover).</summary>
    public string Reason { get; set; } = "";
    /// <summary>Measured (preselected), not guessed from time.</summary>
    public bool Certain { get; set; }
    /// <summary>On when sent: a Certain one the player switched off goes out with Kept = false.</summary>
    public bool Kept { get; set; }
    /// <summary>The raw readings behind it.</summary>
    public Dictionary<string, object?>? Evidence { get; set; }
}

/// <summary>
/// The local log of the player-agent loop: feedback.jsonl in the live folder (next to route.live.json, outside git). The
/// player's presses and the route's own trace (what changed, when) append one line each. Delivery goes through the AI
/// Bridge (AiBridgeLink: "feedback" and "route" events, claimed by the MCP's dispatcher); this file is the record, and
/// the fallback when the bridge is absent (tools/feedback-wait.sh still waits on it). Rare events, so a synchronous append.
/// </summary>
public sealed class FeedbackLog
{
    private readonly object _lock = new();
    private int _seq;
    public string? Path { get; private set; }
    public string? Why { get; private set; }

    public void Open(string? liveDir)
    {
        Path = string.IsNullOrWhiteSpace(liveDir) ? null : System.IO.Path.Combine(liveDir, "feedback.jsonl");
        if (Path == null) return;
        try
        {
            Directory.CreateDirectory(liveDir!);
            if (_seq == 0 && File.Exists(Path)) foreach (var _ in File.ReadLines(Path)) _seq++;   // seq continues across HUD runs
        }
        catch (Exception ex) { Why = $"feedback log {Path}: {ex.Message}"; }
    }

    /// <summary>Appends <paramref name="dto"/> (Seq and At filled in); returns its Seq, or -1 when the file could not be written.</summary>
    public int Write(FeedbackDto dto)
    {
        if (Path == null) return -1;
        lock (_lock)
        {
            dto.Seq = ++_seq;
            dto.At = DateTime.UtcNow;
            try { File.AppendAllText(Path, JsonConvert.SerializeObject(dto, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }) + "\n"); Why = null; }
            catch (Exception ex) { Why = $"feedback log {Path}: {ex.Message}"; return -1; }
            return dto.Seq;
        }
    }
}
