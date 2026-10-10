using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.FilesInMemory;

namespace WhatsARoute.Route;

/// <summary>
/// The open travel map (Act 2 Desert Map: IngameUi.WorldMap). Map nodes live at WorldMap[0][1][2][i]; each node holds
/// a pointer to its WorldAreas.dat row at +0x2E0 (found 2026-10-10 by scanning node memory for row addresses: 120 of
/// 127 nodes matched at that one offset). Caravan stops (the wheel icons) are the nodes whose area is the caravan town
/// itself; a stop is unlocked when its node is visible, and serves the nearest destination node (82 px area markers,
/// 50-110 px away). HUD's WorldMap.ActsRoot / WaypointsRoot are null on this build, so the walk is by index.
/// </summary>
public sealed class WorldMapReader
{
    public const int AreaPtrOffset = 0x2E0;

    public sealed class Stop
    {
        public int Index;
        public Vector2 Center;
        public float Size;
        public bool Unlocked;
        public string DestId = "", DestName = "";
    }

    public readonly List<Stop> Stops = new();
    public bool Open;
    public string? Why;           // the broken link when the map is open but could not be read
    private readonly Dictionary<long, (string id, string name)> _areaRows = new();
    private readonly List<(string id, string name, Vector2 c)> _dests = new();

    public void Read(GameController gc, string townAreaId)
    {
        Stops.Clear();
        Why = null;
        var wm = gc.IngameState.IngameUi.WorldMap;
        Open = wm is { IsVisible: true };
        if (!Open) return;
        var root = Child(wm, 0, "WorldMap")?.Let(e => Child(e, 1, "WorldMap[0]"))?.Let(e => Child(e, 2, "WorldMap[0][1]"));
        if (root == null) return;
        if (_areaRows.Count == 0)
            foreach (var a in gc.Files.WorldAreas.EntriesList) _areaRows[a.Address] = (a.Id, a.Name);

        _dests.Clear();
        var mem = gc.Memory;
        var matched = 0;
        for (var i = 0; i < root.ChildCount; i++)
        {
            var n = root.GetChildAtIndex(i);
            if (n == null) continue;
            if (!_areaRows.TryGetValue(mem.Read<long>(n.Address + AreaPtrOffset), out var area)) continue;
            matched++;
            var r = n.GetClientRectCache;
            var c = new Vector2(r.Center.X, r.Center.Y);
            if (area.id == townAreaId)
                Stops.Add(new Stop { Index = i, Center = c, Size = r.Width, Unlocked = n.IsVisible });
            else if (r.Width >= 80)
                _dests.Add((area.id, area.name, c));
        }
        if (matched == 0) { Why = $"no WorldMap[0][1][2] node (of {root.ChildCount}) has a WorldAreas row at +0x{AreaPtrOffset:X}: offset moved"; return; }
        foreach (var s in Stops)
        {
            var best = float.MaxValue;
            foreach (var d in _dests)
            {
                var dist = Vector2.Distance(d.c, s.Center);
                if (dist < best) { best = dist; s.DestId = d.id; s.DestName = d.name; }
            }
        }
    }

    private Element? Child(Element e, int i, string where)
    {
        if (i < e.ChildCount) return e.GetChildAtIndex(i);
        Why = $"{where} has {e.ChildCount} children, wanted index {i}: travel map layout changed";
        return null;
    }
}

internal static class LetExt
{
    public static TR? Let<T, TR>(this T v, Func<T, TR?> f) where T : class where TR : class => f(v);
}
