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
/// <item><c>click:...</c>: the same, only while you can click it (targetable)</item>
/// <item><c>item:EssenceOfTheWaterGod</c>: an item on the ground, by the item's metadata path or base name</item>
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

    /// <summary>WorldAreas id to display name ("G3_3" -> "Jungle Ruins"), cached; the id itself when unknown.</summary>
    private string AreaName(GameController gc, string id)
    {
        if (!_areaNames.TryGetValue(id, out var name))
            _areaNames[id] = name = gc.Files.WorldAreas.EntriesList.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;
        return name;
    }

    /// <summary>
    /// The exit to <paramref name="destName"/> from Radar's places in this area (named after the destination, "The"
    /// optional), resolved into <paramref name="r"/>. For exits not loaded yet: the layout knows their tile on entry.
    /// </summary>
    private bool ExitFromRadar(GameController gc, RouteBook book, string areaRawName, string destName, Result r)
    {
        static string Bare(string s) => s.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
        var want = Bare(destName);
        var place = book.RadarTargetsFor(areaRawName).FirstOrDefault(t => t.DisplayName is { } d && string.Equals(Bare(d), want, StringComparison.OrdinalIgnoreCase));
        if (place?.DisplayName == null) return false;
        Resolve(gc, book, "radar:" + place.DisplayName, areaRawName, r);
        if (r.Goals.Count > 0) return true;
        r.Why = null;   // the caller's later fallbacks (Desert Map, portal) speak for themselves
        return false;
    }

    /// <summary>The Act 2 caravan's trip (set by the plugin; updated before each resolve).</summary>
    public CaravanState? Caravan;

    /// <summary>
    /// The caravan was sent to a stop and its gates are not open yet: nothing to walk to (the Desert Map would only
    /// send it again). The card says where it is heading; no goals, and not "nothing loaded yet", so the last route
    /// (to the Desert Map) is dropped. Heading elsewhere than <paramref name="dest"/> whose waypoint this character
    /// unlocked: the waypoint gets you there now, so it is the goal instead of the wait.
    /// </summary>
    private bool CaravanTravelling(GameController gc, Result r, string dest)
    {
        if (Caravan?.Heading is not { } h) return false;
        if (dest.Length > 0 && !string.Equals(h, dest, StringComparison.OrdinalIgnoreCase) && Waypoints?.Unlocked(dest) == true)
        {
            Entities(gc, r, EntityType.Waypoint, _ => true);
            if (r.Goals.Count > 0) { r.Label = $"Waypoint: travel to {AreaName(gc, dest)}"; r.Via = "the waypoint"; return true; }
        }
        r.Heading = h;
        r.HeadingName = h.Length > 0 ? AreaName(gc, h) : "a new stop";
        r.Label = $"Caravan heading to {r.HeadingName}: wait for the gates";
        r.FromEntities = false;
        return true;
    }

    /// <summary>The Desert Map in the Act 2 caravan (G2_town Objects/MapTable).</summary>
    private static bool IsMapTable(Entity e) => e.Path?.EndsWith("/Objects/MapTable", StringComparison.Ordinal) == true;

    /// <summary>Destinations the caravan serves (Desert Map stops seen in the Travel view), from <see cref="WorldMapReader"/>.</summary>
    public IReadOnlySet<string>? CaravanStops;
    /// <summary>Waypoints this character has unlocked (seen lit in the Teleport view), from <see cref="WaypointMemory"/>.</summary>
    public WaypointMemory? Waypoints;

    /// <summary>
    /// In the caravan with its Desert Map goals in <paramref name="r"/>: both the Desert Map and the waypoint are ways
    /// out. The Desert Map when <paramref name="dest"/> is a caravan stop (a Travel-view stop serves it, or one of the
    /// caravan's own exits leads there), the waypoint when this character unlocked the destination's waypoint, both
    /// when both hold (the route leads to the nearer). Neither known yet: the Desert Map, as before.
    /// </summary>
    private void TravelFromCaravan(GameController gc, string dest, string name, Result r)
    {
        var stop = CaravanStops?.Contains(dest) == true || HasExitTo(gc, dest);
        var wp = Waypoints?.Unlocked(dest) == true;
        var tables = r.Goals.Count;
        if (wp) Entities(gc, r, EntityType.Waypoint, _ => true);
        var wpGoals = r.Goals.Count > tables;
        if (wpGoals && !stop)
        {
            r.Goals.RemoveRange(0, tables);
            r.GoalEntities.RemoveRange(0, Math.Min(tables, r.GoalEntities.Count));
            r.Label = $"Waypoint: travel to {name}";
            r.Via = "the waypoint";
            return;
        }
        r.ViaTravelMap = true;
        r.Label = wpGoals ? $"Desert Map or waypoint: travel to {name}" : $"Desert Map: travel to {name}";
        r.Via = wpGoals ? "the Desert Map or the waypoint" : "the Desert Map";
    }

    /// <summary>One of the caravan's exits leads to <paramref name="dest"/>, targetable or not (they open when the caravan stops there).</summary>
    private static bool HasExitTo(GameController gc, string dest)
    {
        if (!gc.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.AreaTransition, out var list)) return false;
        foreach (var e in list)
            if (e.IsValid && string.Equals(e.GetComponent<AreaTransition>()?.WorldArea?.Id, dest, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

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
        public bool ViaPortal;        // the way there starts with a town portal (the card names the player's portal key)
        public string? Via;           // what to take here for TravelTo, in the card's words ("the Desert Map", "the waypoint")
        public string? Heading;       // Act 2: the caravan is travelling to this area id; nothing to walk to until it stops
        public string? HeadingName;
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
            if (r.Goals.Count > 0 || r.Heading != null) return;   // the caravan travelling is an answer too: nothing to walk to
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
        r.ViaPortal = false;
        r.Via = null;
        r.Heading = null;
        r.HeadingName = null;
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
            {
                // An area id ("G3_3") reads as its name on the card: "Exit to Jungle Ruins".
                var areaName = arg.Length > 0 ? AreaName(gc, arg) : "";
                r.Label = arg.Length == 0 ? "Exit" : areaName != arg ? $"Exit to {areaName}" : arg;
                // Only exits you can use now: the Act 2 caravan keeps one exit per destination at its doors, all
                // untargetable while it travels; locked doors are untargetable too. Matches the name or area id.
                Entities(gc, r, EntityType.AreaTransition, e => IsTargetable(e) &&
                                     (arg.Length == 0 || e.RenderName?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true ||
                                      string.Equals(e.GetComponent<AreaTransition>()?.WorldArea?.Id, arg, StringComparison.OrdinalIgnoreCase)));
                if (r.Goals.Count == 0 && CaravanTravelling(gc, r, "")) return;
                if (r.Goals.Count == 0)
                {
                    // No open exit: a travel table (the caravan's Desert Map) is the way on.
                    Entities(gc, r, e => IsTargetable(e) && e.Path?.EndsWith("/Objects/MapTable", StringComparison.Ordinal) == true);
                    if (r.Goals.Count > 0) r.Label = arg.Length > 0 ? $"Desert Map: travel to {areaName}" : "Desert Map: choose where to go";
                }
                // Seen earlier in this visit but unloaded now (you walked away): its place is still good.
                if (r.Goals.Count == 0)
                    Remembered(r, s => s.Type == nameof(EntityType.AreaTransition) && s.Targetable &&
                                       (arg.Length == 0 || s.Name?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true || string.Equals(s.DestArea, arg, StringComparison.OrdinalIgnoreCase)));
                // Not loaded and not seen yet (just arrived): Radar's place of that name in this area's layout.
                if (r.Goals.Count == 0 && arg.Length > 0 && ExitFromRadar(gc, book, areaRawName, areaName, r)) r.Label = $"Exit to {areaName}";
                break;
            }
            case "travel":
            {
                // Another area: an open exit that leads there, else Radar's place for that exit (not loaded yet), else
                // the caravan's Desert Map, else a waypoint/checkpoint.
                var name = AreaName(gc, arg);
                r.TravelTo = arg;
                r.Label = $"Exit to {name}";
                Entities(gc, r, EntityType.AreaTransition, e => IsTargetable(e) &&
                                     string.Equals(e.GetComponent<AreaTransition>()?.WorldArea?.Id, arg, StringComparison.OrdinalIgnoreCase));
                if (r.Goals.Count > 0) break;
                if (CaravanTravelling(gc, r, arg)) return;
                Remembered(r, s => s.Type == nameof(EntityType.AreaTransition) && s.Targetable && string.Equals(s.DestArea, arg, StringComparison.OrdinalIgnoreCase));
                if (r.Goals.Count > 0) break;
                // 2026-10-11 Jungle Ruins -> Venom Crypts: the exit had not loaded on arrival, and the card said "open a
                // portal" with no waypoint unlocked. Radar knows the exit's tile from the layout.
                if (ExitFromRadar(gc, book, areaRawName, name, r)) { r.TravelTo = arg; r.Label = $"Exit to {name}"; break; }
                Entities(gc, r, e => IsTargetable(e) && IsMapTable(e));
                if (r.Goals.Count > 0) { TravelFromCaravan(gc, arg, name, r); break; }
                // No exit leads there and no Desert Map here: the way on starts in town (the caravan's Desert Map). Out in
                // the field a portal gets you there from anywhere, so no route is drawn (walking to a waypoint is the slow
                // way the user did not want, 2026-10-10 Buried Shrines -> Mastodon Badlands): the card names the portal key.
                if (gc.Area.CurrentArea is { IsTown: false, IsHideout: false })
                {
                    r.ViaPortal = true;
                    r.Label = $"Travel to {name}";
                    r.FromEntities = false;   // not "nothing loaded yet": the last route (to a waypoint) must not be kept
                    return;
                }
                Entities(gc, r, e => e.Type == EntityType.Waypoint || e.Path?.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase) == true);
                if (r.Goals.Count > 0) { r.Label = $"Waypoint: travel to {name}"; r.Via = "the waypoint"; break; }
                // Nothing loaded near you (a boss arena): a waypoint or checkpoint seen earlier this visit, else any exit.
                Remembered(r, s => s.Type == nameof(EntityType.Waypoint) || s.Path.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase));
                if (r.Goals.Count > 0) { r.Label = $"Waypoint: travel to {name} (seen earlier)"; r.Via = "the waypoint"; break; }
                Entities(gc, r, EntityType.AreaTransition, IsTargetable);
                if (r.Goals.Count == 0) Remembered(r, s => s.Type == nameof(EntityType.AreaTransition) && s.Targetable);
                if (r.Goals.Count > 0) r.Label = $"Exit, then travel to {name}";
                else r.Why = $"no exit to {arg}, no Desert Map, and no waypoint, checkpoint or exit loaded or seen this visit";
                break;
            }
            case "entity":
                r.Label = arg.Split('/').LastOrDefault() ?? arg;
                Entities(gc, r, e => e.Path?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true, MayBeMonster(arg));
                if (r.Goals.Count == 0) Remembered(r, s => s.Path.Contains(arg, StringComparison.OrdinalIgnoreCase));
                break;
            case "click":
                // An entity you can click right now (quest objects turn targetable one after another).
                r.Label = arg.Split('/').LastOrDefault() ?? arg;
                Entities(gc, r, e => IsTargetable(e) && e.Path?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true, MayBeMonster(arg));
                if (r.Goals.Count == 0) Remembered(r, s => s.Targetable && s.Path.Contains(arg, StringComparison.OrdinalIgnoreCase));
                break;
            case "item":
                // An item on the ground (a quest item dropped by an event): the WorldItem entity's path is generic, so
                // match the item inside it, by metadata or base name.
                r.Label = arg;
                Entities(gc, r, EntityType.WorldItem, e => e.GetComponent<WorldItem>()?.ItemEntity is { } it &&
                    (it.Path?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true ||
                     it.GetComponent<Base>()?.Name?.Contains(arg, StringComparison.OrdinalIgnoreCase) == true));
                break;
            case "tile":
                r.Label = arg;
                Tiles(gc, areaRawName, arg, r);
                break;
            case "radar":
            {
                r.Label = arg;
                // Every row with that name: Radar lists repeated places separately (G2_6 has three "Ancient Seal").
                // Entity rows first: once the thing itself is loaded, its tile/room rows are dropped (2026-10-11 Venom
                // Crypts: the 169 boss-room tiles kept the route on the nearest tile, not the corpse with the vial).
                var found = false;
                var entityHit = false;
                foreach (var t in book.RadarTargetsFor(areaRawName).OrderBy(t => string.Equals(t.TargetType, "Entity", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    if (!string.Equals(t.DisplayName, arg, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    var isEntity = string.Equals(t.TargetType, "Entity", StringComparison.OrdinalIgnoreCase);
                    if (!isEntity && entityHit) continue;
                    var before = r.Goals.Count;
                    foreach (var alt in (t.Alternatives ?? []).Prepend(t))
                    {
                        if (isEntity)
                            Entities(gc, r, e => RouteBook.Like(e.Path, alt.Name), MayBeMonster(alt.Name));
                        else
                            Tiles(gc, areaRawName, alt.Name, r, alt.Rooms ?? t.Rooms);
                        if (r.Goals.Count > before) break;
                    }
                    if (isEntity && r.Goals.Count > before) entityHit = true;
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

    private void Entities(GameController gc, Result r, Func<Entity, bool> match, bool monsters = false) => Entities(gc, r, null, match, monsters);

    private static readonly HashSet<EntityType> NotPlaces = [EntityType.Monster, EntityType.Effect, EntityType.Player, EntityType.WorldItem];
    private readonly List<Entity> _places = new(256);

    /// <summary>
    /// Every valid entity except monsters, effects, players and ground items, gathered from the HUD's per-type lists
    /// once per resolve (<see cref="RefreshPlaces"/>). Path matches (entity:, click:, ExcludeNear, DoneWhenSeen) walk
    /// this instead of all entities: the full list grows with every monster on a busy map, this one doesn't.
    /// </summary>
    public IReadOnlyList<Entity> Places => _places;

    public void RefreshPlaces(GameController gc)
    {
        _places.Clear();
        foreach (var (type, list) in gc.EntityListWrapper.ValidEntitiesByType)
            if (!NotPlaces.Contains(type))
                _places.AddRange(list);
    }

    /// <summary>A path pattern that names a monster (a boss) must search every entity, not just <see cref="Places"/>.</summary>
    private static bool MayBeMonster(string pattern) => pattern.Contains("Monster", StringComparison.OrdinalIgnoreCase);

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
    private void Entities(GameController gc, Result r, EntityType? type, Func<Entity, bool> match, bool monsters = false)
    {
        r.FromEntities = true;
        var area = _currentArea;
        IEnumerable<Entity> source = type is { } t && gc.EntityListWrapper.ValidEntitiesByType.TryGetValue(t, out var list) ? list
            : monsters || _places.Count == 0 ? gc.Entities : _places;
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

    /// <summary>
    /// Tiles matching <paramref name="pattern"/>; with <paramref name="rooms"/> (Radar's Rooms), only those inside a room of
    /// the area graph whose .arm path matches one of them, as Radar does. 2026-10-11 Venom Crypts: the vial row is
    /// Name "*" in Rooms "*boss*", and without the room filter every tile was a goal (4725, one at the entrance).
    /// </summary>
    private void Tiles(GameController gc, string areaRawName, string pattern, Result r, string[]? rooms = null)
    {
        if (_tiles == null || _areaKey != areaRawName)
        {
            _tiles = gc.IngameState.Data.Tiles;
            _areaKey = areaRawName;
            _tileMatches.Clear();
        }
        if (_tiles == null) { r.Why = "IngameData.Tiles is null"; return; }
        if (!pattern.Contains('*') && !pattern.Contains('?')) pattern = "*" + pattern + "*";
        var key = rooms is { Length: > 0 } ? pattern + "#" + string.Join(",", rooms) : pattern;
        // Tiles don't change within an area: match each pattern once (a G2_6 scan of 10,989 tiles x 2 regexes was
        // ~1.9 ms, twice a second, mostly for a Radar fallback that matched nothing).
        if (!_tileMatches.TryGetValue(key, out var cells))
        {
            cells = new List<(int, int)>();
            List<(int x0, int y0, int x1, int y1)>? boxes = null;
            if (rooms is { Length: > 0 })
            {
                boxes = new();
                try
                {
                    foreach (var graph in gc.IngameState.Data.AreaGraphs)
                    foreach (var room in graph.Rooms)
                        if (room.Name is { } n && rooms.Any(p => RouteBook.Like(n, p.Contains('*') ? p : "*" + p + "*")))
                            boxes.Add((room.MinCoord.X * TileCells, room.MinCoord.Y * TileCells, room.MaxCoord.X * TileCells, room.MaxCoord.Y * TileCells));
                }
                catch (Exception ex) { r.Why = $"IngameData.AreaGraphs (Radar rooms {string.Join(",", rooms)}): {ex.Message}"; return; }
                if (boxes.Count == 0) { r.Why = $"no room matches '{string.Join(",", rooms)}' in {areaRawName}'s area graph"; _tileMatches[key] = cells; return; }
            }
            foreach (var t in _tiles)
            {
                if (!(RouteBook.Like(t.Name, pattern) || RouteBook.Like(t.Path, pattern))) continue;
                int x = t.Coordinate.X, y = t.Coordinate.Y;
                if (boxes != null && !boxes.Any(b => x >= b.x0 && x < b.x1 && y >= b.y0 && y < b.y1)) continue;
                cells.Add((x, y));
            }
            _tileMatches[key] = cells;
        }
        r.Goals.AddRange(cells);
    }

    private readonly Dictionary<string, List<(int x, int y)>> _tileMatches = new();
}
