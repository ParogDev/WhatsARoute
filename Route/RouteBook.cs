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
}

public sealed class RouteFile
{
    public string? Name { get; set; }
    public string? Source { get; set; }
    /// <summary>Area RawName (Radar-style pattern, * and ?) to its ordered steps.</summary>
    public Dictionary<string, List<RouteStep>> Areas { get; set; } = new();
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
/// places per area ("radar:The Halani Gates"). Loaded on start and on the Reload button.
/// </summary>
public sealed class RouteBook
{
    private static readonly ConcurrentDictionary<string, Regex> LikeCache = new();

    public RouteFile Route { get; private set; } = new();
    public Dictionary<string, List<RadarTarget>> Radar { get; private set; } = new();
    public string? RouteError { get; private set; }
    public string? RadarError { get; private set; }
    public string? RadarPath { get; private set; }

    public void Load(string pluginDir)
    {
        var routePath = Path.Combine(pluginDir, "route.json");
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

    public static bool Like(string? s, string pattern) =>
        s != null && LikeCache.GetOrAdd(pattern, p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)).IsMatch(s);
}
