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
        public float NodePosX;        // the node's position inside the map, layout units (fixed): to derive the pan
        public bool Unlocked;
        public string DestId = "", DestName = "";
    }

    /// <summary>
    /// The same WorldMap element is two views. Its title (WorldMap[3], first visible text) says which:
    /// "Travel" = the caravan's Desert Map, where the wheels (caravan stops) are what you click;
    /// "Teleport" = the waypoint view, where each area's own node (the blue orb) is what you click and the wheels are
    /// drawn but inert. <see cref="Stops"/> holds the clickable choices of the current view either way.
    /// </summary>
    public enum View { Unknown, Travel, Teleport }

    public readonly List<Stop> Stops = new();
    public View Mode;
    public string Title = "";
    public bool Open;
    public bool Port;             // the Travel view is a port (Act 4 sea): Stops are the islands themselves
    public string? Why;           // the broken link when the map is open but could not be read
    public double LastScanMs;
    private readonly Dictionary<long, (string id, string name)> _areaRows = new();
    private readonly List<(string id, string name, Vector2 c)> _dests = new();
    private readonly List<Stop> _destStops = new();
    private double _nextScan;
    private long _wmAddress;

    /// <summary>Tick, cheap: is the map open? Scans the nodes when due (on open, then every 2 s).</summary>
    public void Read(GameController gc, string townAreaId, double now)
    {
        Why = null;
        var wm = gc.IngameState.IngameUi.WorldMap;
        var open = wm is { IsVisible: true };
        if (!open) { Open = false; Port = false; Stops.Clear(); _nextScan = 0; _wmAddress = 0; return; }
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
        Port = false;
        // WorldMap[0] holds one map per act and shows only the current one: [1] the Desert Map, [3] the Act 4 sea.
        var acts = Child(wm, 0, "WorldMap");
        if (acts == null) return;
        var act = 1;
        for (var a = 0; a < acts.ChildCount; a++)
            if (acts.GetChildAtIndex(a) is { IsVisible: true }) { act = a; break; }
        var root = Child(acts, act, "WorldMap[0]")?.Let(e => Child(e, 2, $"WorldMap[0][{act}]"));
        if (root == null) return;
        if (_areaRows.Count == 0)
            foreach (var a in gc.Files.WorldAreas.EntriesList) _areaRows[a.Address] = (a.Id, a.Name);

        Title = wm.ChildCount > 3 ? FirstText(wm.GetChildAtIndex(3), 0) ?? "" : "";
        Mode = Title.Equals("Teleport", StringComparison.OrdinalIgnoreCase) ? View.Teleport
            : Title.Equals("Travel", StringComparison.OrdinalIgnoreCase) ? View.Travel : View.Unknown;
        if (Mode == View.Unknown) Why = $"WorldMap[3] title '{Title}' is neither Travel nor Teleport: view not recognised";

        _dests.Clear();
        _destStops.Clear();
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
            if (Mode == View.Travel && area.id == townAreaId)
                Stops.Add(new Stop { Index = i, Node = n, AreaRow = row, Center = c, ScanCenter = c, Size = r.Width, ScanSize = r.Width, NodePosX = n.Position.X, Unlocked = n.IsVisible });
            else if (r.Width >= 80 && n.ChildCount == 0 && (Mode == View.Teleport || area.id != townAreaId))
            {
                // An area's own node (content icons - bosses, chests - are nodes with an icon child): the destination
                // marker, and in the Teleport view the waypoint orb you click.
                _dests.Add((area.id, area.name, c));
                (Mode == View.Teleport ? Stops : _destStops).Add(new Stop { Index = i, Node = n, AreaRow = row, Center = c, ScanCenter = c, Size = r.Width, ScanSize = r.Width, NodePosX = n.Position.X, Unlocked = n.IsVisible, DestId = area.id, DestName = area.name });
            }
        }
        if (matched == 0) { Why = $"no WorldMap[0][{act}][2] node (of {root.ChildCount}) has a WorldAreas row at +0x{AreaPtrOffset:X}: offset moved"; return; }
        if (Mode != View.Travel) return;
        // The Act 4 sea (WorldMap[0][3]), or every town node on one spot: no wheels, this is the port (Makoru's sailing
        // map), and the islands' own nodes are what you click. Seen 2026-10-11: Kingsmarch's two nodes share a centre,
        // each island is one node. Opened from an island's ship (2026-10-12, Isle of Kin) the "town" is that island,
        // whose content nodes are spread over it: the sea map itself says it is the port.
        if (act == 3 || Stops.Count > 1 && Stops.TrueForAll(s => Vector2.Distance(s.ScanCenter, Stops[0].ScanCenter) < 4))
        {
            Port = true;
            Stops.Clear();
            Stops.AddRange(_destStops);
            return;
        }
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
        var live = new Vector2(r.Center.X + PanCorrectionX(gc), r.Center.Y);
        var scale = anchor.ScanSize > 0 ? r.Width / anchor.ScanSize : 1f;
        foreach (var s in Stops)
        {
            if (s == anchor) { s.Center = live; s.Size = r.Width; continue; }
            s.Center = live + (s.ScanCenter - anchor.ScanCenter) * scale;
            s.Size = s.ScanSize * scale;
        }
        return true;
    }

    /// <summary>
    /// The HUD scales element rects by one UI factor (screen height / 1600) on both axes, but the game pans this map
    /// horizontally by screen width / 2560 (its 2560x1600 layout stretched to the screen). Node offsets inside the map
    /// come out right; the pan (WorldMap[0].Position, layout units) does not, so markers drift sideways as the map is
    /// dragged. Measured 2026-10-10 at 1920x1080: pan -340.8 -> HUD x 766.9, orb on screen ~740 (27 px = 340.8 x
    /// 0.079 = 0.75 - 0.675); pan -2.9 -> ~0 px. Vertically both factors are equal, so no correction.
    /// </summary>
    /// The pan is derived from the node's LIVE rect, not read from WorldMap[0].Position: that property comes from the
    /// element's cached memory and trailed a drag visibly (the user: "super laggy"). The HUD computes
    /// rect.X = (pan + nodePos.X) * sy with nodePos fixed per node, so pan = rect.X / sy - nodePos.X
    /// (check: 614.55 / 0.675 - 1251.28 = -340.84, the pan read at rest).
    /// The pan comes from the pan container's own rect, read every frame: rect(WorldMap[0]).X = WorldMap.X + pan.X * sy.
    /// It used to be derived from the node's rect minus the node's Position captured at the last scan; on the Desert
    /// Map a node's Position changes while dragging, so the correction drifted (20-40 px in mid-drag screenshots) and
    /// snapped back at every 2 s rescan (the user: "re-aligns every ~3 seconds").
    private static float PanCorrectionX(GameController gc)
    {
        var wm = gc.IngameState.IngameUi.WorldMap;
        if (wm.ChildCount == 0) return 0;
        var win = gc.Window.GetWindowRectangle();
        float sx = win.Width / 2560f, sy = win.Height / 1600f;
        var pan = (wm.GetChildAtIndex(0).GetClientRect().X - wm.GetClientRect().X) / sy;
        return pan * (sx - sy);
    }

    private static string? FirstText(Element? e, int depth)
    {
        if (e == null || depth > 3) return null;
        if (e.IsVisible && !string.IsNullOrEmpty(e.Text)) return e.Text;
        for (var i = 0; i < e.ChildCount; i++)
            if (FirstText(e.GetChildAtIndex(i), depth + 1) is { } t) return t;
        return null;
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
