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
    /// feedback: works | wrong-place | not-advancing | missing-step | note | heads-up. trace: area | quest | objective |
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
    /// <summary>heads-up only: what changed around what the player did (F7).</summary>
    public HeadsUpDto? Capture { get; set; }
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
