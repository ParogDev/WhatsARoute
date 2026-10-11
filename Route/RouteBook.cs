using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace WhatsARoute.Route;

/// <summary>One thing to walk to in an area. Target syntax: see <see cref="TargetSpec"/>.</summary>
public sealed class RouteStep
{
    public string Target { get; set; } = "";
    public string? Label { get; set; }
    /// <summary>QuestFlag enum name; the step is done once this flag is set.</summary>
    public string? DoneFlag { get; set; }
    public bool Optional { get; set; }
    public string? Note { get; set; }
    /// <summary>Skip this step while it has nothing to go to (after the filters below).</summary>
    public bool SkipIfNone { get; set; }
    /// <summary>Only goals within this many grid cells of the player.</summary>
    public int? Within { get; set; }
    /// <summary>Entity goals must have this StateMachine state, "name=value" (e.g. "active=1").</summary>
    public string? State { get; set; }
    /// <summary>Drop goals that have a matching entity in this state nearby (a lit seal has an active portal).</summary>
    public ExcludeNearRule? ExcludeNear { get; set; }
    /// <summary>The step applies only while the player stands on a terrain tile matching this pattern (e.g. "*TitanQuest*": a seal platform).</summary>
    public string? PlayerOn { get; set; }
    /// <summary>The step is done for the rest of the visit once this many distinct entities matching it were seen in that state (all three seal portals active).</summary>
    public DoneWhenSeenRule? DoneWhenSeen { get; set; }
    /// <summary>
    /// The step applies only while an item whose metadata path contains this is in your inventory ("VaalKeyGem": a Soul
    /// Core to put in a Stone Altar). The quest state alone can't tell: it stays the same while you carry the next core.
    /// </summary>
    public string? Carrying { get; set; }
    /// <summary>The step is done once a monster whose path contains this is seen dead (a boss): lead to its room, not to it.</summary>
    public string? UntilDead { get; set; }
}

public sealed class DoneWhenSeenRule
{
    /// <summary>Path substring; distinct full paths are counted (AncientSeal1Portal, 2, 3).</summary>
    public string Entity { get; set; } = "";
    public string? State { get; set; }
    public int Count { get; set; } = 1;
}

public sealed class ExcludeNearRule
{
    public string Entity { get; set; } = "";
    public string? State { get; set; }
    public float Radius { get; set; } = 40;
}

/// <summary>
/// Where an active quest state sends you. The first entry (file order = the speedrun order) whose quest is open in
/// one of <see cref="States"/> (any state when null) is the objective: walk <see cref="Target"/> when you are in
/// <see cref="Area"/>, otherwise travel there. Read from IngameUi.UncompletedQuests, so it is stateless.
/// </summary>
public sealed class QuestObjective
{
    public string Quest { get; set; } = "";
    public int[]? States { get; set; }
    /// <summary>Area pattern, or several as "G3_2_1|G3_5" (travel goes to the first while you are in none of them).</summary>
    public string Area { get; set; } = "";
    public string? Target { get; set; }
    public string? Label { get; set; }
    public bool Optional { get; set; }
    public string? Note { get; set; }
    /// <summary>
    /// The way there from areas with no exit to <see cref="Area"/>: current-area pattern to a target spec (from town
    /// "transition:G3_3" rather than a waypoint not unlocked yet). Areas not listed travel as usual.
    /// </summary>
    public Dictionary<string, string>? Via { get; set; }
}

public sealed class RouteFile
{
    public string? Name { get; set; }
    public string? Source { get; set; }
    public List<QuestObjective> Quests { get; set; } = new();
    /// <summary>Area RawName (Radar-style pattern, * and ?) to its ordered steps.</summary>
    public Dictionary<string, List<RouteStep>> Areas { get; set; } = new();
    /// <summary>Live file only: a line for the player, shown under the card when the file changes (the agent's reply).</summary>
    public string? Message { get; set; }
    /// <summary>Reward items to use from the inventory (The Flame Core): while one is carried, using it is the objective, anywhere.</summary>
    public List<UseItem> Use { get; set; } = new();
}

/// <summary>An item to right-click in the inventory once you have it (a permanent buff), by metadata path part.</summary>
public sealed class UseItem
{
    public string Item { get; set; } = "";
    public string? Label { get; set; }
    public string? Note { get; set; }
}

/// <summary>Radar's targets.json row (read from the sibling Radar plugin when installed; never copied).</summary>
public sealed class RadarTarget
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? TargetType { get; set; }
    public string[]? Rooms { get; set; }
    public RadarTarget[]? Alternatives { get; set; }
}

/// <summary>
/// The routes: our own route.json (ordered steps per area), plus Radar's targets.json as a dictionary of named
/// places per area ("radar:The Halani Gates"). Loaded on start, on the Reload button, and whenever route.json or the
/// live file is written. The live file (route.live.json in the live folder, outside git) is laid over route.json: its
/// areas replace route.json's areas of the same key and its quests come first. The agent edits it while you play; the
/// fixes move into route.json by PR afterwards.
/// </summary>
public sealed class RouteBook
{
    private static readonly ConcurrentDictionary<string, Regex> LikeCache = new();

    public RouteFile Route { get; private set; } = new();
    public Dictionary<string, List<RadarTarget>> Radar { get; private set; } = new();
    public string? RouteError { get; private set; }
    public string? RadarError { get; private set; }
    public string? RadarPath { get; private set; }
    public string? LivePath { get; private set; }
    public string? LiveError { get; private set; }
    /// <summary>The live file's Message at the last load (null without one).</summary>
    public string? LiveMessage { get; private set; }
    public int LiveAreas { get; private set; }
    public int LiveQuests { get; private set; }

    private string _routePath = "";
    private DateTime _routeStamp, _liveStamp;

    /// <summary>True when route.json or the live file was written since the last load (two stat calls).</summary>
    public bool Changed() => Stamp(_routePath) != _routeStamp || (LivePath != null && Stamp(LivePath) != _liveStamp);

    private static DateTime Stamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    public void Load(string pluginDir, string? liveDir = null)
    {
        LoadRoute(pluginDir);
        LoadLive(liveDir);
        LoadRadar(pluginDir);
    }

    private void LoadLive(string? liveDir)
    {
        LiveError = null; LiveMessage = null; LiveAreas = 0; LiveQuests = 0;
        LivePath = string.IsNullOrWhiteSpace(liveDir) ? null : Path.Combine(liveDir, "route.live.json");
        if (LivePath == null) return;
        _liveStamp = Stamp(LivePath);
        if (!File.Exists(LivePath)) return;
        RouteFile? live;
        try { live = JsonConvert.DeserializeObject<RouteFile>(File.ReadAllText(LivePath)); }
        catch (Exception ex) { LiveError = $"route.live.json: {ex.Message}"; return; }   // route.json alone still routes
        if (live == null) return;
        foreach (var (key, steps) in live.Areas) Route.Areas[key] = steps;
        Route.Quests.InsertRange(0, live.Quests);
        Route.Use.InsertRange(0, live.Use);
        LiveAreas = live.Areas.Count;
        LiveQuests = live.Quests.Count;
        LiveMessage = live.Message;
    }

    private void LoadRoute(string pluginDir)
    {
        var routePath = _routePath = Path.Combine(pluginDir, "route.json");
        _routeStamp = Stamp(routePath);
        try
        {
            Route = File.Exists(routePath) ? JsonConvert.DeserializeObject<RouteFile>(File.ReadAllText(routePath)) ?? new RouteFile() : new RouteFile();
            RouteError = File.Exists(routePath) ? null : $"{routePath} not found: Route mode uses the fallback target everywhere";
        }
        catch (Exception ex)
        {
            Route = new RouteFile();
            RouteError = $"route.json: {ex.Message}";
        }
    }

    private void LoadRadar(string pluginDir)
    {
        RadarPath = Path.GetFullPath(Path.Combine(pluginDir, "..", "Radar", "targets.json"));
        try
        {
            Radar = File.Exists(RadarPath)
                ? JsonConvert.DeserializeObject<Dictionary<string, List<RadarTarget>>>(File.ReadAllText(RadarPath)) ?? new()
                : new();
            RadarError = File.Exists(RadarPath) ? null : $"{RadarPath} not found: radar: targets cannot resolve";
        }
        catch (Exception ex)
        {
            Radar = new();
            RadarError = $"Radar targets.json: {ex.Message}";
        }
    }

    public List<RouteStep> StepsFor(string areaRawName)
    {
        foreach (var (pattern, steps) in Route.Areas)
            if (Like(areaRawName, pattern)) return steps;
        return [];
    }

    public IEnumerable<RadarTarget> RadarTargetsFor(string areaRawName) =>
        Radar.Where(kv => Like(areaRawName, kv.Key)).SelectMany(kv => kv.Value);

    /// <summary><see cref="Like"/> against any of "a|b|c" (a quest objective whose area steps span several areas).</summary>
    public static bool LikeAny(string? s, string patterns)
    {
        if (!patterns.Contains('|')) return Like(s, patterns);
        foreach (var p in patterns.Split('|'))
            if (Like(s, p)) return true;
        return false;
    }

    /// <summary>The first of "a|b|c": where travel goes when you are in none of them.</summary>
    public static string FirstOf(string patterns) => patterns.Split('|')[0];

    public static bool Like(string? s, string pattern) =>
        s != null && LikeCache.GetOrAdd(pattern, p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)).IsMatch(s);
}
