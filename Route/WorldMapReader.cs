using System;
using System.Collections.Generic;
using System.Linq;
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
        public Vector2 NodeLocal;     // the node's top-left inside the pan container, layout units (fixed per node)
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

        Title = wm.ChildCount > 3 ? FirstText(wm.GetChildAtIndex(3), 0) ?? "" : "";
        Mode = Title.Equals("Teleport", StringComparison.OrdinalIgnoreCase) ? View.Teleport
            : Title.Equals("Travel", StringComparison.OrdinalIgnoreCase) ? View.Travel : View.Unknown;
        if (Mode == View.Unknown) Why = $"WorldMap[3] title '{Title}' is neither Travel nor Teleport: view not recognised";

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
            if (Mode == View.Travel && area.id == townAreaId)
                Stops.Add(new Stop { Index = i, Node = n, AreaRow = row, Center = c, ScanCenter = c, Size = r.Width, ScanSize = r.Width, NodePosX = n.Position.X, NodeLocal = Local(n, root), Unlocked = n.IsVisible });
            else if (r.Width >= 80 && n.ChildCount == 0 && (Mode == View.Teleport || area.id != townAreaId))
            {
                // An area's own node (content icons - bosses, chests - are nodes with an icon child): the destination
                // marker, and in the Teleport view the waypoint orb you click.
                _dests.Add((area.id, area.name, c));
                if (Mode == View.Teleport)
                    Stops.Add(new Stop { Index = i, Node = n, AreaRow = row, Center = c, ScanCenter = c, Size = r.Width, ScanSize = r.Width, NodePosX = n.Position.X, NodeLocal = Local(n, root), Unlocked = n.IsVisible, DestId = area.id, DestName = area.name });
            }
        }
        if (matched == 0) { Why = $"no WorldMap[0][1][2] node (of {root.ChildCount}) has a WorldAreas row at +0x{AreaPtrOffset:X}: offset moved"; return; }
        CalibratePan(gc, wm);
        if (Mode != View.Travel) return;
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
        Vector2 live;
        float width;
        if (FreshPan(gc) is { } pan)
        {
            // Straight from game memory: the HUD's rect (and WorldMap[0].Position) come from cached element memory
            // and trailed a drag by up to 52 px (measured 2026-10-10 against this raw read: avg 9.4 px).
            var win = gc.Window.GetWindowRectangle();
            float sx = win.Width / 2560f, sy = win.Height / 1600f;
            width = anchor.ScanSize;
            live = new Vector2(pan.X * sx + anchor.NodeLocal.X * sy + width * 0.5f, (pan.Y + anchor.NodeLocal.Y) * sy + width * 0.5f);
            PanSource = "fresh";
        }
        else
        {
            var r = n.GetClientRect();
            if (r.Width <= 0) return false;
            width = r.Width;
            live = new Vector2(r.Center.X + PanCorrectionX(gc, r.X, anchor.NodePosX), r.Center.Y);
            PanSource = "hud rect (fresh pan unavailable: " + (_panWhy ?? "?") + ")";
        }
        var scale = anchor.ScanSize > 0 ? width / anchor.ScanSize : 1f;
        foreach (var s in Stops)
        {
            if (s == anchor) { s.Center = live; s.Size = width; continue; }
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
    private static float PanCorrectionX(GameController gc, float liveRectX, float nodePosX)
    {
        var win = gc.Window.GetWindowRectangle();
        float sx = win.Width / 2560f, sy = win.Height / 1600f;
        var pan = liveRectX / sy - nodePosX;
        return pan * (sx - sy);
    }

    // ── Fresh pan: WorldMap[0]'s pan read straight from game memory every frame ─────────────────────────────────

    private delegate bool RawReadFn(IntPtr address, Span<byte> buffer);
    private RawReadFn? _raw;
    private bool _rawTried;
    private long _panAddr;        // WorldMap[0] element
    private int _panOffset = -1;  // where its pan (Position, 2 floats) sits; calibrated by matching at scan time
    private string? _panWhy;
    public string PanSource = "";

    /// <summary>Scan time (map at rest): find WorldMap[0]'s pan in its first 0x400 bytes by its HUD value.</summary>
    private void CalibratePan(GameController gc, Element wm)
    {
        if (wm.ChildCount == 0) { _panWhy = "WorldMap has no children"; _panOffset = -1; return; }
        var c1 = wm.GetChildAtIndex(0);
        if (c1.Address == _panAddr && _panOffset >= 0) return;
        _panAddr = c1.Address; _panOffset = -1;
        var raw = Raw(gc);
        if (raw == null) { _panWhy = "leaf memory backend not reachable"; return; }
        Span<byte> buf = stackalloc byte[0x400];
        if (!raw((IntPtr)c1.Address, buf)) { _panWhy = $"could not read WorldMap[0] at 0x{c1.Address:X}"; return; }
        var want = c1.Position;
        for (var off = 0; off + 8 <= buf.Length; off += 4)
            if (MathF.Abs(BitConverter.ToSingle(buf.Slice(off, 4)) - want.X) < 0.01f && MathF.Abs(BitConverter.ToSingle(buf.Slice(off + 4, 4)) - want.Y) < 0.01f)
            { _panOffset = off; _panWhy = null; return; }   // +0x100 on 2026-10-10
        _panWhy = $"WorldMap[0].Position {want} not found in its first 0x400 bytes (offset moved, or the map was moving during the scan)";
    }

    private Vector2? FreshPan(GameController gc)
    {
        if (_panOffset < 0 || _panAddr == 0 || _raw == null) return null;
        Span<byte> b = stackalloc byte[8];
        if (!_raw((IntPtr)(_panAddr + _panOffset), b)) { _panWhy = "fresh pan read failed"; return null; }
        return new Vector2(BitConverter.ToSingle(b[..4]), BitConverter.ToSingle(b[4..]));
    }

    private RawReadFn? Raw(GameController gc)
    {
        if (_rawTried) return _raw;
        _rawTried = true;
        try
        {
            const System.Reflection.BindingFlags inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var mem = (object)gc.Memory;
            var paged = mem.GetType().GetField("_backend", inst)?.GetValue(mem);
            var leaf = paged?.GetType().GetFields(inst).FirstOrDefault(f => f.FieldType.Name == "IMemoryBackend")?.GetValue(paged) ?? paged;
            var m = leaf?.GetType().GetMethod("TryReadMemory", [typeof(IntPtr), typeof(Span<byte>)]);
            if (leaf != null && m != null) _raw = (RawReadFn)Delegate.CreateDelegate(typeof(RawReadFn), leaf, m);
        }
        catch { _raw = null; }
        return _raw;
    }

    /// <summary>A node's top-left inside the pan container (sum of Position from the node up to WorldMap[0][1][2]).</summary>
    private static Vector2 Local(Element node, Element container)
    {
        var p = node.Position;
        for (var e = node.Parent; e != null && e.Address != 0 && e.Address != container.Parent?.Parent?.Address; e = e.Parent)
            p += e.Position;
        return p;
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
