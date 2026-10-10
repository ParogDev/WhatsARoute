using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
/// <item><c>transition</c>, <c>transition:Halani</c> or <c>transition:G2_4_1</c>: usable area transitions, optionally by
/// name part or destination area id; with none open, a travel table (Act 2 caravan Desert Map) instead</item>
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
    private readonly Dictionary<string, string> _areaNames = new(StringComparer.OrdinalIgnoreCase);   // WorldAreas id -> name (a ~600-row scan otherwise)
    private Dictionary<(int, int), IngameData.Tile>? _tileIndex;   // tile origin (cell / TileCells) -> tile
    private string _currentArea = "";   // the area Resolve was last called for (entity scans record sightings under it)
    public const int TileCells = 23;   // grid cells per terrain tile edge (tile Coordinates step by 23)

    /// <summary>Places seen in this area instance; goals fall back to them once their entity has unloaded.</summary>
    public Sightings Sightings { get; } = new();
    /// <summary>The plugin's clock, for the sightings' write debounce.</summary>
    public double Now;

    public void ResetArea() { _tiles = null; _tileIndex = null; _areaKey = ""; _tileMatches.Clear(); }

    /// <summary>The terrain tile under a grid cell (tiles are known for the whole area on entry), or null off the map.</summary>
    public IngameData.Tile? TileAt(GameController gc, string areaRawName, (int x, int y) cell)
    {
        if (_tiles == null || _areaKey != areaRawName)
        {
            _tiles = gc.IngameState.Data.Tiles;
            _areaKey = areaRawName;
            _tileMatches.Clear();
            _tileIndex = null;
        }
        if (_tiles == null) return null;
        if (_tileIndex == null)
        {
            _tileIndex = new Dictionary<(int, int), IngameData.Tile>(_tiles.Count);
            foreach (var t in _tiles) _tileIndex[(t.Coordinate.X / TileCells, t.Coordinate.Y / TileCells)] = t;
        }
        return cell.x >= 0 && cell.y >= 0 && _tileIndex.TryGetValue((cell.x / TileCells, cell.y / TileCells), out var tile) ? tile : null;
    }

    /// <summary>A goal that is an entity: where it stands and how big it is, for the in-world marker.</summary>
    public readonly record struct GoalEntity(int X, int Y, Entity Entity, Render? Render, Vector3 Pos, Vector3 Bounds);

    public sealed class Result
    {
        public readonly List<(int x, int y)> Goals = new();
        public readonly List<GoalEntity> GoalEntities = new();   // one per goal when the goals are entities (same order)
        public string Label = "";
        public string? Why;   // the broken link, when there are no goals
        public bool FromEntities;
        public bool FromMemory;       // the goals are remembered sightings: their entities are not loaded right now
        public string? TravelTo;      // area id when the objective is in another area
        public bool ViaTravelMap;     // the way there is the Desert Map: highlight its stop when the map opens
    }

    /// <summary>
    /// <paramref name="spec"/> may list fallbacks, "a|b|c": the first that finds goals wins, so one stale data source
    /// (e.g. a Radar tile that no longer exists in this layout) doesn't break the step. With none, every reason is kept.
    /// </summary>
    public void Resolve(GameController gc, RouteBook book, string spec, string areaRawName, Result r)
    {
        _currentArea = areaRawName;
        if (!spec.Contains('|')) { ResolveOne(gc, book, spec, areaRawName, r); return; }
        var whys = new List<string>();
        foreach (var part in spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ResolveOne(gc, book, part, areaRawName, r);
            if (r.Goals.Count > 0) return;
            if (r.Why != null) whys.Add(r.Why);
        }
        r.Why = string.Join("; ", whys);
    }

    private void ResolveOne(GameController gc, RouteBook book, string spec, string areaRawName, Result r)
    {
        r.Goals.Clear();
        r.GoalEntities.Clear();
        r.Why = null;
        r.FromEntities = false;
        r.FromMemory = false;
        r.TravelTo = null;
        r.ViaTravelMap = false;
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
                Entities(gc, r, EntityType.Waypoint, _ => true);
                break;
            case "transition":
                r.Label = arg.Length > 0 ? arg : "Exit";
                // Only exits you can use now: the Act 2 caravan keeps one exit per destination at its doors, all
                // untargetable while it travels; locked doors are untargetable too. Matches the name or area id.
                Entities(gc, r, EntityType.AreaTransition, e => IsTargetable(e) &&
                                     (arg.Length == 0 || e.RenderName?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true ||
                                      string.Equals(e.GetComponent<AreaTransition>()?.WorldArea?.Id, arg, StringComparison.OrdinalIgnoreCase)));
                if (r.Goals.Count == 0)
                {
                    // No open exit: a travel table (the caravan's Desert Map) is the way on.
                    Entities(gc, r, e => IsTargetable(e) && e.Path?.EndsWith("/Objects/MapTable", StringComparison.Ordinal) == true);
                    if (r.Goals.Count > 0) r.Label = arg.Length > 0 ? $"Desert Map: travel to {arg}" : "Desert Map: choose where to go";
                }
                // Seen earlier in this visit but unloaded now (you walked away): its place is still good.
                if (r.Goals.Count == 0)
                    Remembered(r, s => s.Type == nameof(EntityType.AreaTransition) && s.Targetable &&
                                       (arg.Length == 0 || s.Name?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true || string.Equals(s.DestArea, arg, StringComparison.OrdinalIgnoreCase)));
                break;
            case "travel":
            {
                // Another area: an open exit that leads there, else the caravan's Desert Map, else a waypoint/checkpoint.
                if (!_areaNames.TryGetValue(arg, out var name))
                    _areaNames[arg] = name = gc.Files.WorldAreas.EntriesList.FirstOrDefault(a => string.Equals(a.Id, arg, StringComparison.OrdinalIgnoreCase))?.Name ?? arg;
                r.TravelTo = arg;
                r.Label = $"Exit to {name}";
                Entities(gc, r, EntityType.AreaTransition, e => IsTargetable(e) &&
                                     string.Equals(e.GetComponent<AreaTransition>()?.WorldArea?.Id, arg, StringComparison.OrdinalIgnoreCase));
                if (r.Goals.Count > 0) break;
                Remembered(r, s => s.Type == nameof(EntityType.AreaTransition) && s.Targetable && string.Equals(s.DestArea, arg, StringComparison.OrdinalIgnoreCase));
                if (r.Goals.Count > 0) break;
                Entities(gc, r, e => IsTargetable(e) && e.Path?.EndsWith("/Objects/MapTable", StringComparison.Ordinal) == true);
                if (r.Goals.Count > 0) { r.Label = $"Desert Map: travel to {name}"; r.ViaTravelMap = true; break; }
                Entities(gc, r, e => e.Type == EntityType.Waypoint || e.Path?.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase) == true);
                if (r.Goals.Count > 0) r.Label = $"Waypoint: travel to {name}";
                else r.Why = $"no exit to {arg}, no Desert Map and no waypoint/checkpoint loaded near you";
                break;
            }
            case "entity":
                r.Label = arg.Split('/').LastOrDefault() ?? arg;
                Entities(gc, r, e => e.Path?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true);
                if (r.Goals.Count == 0) Remembered(r, s => s.Path.Contains(arg, StringComparison.OrdinalIgnoreCase));
                break;
            case "click":
                // An entity you can click right now (quest objects turn targetable one after another).
                r.Label = arg.Split('/').LastOrDefault() ?? arg;
                Entities(gc, r, e => IsTargetable(e) && e.Path?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true);
                if (r.Goals.Count == 0) Remembered(r, s => s.Targetable && s.Path.Contains(arg, StringComparison.OrdinalIgnoreCase));
                break;
            case "tile":
                r.Label = arg;
                Tiles(gc, areaRawName, arg, r);
                break;
            case "radar":
            {
                r.Label = arg;
                // Every row with that name: Radar lists repeated places separately (G2_6 has three "Ancient Seal").
                var found = false;
                foreach (var t in book.RadarTargetsFor(areaRawName))
                {
                    if (!string.Equals(t.DisplayName, arg, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    var before = r.Goals.Count;
                    foreach (var alt in (t.Alternatives ?? []).Prepend(t))
                    {
                        if (string.Equals(t.TargetType, "Entity", StringComparison.OrdinalIgnoreCase))
                            Entities(gc, r, e => RouteBook.Like(e.Path, alt.Name));
                        else
                            Tiles(gc, areaRawName, alt.Name, r);
                        if (r.Goals.Count > before) break;
                    }
                }
                if (!found) { r.Why = book.RadarError ?? $"Radar targets.json has no '{arg}' for area {areaRawName}"; return; }
                if (!r.FromEntities && r.Goals.Count > 0) ExitOnPlace(gc, r, arg, areaRawName);
                break;
            }
            default:
                r.Why = $"unknown target kind '{kind}' in '{spec}' (checkpoint | waypoint | transition | tile: | radar: | entity:)";
                return;
        }
        if (r.Goals.Count == 0 && r.Why == null)
            r.Why = r.FromEntities ? $"no '{spec}' loaded near you yet (entities only exist once the game sends them)" : $"no tile matches '{spec}' in {areaRawName}";
    }

    /// <summary>
    /// A Radar place made of tiles can be big (G2_4_1 "The Lost City" is Keth_Stairs_RuinEntrance, 81 tiles): its
    /// goals are tile corners, and the route ended at the nearest one, by the checkpoint. When an exit is part of the
    /// place (named like it, or standing on one of its tiles), that exit is the goal, loaded or seen earlier.
    /// </summary>
    private void ExitOnPlace(GameController gc, Result r, string name, string areaRawName)
    {
        var tiles = new HashSet<(int, int)>(r.Goals);
        bool OnPlace(string? n, (int, int) tile) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || tiles.Contains(tile);
        var exit = new Result { Label = r.Label };
        Entities(gc, exit, EntityType.AreaTransition, e => IsTargetable(e) &&
            OnPlace(e.RenderName, TileAt(gc, areaRawName, CellOf(e)) is { } t ? (t.Coordinate.X, t.Coordinate.Y) : (-1, -1)));
        if (exit.Goals.Count == 0)
            Remembered(exit, s => s.Type == nameof(EntityType.AreaTransition) && s.Targetable && OnPlace(s.Name, (s.TileX, s.TileY)));
        if (exit.Goals.Count == 0) return;
        r.Goals.Clear();
        r.Goals.AddRange(exit.Goals);
        r.GoalEntities.Clear();
        r.GoalEntities.AddRange(exit.GoalEntities);
        r.FromEntities = true;
        r.FromMemory = exit.FromMemory;
        r.Label = exit.Label;
    }

    private static (int x, int y) CellOf(Entity e) =>
        e.GetComponent<Positioned>() is { } p ? ((int)p.GridPos.X, (int)p.GridPos.Y) : (-1, -1);

    private void Entities(GameController gc, Result r, Func<Entity, bool> match) => Entities(gc, r, null, match);

    /// <summary>Goals from this visit's sightings (the entity unloaded when you walked away). Their place is still right.</summary>
    private void Remembered(Result r, Func<SightingDto, bool> match)
    {
        var n = 0;
        foreach (var s in Sightings.Area)
        {
            if (!match(s)) continue;
            r.Goals.Add((s.X, s.Y));
            n++;
        }
        if (n == 0) return;
        r.FromMemory = true;
        r.Label += " (seen earlier)";
    }

    /// <summary>
    /// With a type, only the HUD's per-type list is walked (measured in the caravan, 513 entities: 7.2 us / 1.7 KB vs
    /// 16 us / 5.9 KB for all entities; the full scan grows with every monster and effect on a busy map, the list only
    /// with what we want). Without one (metadata-path matches), every valid entity.
    /// </summary>
    private void Entities(GameController gc, Result r, EntityType? type, Func<Entity, bool> match)
    {
        r.FromEntities = true;
        var area = _currentArea;
        IEnumerable<Entity> source = type is { } t && gc.EntityListWrapper.ValidEntitiesByType.TryGetValue(t, out var list) ? list : gc.Entities;
        foreach (var e in source)
        {
            if (!e.IsValid || !match(e)) continue;
            var rd = e.GetComponent<Render>();
            (int, int) cell;
            if (rd != null)
            {
                var pos = rd.Pos;
                cell = ((int)(pos.X / AreaGrid.GridToWorld), (int)(pos.Y / AreaGrid.GridToWorld));
                r.GoalEntities.Add(new GoalEntity(cell.Item1, cell.Item2, e, rd, pos, rd.Bounds));
            }
            else if (e.GetComponent<Positioned>() is { } p)
            {
                // Markers without a model (e.g. G2_6 TitanGrottoPointOfInterest: Positioned + ProximityTrigger only):
                // a goal for the path, no box for the in-world marker.
                var gp = p.GridPos;
                cell = ((int)gp.X, (int)gp.Y);
                r.GoalEntities.Add(new GoalEntity(cell.Item1, cell.Item2, e, null, new Vector3(gp.X * AreaGrid.GridToWorld, gp.Y * AreaGrid.GridToWorld, 0), Vector3.Zero));
            }
            else continue;
            r.Goals.Add(cell);
            Sightings.Note(e, c => TileAt(gc, area, c), Now);   // a goal keeps its place after it unloads
        }
    }

    private static bool IsTargetable(Entity e) => e.GetComponent<Targetable>() is not { } t || t.isTargetable;

    private void Tiles(GameController gc, string areaRawName, string pattern, Result r)
    {
        if (_tiles == null || _areaKey != areaRawName)
        {
            _tiles = gc.IngameState.Data.Tiles;
            _areaKey = areaRawName;
            _tileMatches.Clear();
        }
        if (_tiles == null) { r.Why = "IngameData.Tiles is null"; return; }
        if (!pattern.Contains('*') && !pattern.Contains('?')) pattern = "*" + pattern + "*";
        // Tiles don't change within an area: match each pattern once (a G2_6 scan of 10,989 tiles x 2 regexes was
        // ~1.9 ms, twice a second, mostly for a Radar fallback that matched nothing).
        if (!_tileMatches.TryGetValue(pattern, out var cells))
        {
            cells = new List<(int, int)>();
            foreach (var t in _tiles)
                if (RouteBook.Like(t.Name, pattern) || RouteBook.Like(t.Path, pattern))
                    cells.Add((t.Coordinate.X, t.Coordinate.Y));
            _tileMatches[pattern] = cells;
        }
        r.Goals.AddRange(cells);
    }

    private readonly Dictionary<string, List<(int x, int y)>> _tileMatches = new();
}
