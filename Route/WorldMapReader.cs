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
/// Two rates: <see cref="Scan"/> identifies the stops (index, area row, unlocked, destination) and costs ~4 ms and
/// ~1.9 MB of page rents for 127 nodes, so it runs when the map opens and then every 2 s. <see cref="Refresh"/> runs
/// every frame while drawing: it reads the live rect of ONE node (the chosen stop, GetClientRect(): ~3.5 us, 880 B;
/// GetClientRectCache is a 28 ms TimeCache and trails a dragged map by up to 300 px) and moves every other stop by
/// the same translation and scale, so the marker is glued to the wheel at the HUD's frame rate. Before trusting the
/// node it checks that the child still points at the same WorldAreas row; otherwise the stops are dropped until the
/// next scan rather than drawn on the wrong node.
/// </summary>
public sealed class WorldMapReader
{
    public const int AreaPtrOffset = 0x2E0;
    private const double ScanEverySec = 2.0;

    public sealed class Stop
    {
        public int Index;
        public Element? Node;         // the node element at scan time (its address is checked before use)
        public long AreaRow;          // the WorldAreas row the node pointed at when scanned
        public Vector2 Center;        // live (Refresh), screen px
        public float Size;            // live, px
        public Vector2 ScanCenter;    // as scanned
        public float ScanSize;
        public bool Unlocked;
        public string DestId = "", DestName = "";
    }

    public readonly List<Stop> Stops = new();
    public bool Open;
    public string? Why;           // the broken link when the map is open but could not be read
    public double LastScanMs;
    private readonly Dictionary<long, (string id, string name)> _areaRows = new();
    private readonly List<(string id, string name, Vector2 c)> _dests = new();
    private double _nextScan;
    private long _wmAddress;

    /// <summary>Tick, cheap: is the map open? Scans the nodes when due (on open, then every 2 s).</summary>
    public void Read(GameController gc, string townAreaId, double now)
    {
        Why = null;
        var wm = gc.IngameState.IngameUi.WorldMap;
        var open = wm is { IsVisible: true };
        if (!open) { Open = false; Stops.Clear(); _nextScan = 0; _wmAddress = 0; return; }
        if (!Open || wm.Address != _wmAddress) { _nextScan = 0; _wmAddress = wm.Address; }
        Open = true;
        if (now < _nextScan) return;
        _nextScan = now + ScanEverySec;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Scan(gc, wm, townAreaId);
        LastScanMs = sw.Elapsed.TotalMilliseconds;
    }

    private void Scan(GameController gc, Element wm, string townAreaId)
    {
        Stops.Clear();
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
            var row = mem.Read<long>(n.Address + AreaPtrOffset);
            if (!_areaRows.TryGetValue(row, out var area)) continue;
            matched++;
            var r = n.GetClientRect();
            var c = new Vector2(r.Center.X, r.Center.Y);
            if (area.id == townAreaId)
                Stops.Add(new Stop { Index = i, Node = n, AreaRow = row, Center = c, ScanCenter = c, Size = r.Width, ScanSize = r.Width, Unlocked = n.IsVisible });
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

    /// <summary>
    /// Render, every frame: the anchor stop's live rect, and every other stop moved by the same translation and scale
    /// (the map pans and zooms as one). False when the anchor's node no longer points at its area row (the map was
    /// rebuilt): nothing should be drawn until the next scan.
    /// </summary>
    public bool Refresh(GameController gc, Stop anchor)
    {
        if (anchor.Node is not { } n || n.Address == 0) return false;
        if (gc.Memory.Read<long>(n.Address + AreaPtrOffset) != anchor.AreaRow) { Why = $"stop {anchor.Index}: node no longer points at its area row (map rebuilt), waiting for the next scan"; return false; }
        var r = n.GetClientRect();
        if (r.Width <= 0) return false;
        var live = new Vector2(r.Center.X, r.Center.Y);
        var scale = anchor.ScanSize > 0 ? r.Width / anchor.ScanSize : 1f;
        foreach (var s in Stops)
        {
            if (s == anchor) { s.Center = live; s.Size = r.Width; continue; }
            s.Center = live + (s.ScanCenter - anchor.ScanCenter) * scale;
            s.Size = s.ScanSize * scale;
        }
        return true;
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
