using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using Newtonsoft.Json;

namespace WhatsARoute.Route;

/// <summary>One place of interest seen in an area: where it stood and the terrain tile under it.</summary>
public sealed class SightingDto
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>The .tdt under it: what to put in a tile: target for layouts where the entity isn't loaded yet.</summary>
    public string? Tile { get; set; }
    public int TileX { get; set; }
    public int TileY { get; set; }
    /// <summary>Area transitions: where they lead.</summary>
    public string? DestArea { get; set; }
    /// <summary>StateMachine states, "active=1,..." (last seen; updated while the entity stays loaded).</summary>
    public string? State { get; set; }
    public bool Targetable { get; set; }
    public uint AreaHash { get; set; }
    public string Seen { get; set; } = "";
}

/// <summary>
/// What the route has seen in this area instance: transitions, waypoints, checkpoints, quest icons and every entity
/// a step resolved to, with their grid cell and the tile under them. Two uses: a goal that unloads when you walk
/// away keeps its place for the rest of the visit (the game only sends entities near you), and sightings.json next
/// to route.json records them per area, so a new layout's entity can be mapped to its tile for route.json.
/// </summary>
public sealed class Sightings
{
    private const int KeepPerArea = 400;
    private readonly Dictionary<(string path, int x, int y), SightingDto> _area = new();
    private readonly List<SightingDto> _list = new();
    private string _areaRaw = "";
    private uint _hash;
    private bool _dirty;
    private double _dirtySince;
    private Task? _write;

    public IReadOnlyList<SightingDto> Area => _list;
    public string? Why { get; private set; }

    public void ResetArea(string areaRaw, uint hash, string pluginDir)
    {
        Flush(pluginDir);
        _area.Clear(); _list.Clear();
        _areaRaw = areaRaw; _hash = hash;
        // Same instance as before a plugin reload or HUD restart: what was seen is still there (and the next write
        // replaces this instance's rows, so starting empty would also drop them from the file).
        if (hash == 0 || areaRaw.Length == 0) return;
        var file = Path.Combine(pluginDir, "sightings.json");
        try
        {
            _write?.Wait(1000);
            if (!File.Exists(file)) return;
            var all = JsonConvert.DeserializeObject<Dictionary<string, List<SightingDto>>>(File.ReadAllText(file));
            if (all == null || !all.TryGetValue(areaRaw, out var rows)) return;
            foreach (var s in rows)
                if (s.AreaHash == hash && _area.TryAdd((s.Path, s.X, s.Y), s)) _list.Add(s);
        }
        catch (Exception ex) { Why = $"sightings.json: {ex.Message}"; }
    }

    /// <summary>
    /// Walks the HUD's per-type lists of places (cheap: a few dozen entities, never monsters). A new area instance
    /// (also after a plugin reload, when AreaChange never fired) writes the old one's sightings and starts afresh.
    /// </summary>
    public int Scan(GameController gc, string areaRaw, uint hash, string pluginDir, Func<(int x, int y), IngameData.Tile?> tileAt, double now)
    {
        if (areaRaw != _areaRaw || hash != _hash) ResetArea(areaRaw, hash, pluginDir);
        var added = 0;
        var byType = gc.EntityListWrapper.ValidEntitiesByType;
        foreach (var t in new[] { EntityType.AreaTransition, EntityType.Waypoint, EntityType.IngameIcon })
            if (byType.TryGetValue(t, out var list))
                foreach (var e in list)
                    if (Note(e, tileAt, now)) added++;
        return added;
    }

    /// <summary>Records <paramref name="e"/> (or refreshes its state). True when it is new here.</summary>
    public bool Note(Entity e, Func<(int x, int y), IngameData.Tile?> tileAt, double now)
    {
        if (!e.IsValid || e.Path is not { Length: > 0 } path) return false;
        if (path.StartsWith("Metadata/Monsters", StringComparison.Ordinal) || path.StartsWith("Metadata/Effects", StringComparison.Ordinal)) return false;
        var gp = e.GridPos;
        var cell = ((int)gp.X, (int)gp.Y);
        var state = StateOf(e);
        var targetable = e.GetComponent<Targetable>() is not { } tg || tg.isTargetable;
        if (_area.TryGetValue((path, cell.Item1, cell.Item2), out var seen))
        {
            if (seen.State != state || seen.Targetable != targetable) { seen.State = state; seen.Targetable = targetable; MarkDirty(now); }
            return false;
        }
        var tile = tileAt(cell);
        var dto = new SightingDto
        {
            Path = path, Name = e.RenderName, Type = e.Type.ToString(), X = cell.Item1, Y = cell.Item2,
            Tile = tile?.Path, TileX = tile?.Coordinate.X ?? 0, TileY = tile?.Coordinate.Y ?? 0,
            DestArea = e.Type == EntityType.AreaTransition ? e.GetComponent<AreaTransition>()?.WorldArea?.Id : null,
            State = state, Targetable = targetable, AreaHash = _hash, Seen = DateTime.Now.ToString("s"),
        };
        _area[(path, cell.Item1, cell.Item2)] = dto;
        _list.Add(dto);
        MarkDirty(now);
        return true;
    }

    public static string? StateOf(Entity e)
    {
        var sm = e.GetComponent<StateMachine>()?.States;
        if (sm == null || sm.Count == 0) return null;
        return string.Join(",", sm.Select(s => s.Name + "=" + s.Value));
    }

    /// <summary>Does a recorded state string hold "name=value"?</summary>
    public static bool HasState(string? state, string nameEqValue)
    {
        if (state == null) return false;
        foreach (var part in state.Split(','))
            if (string.Equals(part, nameEqValue, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void MarkDirty(double now) { if (!_dirty) _dirtySince = now; _dirty = true; }

    /// <summary>Writes a few seconds after the last change, so a burst of sightings is one file write.</summary>
    public void FlushIfIdle(string pluginDir, double now)
    {
        if (_dirty && now - _dirtySince > 3) Flush(pluginDir);
    }

    public void Flush(string pluginDir)
    {
        if (!_dirty || _list.Count == 0 || _areaRaw.Length == 0) { _dirty = false; return; }
        if (_write is { IsCompleted: false }) return;   // the next idle check catches it
        _dirty = false;
        var area = _areaRaw;
        var snapshot = _list.Select(s => new SightingDto
        {
            Path = s.Path, Name = s.Name, Type = s.Type, X = s.X, Y = s.Y, Tile = s.Tile, TileX = s.TileX, TileY = s.TileY,
            DestArea = s.DestArea, State = s.State, Targetable = s.Targetable, AreaHash = s.AreaHash, Seen = s.Seen,
        }).ToList();
        var hash = _hash;
        var file = Path.Combine(pluginDir, "sightings.json");
        _write = Task.Run(() =>
        {
            try
            {
                Dictionary<string, List<SightingDto>> all;
                try { all = File.Exists(file) ? JsonConvert.DeserializeObject<Dictionary<string, List<SightingDto>>>(File.ReadAllText(file)) ?? new() : new(); }
                catch { all = new(); }
                if (!all.TryGetValue(area, out var rows)) all[area] = rows = new List<SightingDto>();
                rows.RemoveAll(r => r.AreaHash == hash);
                rows.InsertRange(0, snapshot);
                if (rows.Count > KeepPerArea) rows.RemoveRange(KeepPerArea, rows.Count - KeepPerArea);
                File.WriteAllText(file, JsonConvert.SerializeObject(all, Formatting.Indented));
                Why = null;
            }
            catch (Exception ex) { Why = $"sightings.json: {ex.Message}"; }
        });
    }
}
