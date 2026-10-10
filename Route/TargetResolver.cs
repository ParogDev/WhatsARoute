using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using WhatsARoute.Navigation;

namespace WhatsARoute.Route;

/// <summary>
/// Target syntax (route.json "target", or the manual target setting):
/// <list type="bullet">
/// <item><c>checkpoint</c>, <c>waypoint</c>: the nearest one loaded</item>
/// <item><c>transition</c> or <c>transition:Halani</c>: area transitions, optionally by name part</item>
/// <item><c>tile:*Thickwall_Cave_Entrance*</c>: terrain tiles by name or .tdt path (known for the whole map on entry)</item>
/// <item><c>radar:The Halani Gates</c>: a named place from Radar's targets.json for this area</item>
/// <item><c>entity:Metadata/QuestObjects/...</c>: entities whose metadata path contains this</item>
/// </list>
/// Several matches become several goals: the flow field then leads to the nearest reachable one.
/// </summary>
public sealed class TargetResolver
{
    private List<IngameData.Tile>? _tiles;
    private string _areaKey = "";

    public void ResetArea() { _tiles = null; _areaKey = ""; }

    public sealed class Result
    {
        public readonly List<(int x, int y)> Goals = new();
        public string Label = "";
        public string? Why;   // the broken link, when there are no goals
        public bool FromEntities;
    }

    public void Resolve(GameController gc, RouteBook book, string spec, string areaRawName, Result r)
    {
        r.Goals.Clear();
        r.Why = null;
        r.FromEntities = false;
        spec = spec.Trim();
        var colon = spec.IndexOf(':');
        var kind = (colon < 0 ? spec : spec[..colon]).Trim().ToLowerInvariant();
        var arg = colon < 0 ? "" : spec[(colon + 1)..].Trim();
        switch (kind)
        {
            case "checkpoint":
                r.Label = "Checkpoint";
                Entities(gc, r, e => e.Path?.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase) == true);
                break;
            case "waypoint":
                r.Label = "Waypoint";
                Entities(gc, r, e => e.Type == EntityType.Waypoint);
                break;
            case "transition":
                r.Label = arg.Length > 0 ? arg : "Exit";
                Entities(gc, r, e => e.Type == EntityType.AreaTransition &&
                                     (arg.Length == 0 || e.RenderName?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true));
                break;
            case "entity":
                r.Label = arg.Split('/').LastOrDefault() ?? arg;
                Entities(gc, r, e => e.Path?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true);
                break;
            case "tile":
                r.Label = arg;
                Tiles(gc, areaRawName, arg, r);
                break;
            case "radar":
            {
                r.Label = arg;
                var t = book.RadarTargetsFor(areaRawName).FirstOrDefault(x => string.Equals(x.DisplayName, arg, StringComparison.OrdinalIgnoreCase));
                if (t == null)
                {
                    r.Why = book.RadarError ?? $"Radar targets.json has no '{arg}' for area {areaRawName}";
                    return;
                }
                foreach (var alt in (t.Alternatives ?? []).Prepend(t))
                {
                    if (string.Equals(t.TargetType, "Entity", StringComparison.OrdinalIgnoreCase))
                        Entities(gc, r, e => RouteBook.Like(e.Path, alt.Name));
                    else
                        Tiles(gc, areaRawName, alt.Name, r);
                    if (r.Goals.Count > 0) break;
                }
                break;
            }
            default:
                r.Why = $"unknown target kind '{kind}' in '{spec}' (checkpoint | waypoint | transition | tile: | radar: | entity:)";
                return;
        }
        if (r.Goals.Count == 0 && r.Why == null)
            r.Why = r.FromEntities ? $"no '{spec}' loaded near you yet (entities only exist once the game sends them)" : $"no tile matches '{spec}' in {areaRawName}";
    }

    private static void Entities(GameController gc, Result r, Func<Entity, bool> match)
    {
        r.FromEntities = true;
        foreach (var e in gc.Entities)
        {
            if (!e.IsValid || !match(e)) continue;
            var rd = e.GetComponent<Render>();
            if (rd == null) continue;
            r.Goals.Add(((int)(rd.Pos.X / AreaGrid.GridToWorld), (int)(rd.Pos.Y / AreaGrid.GridToWorld)));
        }
    }

    private void Tiles(GameController gc, string areaRawName, string pattern, Result r)
    {
        if (_tiles == null || _areaKey != areaRawName)
        {
            _tiles = gc.IngameState.Data.Tiles;
            _areaKey = areaRawName;
        }
        if (_tiles == null) { r.Why = "IngameData.Tiles is null"; return; }
        if (!pattern.Contains('*') && !pattern.Contains('?')) pattern = "*" + pattern + "*";
        foreach (var t in _tiles)
            if (RouteBook.Like(t.Name, pattern) || RouteBook.Like(t.Path, pattern))
                r.Goals.Add((t.Coordinate.X, t.Coordinate.Y));
    }
}
