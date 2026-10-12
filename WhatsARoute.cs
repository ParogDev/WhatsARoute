using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ImGuiNET;
using Newtonsoft.Json;
using WhatsARoute.Navigation;
using WhatsARoute.Rendering;
using WhatsARoute.Route;

namespace WhatsARoute;

/// <summary>
/// One path at a time to the current objective, drawn in the world and on the large map with the terrain edges
/// along it. Per area: the terrain is copied once and its edges traced on a worker. Per objective: a flow field
/// (cost to the goal from every cell) is built once on a worker. Per step the player takes: the path is read off
/// the field and shaped (microseconds). Per frame: only projection and drawing.
/// </summary>
public sealed partial class WhatsARoute : BaseSettingsPlugin<WhatsARouteSettings>
{
    private const int NearCells = 400;          // shaped for the world view (~4300 world units)
    private const int MaxCells = 60_000;
    private const int MapEvery = 3;             // map path keeps every Nth cell

    private readonly RouteBook _book = new();
    private readonly TargetResolver _resolver = new();
    private readonly TargetResolver.Result _resolved = new();
    private readonly RouteRenderer _renderer = new();
    private readonly RouteFrame _frame = new();
    private readonly FreshView _fresh = new();
    private readonly Markers _markers = new();
    private readonly UiMask _mask = new();
    private readonly PathShaper _shaper = new();
    private readonly List<(int x, int y)> _cells = new(4096);
    private readonly List<(int x, int y)> _nearCells = new(NearCells);
    private readonly List<Vector2> _edgeQuery = new(256);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // area
    private volatile AreaGrid? _grid;
    private volatile EdgeMap? _edges;
    private int[] _edgeStamp = [];
    private int _edgeStampId;
    private string _areaRaw = "", _areaName = "";
    private CancellationTokenSource _areaCts = new();
    private double _gridMs;
    private string? _areaWhy;

    // objective
    private volatile FlowField? _field;
    private FlowField? _blockedField;           // the objective's field when the player was on another island
    private string _blockedSpec = "";
    private string _goalSig = "";
    private CancellationTokenSource _fieldCts = new();
    private double _nextResolve;
    private string _targetSpec = "";
    private int _stepIndex = -1;
    private int _stepShift;                     // hotkey nudges in this area
    private readonly HashSet<string> _reachedSteps = new();   // StepKey of steps done this area visit
    private readonly Dictionary<string, int> _quests = new();   // open quest id -> state id
    private double _nextQuests;
    private QuestObjective? _questObj;
    private readonly WorldMapReader _map = new();
    private WorldMapReader.Stop? _chosenStop;
    private int _stopShown = -1;                // stop index the marker is on (snaps in again when it changes)
    private double _stopAppearedAt, _goalAppearedAt;
    private Render? _playerRender;              // cached in Tick; Render reads Pos from it (no GetComponent per frame)
    private Entity? _goalEntity;                // the entity the route leads to, when the goal is a thing to click
    private Render? _goalRender;
    private readonly Dictionary<string, bool> _flags = new();
    private double _nextFlags;
    private string? _why;

    // per step
    private (int x, int y) _lastCell = (int.MinValue, 0);
    private FlowField? _lastFieldUsed;
    private EdgeMap? _lastEdgesUsed;
    private int _lastEdgeAhead, _lastEdgeCorridor;
    private double _replanUs, _tickUs, _renderUs, _resolveUs, _scanUs;
    private float _pathLen;

    /// <summary>
    /// Where route.json and sightings.json live. With "Avoid locking plugin dlls" the HUD runs the plugin from a copy
    /// under Plugins\Temp, so DirectoryFullName points at a snapshot: the source folder keeps the files the user edits
    /// (Reload Route then reads the live route.json) and the sightings the agent reads.
    /// </summary>
    private string DataDir => _dataDir ??= FindDataDir();   // Directory.Exists is ~11 us (more on the junction): once
    private string? _dataDir;

    private string FindDataDir()
    {
        var dir = DirectoryFullName;
        var i = dir.IndexOf(@"\Plugins\Temp\", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return dir;
        var src = dir[..i] + @"\Plugins\Source\" + dir[(i + @"\Plugins\Temp\".Length)..];
        return System.IO.Directory.Exists(src) ? src : dir;
    }

    public override bool Initialise()
    {
        Name = "Whats A Route";
        _feedback.Open(LiveDir);
        _book.Load(DataDir, LiveDir);
        Settings.Route.ReloadRoute.OnPressed = ReloadRoute;
        GameController.PluginBridge.SaveMethod("WhatsARoute.GetState", (Func<string>)GetStateJson);
        _bridge.Refresh(GameController.PluginBridge, 0);   // the bridge may load later: Tick looks again every 2 s
        return true;
    }

    // ── The live loop: the agent edits route.live.json, the player answers with the feedback key ──

    private readonly FeedbackLog _feedback = new();
    private readonly AiBridgeLink _bridge = new();
    private double _nextLiveCheck;
    private string? _toast;                      // the line above the card: the agent's reply, or "sent"
    private double _toastUntil;
    private bool _feedbackOpen;
    private string _lastObjective = "";

    /// <summary>The live folder: the setting, else %LOCALAPPDATA%\Hexile\WhatsARoute (outside git, writable by the agent).</summary>
    private string LiveDir => Settings.Route.LiveFolder.Value is { Length: > 0 } f ? f
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexile", "WhatsARoute");

    private void ReloadRoute()
    {
        _book.Load(DataDir, LiveDir);
        // Reached steps are kept: they are keyed by what the step is (StepKey), so an edit that reorders or inserts steps
        // neither loses them nor hands "reached" to another step.
        _goalSig = ""; _nextResolve = 0; _nextQuests = 0;
        var what = _book.LiveError ?? _book.RouteError ?? _book.LiveMessage
            ?? (_book.LiveAreas + _book.LiveQuests > 0 ? $"Route updated (live: {_book.LiveAreas} areas, {_book.LiveQuests} quest objectives)" : "Route updated");
        Toast(what, _book.LiveMessage != null || _book.LiveError != null ? 12 : 4);
        Trace("route-reloaded", what);
    }

    private void Toast(string text, double seconds) { _toast = text; _toastUntil = _clock.Elapsed.TotalSeconds + seconds; }

    /// <summary>A trace line: feedback.jsonl (the local log) and, through the AI Bridge, a "route" event on its timeline.</summary>
    private void Trace(string evt, string? note = null, List<string>? quests = null)
    {
        var dto = new FeedbackDto
        {
            Kind = "trace", Event = evt, Note = note, Area = _areaRaw, AreaName = _areaName, Target = _targetSpec, Label = _resolved.Label,
            StepIndex = _stepIndex, PlayerGrid = _lastCell.x == int.MinValue ? null : [_lastCell.x, _lastCell.y], Quests = quests,
        };
        _feedback.Write(dto);
        _bridge.Publish("route", dto);
        if (_capture is { } c && evt != "heads-up-start" && c.Events.Count < 200)
            c.Events.Add($"{evt}{(note != null ? ": " + note : "")}{(quests != null ? " " + string.Join(" | ", quests) : "")}{(evt == "objective" ? $": {_resolved.Label} ({_targetSpec})" : "")}");
    }

    /// <summary>
    /// The plugin's own line just above the card (route reloaded, an agent route's hand-back, the bridge back); fades out.
    /// F8 progress is the bridge's to draw (WhatsARoute.Feedback.cs HandleReplies), except with an older bridge.
    /// </summary>
    private void DrawToast(double now)
    {
        if (_toast == null || now >= _toastUntil || DrawToastStyled(now)) return;   // the toast family's look (WhatsARoute.Compose.cs)
        var a = (float)Math.Clamp(_toastUntil - now, 0, 1);
        var dl = ImGui.GetBackgroundDrawList();
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var wrap = Settings.Debug.StatusWidth.Value - 16f;
        var ts = font.CalcTextSizeA(size, float.MaxValue, wrap, _toast);
        var pos = new Vector2(Settings.Debug.StatusX.Value, Settings.Debug.StatusY.Value - ts.Y - 18);
        dl.AddRectFilled(pos, pos + ts + new Vector2(16, 12), Glow.Col(new Vector3(0.05f, 0.09f, 0.12f), 0.9f * a), 5f);
        dl.AddRect(pos, pos + ts + new Vector2(16, 12), Glow.Col(new Vector3(0.45f, 0.75f, 0.95f), 0.6f * a), 5f, ImDrawFlags.None, 1f);
        dl.AddText(font, size, pos + new Vector2(8, 6), Glow.Col(new Vector3(0.88f, 0.94f, 0.98f), a), _toast, wrap);
    }

    /// <summary>Quest states that changed since the last read, as "QuestId:StateId text", for the trace.</summary>
    private readonly Dictionary<string, int> _questsBefore = new();

    public override void OnClose()
    {
        _bridge.Close();
        _resolver.Sightings.Flush(DataDir);
    }

    public override void AreaChange(AreaInstance area)
    {
        _areaCts.Cancel();
        _areaCts = new CancellationTokenSource();
        _fieldCts.Cancel();
        _grid = null; _edges = null; _field = null; _blockedField = null; _goalSig = ""; _why = null; _areaWhy = null;
        _frame.Ready = false; _frame.Path.Clear(); _frame.MapPath.Clear(); _frame.MapPathZ.Clear(); _frame.EdgePieces.Clear();
        _lastCell = (int.MinValue, 0); _lastFieldUsed = null; _lastEdgesUsed = null;
        _stepShift = 0; _stepIndex = -1; _reachedSteps.Clear(); _nextResolve = 0; _nextFlags = 0;
        _goalEntity = null; _goalRender = null; _frame.HasGoalBox = false; _frame.Detected = false; _playerRender = null; _chosenStop = null; _stopShown = -1;
        _doors.Clear(); _doorAhead = null; _doorRender = null; _questItems.Clear();
        _fresh.Reset();
        _resolver.ResetArea();
        _portalKey = GameKeys.Binding(GameKeys.Config(), "use_portal_scroll");   // the binding is re-read per area (the player may rebind), the path found once
        _areaRaw = area?.Area?.RawName ?? "";
        _areaName = area?.Area?.Name ?? "";
        _lastObjective = "";
        Trace("area");
        _resolver.Sightings.ResetArea(_areaRaw, area?.Hash ?? 0, DataDir);   // writes the old area's sightings first

        var data = GameController.IngameState.Data;
        int[][]? pf; float[][]? height;
        try { pf = data.RawPathfindingData; height = data.RawTerrainHeightData; }
        catch (Exception ex) { _areaWhy = $"reading terrain: {ex.Message}"; return; }
        var ct = _areaCts.Token;
        var minEdges = Settings.World.MinContourEdges.Value;
        _dumpBase = Settings.Debug.SaveTerrain.Value && _areaRaw.Length > 0 && pf is { Length: > 0 }
            ? System.IO.Path.Combine(DataDir, "terrain", $"{_areaRaw}_{area?.Hash ?? 0:x8}") : null;
        var dumpBase = _dumpBase;
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var g = AreaGrid.From(pf, height, out var why);
            _gridMs = sw.Elapsed.TotalMilliseconds;
            _areaWhy = why;
            if (g == null || ct.IsCancellationRequested) return;
            _grid = g;
            var e = EdgeMap.Build(g, minEdges, ct);
            if (e == null || ct.IsCancellationRequested) return;
            _edgeStamp = new int[e.Pieces.Count];
            _edges = e;
            // Last: the route is up before the (gzip, a few hundred ms) dump starts.
            if (dumpBase != null)
                try { TerrainDump.Write(dumpBase + ".bin.gz", pf!, height); }
                catch (Exception ex) { _areaWhy = $"saving terrain: {ex.Message}"; }
        }, ct);
    }

    public override void Tick()
    {
        var t0 = Stopwatch.GetTimestamp();
        try { TickBody(); }
        catch (Exception ex) { _why = $"tick: {ex.Message}"; }
        _tickUs = _tickUs * 0.9 + 0.1 * Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
    }

    private void TickBody()
    {
        var t = _clock.Elapsed.TotalSeconds;
        HandleReplies(t);   // before the in-game check: a reply may arrive on a loading screen
        AgentTick(t);
        F8Tick(t);
        if (!GameController.InGame || GameController.Player == null) { _frame.Ready = false; return; }
        if (t >= _nextLiveCheck) { _nextLiveCheck = t + 0.5; if (_book.Changed()) ReloadRoute(); }   // the agent's live edits, in half a second
        if (t >= _nextQuests) { _nextQuests = t + 1; ReadQuests(); }
        WatchWorld(t);
        if (_capture is { } cap && cap.ShouldEnd(t) is { } endedBy) EndHeadsUp(endedBy);
        ReadTravelMap(t);   // every tick: the open check is one cached read; the node scan inside is rate-limited
        var grid = _grid;
        if (grid == null) { _why = _areaWhy ?? "building the area grid"; _frame.Ready = false; return; }
        var r = _playerRender;
        if (r == null || r.Address == 0 || GameController.Player.Address != r.OwnerAddress)
        {
            r = GameController.Player.GetComponent<Render>();
            if (r == null) { _why = "Player has no Render component"; return; }
            _playerRender = r;
        }
        _frame.Player = r.Pos;
        _frame.PlayerUnclampedHeight = r.UnclampedHeight;
        var cell = ((int)(r.Pos.X / AreaGrid.GridToWorld), (int)(r.Pos.Y / AreaGrid.GridToWorld));
        var now = _clock.Elapsed.TotalSeconds;

        if (now >= _nextFlags) { _nextFlags = now + 2; ReadFlags(); }
        if (now >= _nextResolve)
        {
            _nextResolve = now + 0.5;
            _resolver.Now = now;
            var st = Stopwatch.GetTimestamp();
            _resolver.RefreshPlaces(GameController);
            _seenCache.Clear();
            // Places near you right now (transitions, waypoints, checkpoints, quest icons): remembered for the visit
            // and written to sightings.json, so a goal that unloads keeps its place and a new layout can be mapped.
            _resolver.Sightings.Scan(GameController, _areaRaw, GameController.Area.CurrentArea?.Hash ?? 0, DataDir, c => _resolver.TileAt(GameController, _areaRaw, c), now);
            _scanUs = _scanUs * 0.8 + 0.2 * Stopwatch.GetElapsedTime(st).TotalMicroseconds;
            ResolveObjective(grid, cell);
            _resolver.Sightings.FlushIfIdle(DataDir, now);
            ScanDoors();
            FindDoorAhead();
            ScanBossFight(cell);
        }

        var field = _field;
        if (field == null || field.Grid != grid) { _frame.Ready = false; return; }
        var edgeAhead = Settings.World.EdgeAhead.Value;
        var corridor = Settings.World.EdgeCorridor.Value;
        var edgesNow = _edges;
        if (cell == _lastCell && field == _lastFieldUsed && edgeAhead == _lastEdgeAhead && corridor == _lastEdgeCorridor && edgesNow == _lastEdgesUsed) return;
        // Tick moves to a new objective's field: the previous one is read by nothing else (the cut-off fallback keeps
        // its own), so its 1 float/cell cost array goes back for the next build instead of to the GC.
        if (field != _lastFieldUsed && _lastFieldUsed is { } old && old != _blockedField && old.Grid == grid) old.Recycle();
        _lastCell = cell; _lastFieldUsed = field; _lastEdgesUsed = edgesNow; _lastEdgeAhead = edgeAhead; _lastEdgeCorridor = corridor;
        Replan(grid, field, cell, edgeAhead, corridor);
    }

    private void Replan(AreaGrid grid, FlowField field, (int x, int y) cell, int edgeAhead, int corridor)
    {
        var t0 = Stopwatch.GetTimestamp();
        if (!field.Descend(cell, _cells, MaxCells, out var why))
        {
            _why = why;
            // The player stands on another island than the goal (a boss arena after the fight): lead to a way off it
            // instead of drawing nothing. Not when this field already is the way off (no exit on this island).
            if (_blockedField == null && field.Snap(cell) == null) { _blockedField = field; _blockedSpec = _targetSpec; _nextResolve = 0; }
            _frame.Ready = false;
            return;
        }
        _why = null;
        _nearCells.Clear();
        for (var i = 0; i < _cells.Count && i < NearCells; i++) _nearCells.Add(_cells[i]);
        _shaper.Shape(grid, _nearCells, _frame.Path);
        _frame.Goal = _cells.Count <= NearCells && _frame.Path.Count > 0 ? _frame.Path[^1] : null;
        _frame.Label = _resolved.Label;

        _frame.MapPath.Clear(); _frame.MapPathZ.Clear();
        _pathLen = 0;
        for (var i = 0; i < _cells.Count; i++)
        {
            if (i > 0) _pathLen += (Math.Abs(_cells[i].x - _cells[i - 1].x) + Math.Abs(_cells[i].y - _cells[i - 1].y) > 1 ? 1.41421f : 1f) * AreaGrid.GridToWorld;
            if (i % MapEvery != 0 && i != _cells.Count - 1) continue;
            _frame.MapPath.Add(new Vector2(_cells[i].x + 0.5f, _cells[i].y + 0.5f));
            _frame.MapPathZ.Add(grid.Z[_cells[i].y * grid.W + _cells[i].x]);
        }

        var edges = _edges;
        _frame.EdgePieces.Clear();
        if (edges != null)
        {
            _edgeQuery.Clear();
            for (var i = 0; i < _cells.Count && i < edgeAhead; i += 4) _edgeQuery.Add(new Vector2(_cells[i].x, _cells[i].y));
            if (_edgeStamp.Length < edges.Pieces.Count) _edgeStamp = new int[edges.Pieces.Count];
            edges.Query(_edgeQuery, corridor, _frame.EdgePieces, _edgeStamp, ++_edgeStampId);
        }
        _frame.Ready = true;
        SetGoalEntity(_cells[^1]);
        FindDoorAhead();
        _replanUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;

        // A step without a quest flag counts as done once you stand on its goal. SkipIfNone steps are stateless and
        // repeat (light the next seal, take its portal back): they are never marked reached.
        if (_stepIndex >= 0 && _cells.Count <= 4 && _blockedField == null)   // not at the way out of a cut-off part
        {
            var steps = _book.StepsFor(_areaRaw);
            if (_stepIndex < steps.Count && !steps[_stepIndex].SkipIfNone) _reachedSteps.Add(StepKey(steps[_stepIndex]));
        }
        if (_cells.Count <= 4 && _blockedField == null) AgentReached();
    }

    // A closed door on the way (2026-10-11 Venom Crypts, the user: "it should make me click the door here"): doors are
    // walkable in the pathfinding grid, so the route runs straight through them. Closed = targetable.
    private const int DoorAheadCells = 160, DoorNearPath = 5;
    private readonly List<(Entity e, Render r)> _doors = new();
    private Entity? _doorAhead;
    private Render? _doorRender;
    private double _doorAppearedAt;
    private long _doorTraced;

    private const int DoorCarve = 4;   // cells: a doorway is ~6-8 cells wide
    private readonly HashSet<long> _carvedDoors = new();
    private AreaGrid? _carvedGrid;

    /// <summary>
    /// Doors loaded now (every resolve, 2 Hz): a path test on the entity list, no component reads but Render. Closed ones
    /// (targetable) go to <see cref="_doors"/> for the "Open the door" marker. Every door, open or closed, gets its cells
    /// carved walkable once per grid: the game's pathfinding data keeps a doorway blocked even after the door opens
    /// (read 0 at the open Venom Crypts door, 2026-10-11), which cut the route off from the exit behind it.
    /// </summary>
    private void ScanDoors()
    {
        var grid = _grid;
        if (grid != _carvedGrid) { _carvedDoors.Clear(); _carvedGrid = grid; }
        var carved = false;
        _doors.Clear();
        foreach (var e in GameController.EntityListWrapper.OnlyValidEntities)
        {
            if (e.Path is not { } p || !p.EndsWith("/Door", StringComparison.Ordinal) || e.GetComponent<Render>() is not { } r) continue;
            if (e.IsTargetable) _doors.Add((e, r));
            if (grid != null && _carvedDoors.Add(e.Address)
                && grid.Carve((int)(r.Pos.X / AreaGrid.GridToWorld), (int)(r.Pos.Y / AreaGrid.GridToWorld), DoorCarve)) carved = true;
        }
        // The objective's field was built on the blocked doorway: build it again (cheap, a worker).
        if (carved) { _goalSig = ""; _blockedField = null; _nextResolve = 0; }
    }

    /// <summary>The first closed door within <see cref="DoorNearPath"/> cells of the path's next <see cref="DoorAheadCells"/> cells.</summary>
    private void FindDoorAhead()
    {
        Entity? found = null; Render? fr = null;
        if (_doors.Count > 0 && _frame.Ready)
            for (var i = 0; i < _cells.Count && i < DoorAheadCells && found == null; i += 2)
                foreach (var (e, r) in _doors)
                {
                    if (!e.IsValid || !e.IsTargetable) continue;
                    var dx = r.Pos.X / AreaGrid.GridToWorld - _cells[i].x;
                    var dy = r.Pos.Y / AreaGrid.GridToWorld - _cells[i].y;
                    if (dx * dx + dy * dy <= DoorNearPath * DoorNearPath) { found = e; fr = r; break; }
                }
        if (found?.Address != _doorAhead?.Address) _doorAppearedAt = _clock.Elapsed.TotalSeconds;
        if (found != null && found.Address != _doorTraced) { _doorTraced = found.Address; Trace("door", found.Path); }   // once per door, not per step
        _doorAhead = found; _doorRender = fr;
    }

    /// <summary>The goal entity nearest <paramref name="cell"/> (the path's end, else the player) gets the in-world marker.</summary>
    private void SetGoalEntity((int x, int y) cell)
    {
        var list = _resolved.GoalEntities;
        var best = -1; var bestD = int.MaxValue;
        for (var i = 0; i < list.Count; i++)
        {
            var d = Math.Abs(list[i].X - cell.x) + Math.Abs(list[i].Y - cell.y);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) { _goalEntity = null; _goalRender = null; _frame.HasGoalBox = false; return; }
        var g = list[best];
        if (_goalEntity?.Address != g.Entity.Address) { _goalAppearedAt = _clock.Elapsed.TotalSeconds; _goalEntity = g.Entity; _goalRender = g.Render; }
        _frame.HasGoalBox = true;
        _frame.GoalPos = g.Pos;
        _frame.GoalHalf = g.Bounds;
        _frame.GoalAddress = g.Entity.Address;
    }

    private void ReadFlags()
    {
        var steps = _book.StepsFor(_areaRaw);
        if (!steps.Any(s => s.DoneFlag != null)) return;
        Dictionary<QuestFlag, bool>? all;
        try { all = GameController.IngameState.Data.ServerData?.QuestFlags; }
        catch (Exception ex) { _why = $"ServerData.QuestFlags: {ex.Message}"; return; }
        if (all == null) return;
        foreach (var s in steps)
        {
            if (s.DoneFlag == null) continue;
            _flags[s.DoneFlag] = Enum.TryParse<QuestFlag>(s.DoneFlag, out var f) && all.TryGetValue(f, out var v) && v;
        }
    }

    // ── The world trace: what the player did, recorded as it happens, so a route can be mapped from any play session ──
    // (2026-10-12, the user: "any time there's a required NPC interaction ... any time there's a quest item I must get
    // first, I must tell you first"). Quest flags, NPC dialogs and quest items in the inventory go out as trace events
    // next to the quest states, and into a running heads-up capture.

    private HashSet<QuestFlag>? _flagsSet;          // quest flags set at the last read (null until the first)
    private double _nextAllFlags, _nextWorld;
    private string? _dialogNpc;
    private HashSet<string>? _questItemsHeld;    // "name|path" of quest items carried at the last look

    private void WatchWorld(double now)
    {
        if (now >= _nextAllFlags)
        {
            _nextAllFlags = now + 3;
            if (ReadFlagsSet() is { } set)
            {
                if (_flagsSet != null)
                {
                    var added = set.Where(f => !_flagsSet.Contains(f)).Select(f => f.ToString()).ToList();
                    var cleared = _flagsSet.Where(f => !set.Contains(f)).Select(f => f.ToString()).ToList();
                    if (added.Count > 0) { Trace("flag", string.Join(", ", added)); _capture?.Changed(now); }
                    if (cleared.Count > 0) Trace("flag-cleared", string.Join(", ", cleared));
                }
                _flagsSet = set;
            }
        }
        if (now < _nextWorld) return;
        _nextWorld = now + 0.5;
        string? npc = null;
        try { if (GameController.IngameState.IngameUi.NpcDialog is { IsVisible: true } d) npc = d.NpcName; }
        catch (Exception ex) { _why = $"IngameUi.NpcDialog: {ex.Message}"; }
        if (npc is { Length: > 0 } && npc != _dialogNpc)
        {
            Trace("npc-dialog", npc);
            _capture?.Dialogs.Add(npc);
            _capture?.Changed(now);
        }
        _dialogNpc = npc is { Length: > 0 } ? npc : null;
        RefreshCarried();
        var held = _carriedQuest.Select(q => q.name + "|" + q.path).ToHashSet();
        if (_questItemsHeld != null)
        {
            foreach (var h in held.Where(h => !_questItemsHeld.Contains(h))) { Trace("item-gained", ShowItem(h)); _capture?.Changed(now); }
            foreach (var h in _questItemsHeld.Where(h => !held.Contains(h))) { Trace("item-lost", ShowItem(h)); _capture?.Changed(now); }
        }
        _questItemsHeld = held;
    }

    private static string ShowItem(string nameBarPath) => nameBarPath.Replace("|", " (") + ")";

    /// <summary>Every quest flag set now, by name (ServerData.QuestFlags: ~4700 flags, ~1.4 ms cold, cached by the HUD after).</summary>
    private HashSet<QuestFlag>? ReadFlagsSet()
    {
        try
        {
            var all = GameController.IngameState.Data.ServerData?.QuestFlags;
            if (all == null) return null;
            var set = new HashSet<QuestFlag>();   // enum values: names only for the few that change
            foreach (var (flag, on) in all)
                if (on) set.Add(flag);
            return set;
        }
        catch (Exception ex) { _why = $"ServerData.QuestFlags: {ex.Message}"; return null; }
    }

    // ── Heads-up (F7): "I'm about to do something for a quest" → what changed, to Claude ──

    private bool _headsUpOpen;
    private string _headsUpNote = "";
    private HeadsUpCapture? _capture;
    private static readonly (string intent, string label)[] Intents =
    [
        ("Talk", "Talk to an NPC"), ("PickUp", "Pick up / take an item"), ("Use", "Use an item"), ("Click", "Click something"),
        ("Enter", "Enter / travel"), ("Defeat", "Kill a boss"), ("Other", "Something else"),
    ];

    /// <summary>The things near the player now (places, NPCs, ground items; not monsters): what a click changes is among them.</summary>
    private Dictionary<string, NearThing> NearThings((int x, int y) cell)
    {
        var near = new Dictionary<string, NearThing>();
        foreach (var e in GameController.EntityListWrapper.OnlyValidEntities)
        {
            if (e.Type is EntityType.Monster or EntityType.Effect or EntityType.Player || e.Path is not { Length: > 0 } p) continue;
            var gp = e.GridPos;
            if (Math.Max(Math.Abs(gp.X - cell.x), Math.Abs(gp.Y - cell.y)) > 150) continue;
            var name = e.Type == EntityType.WorldItem ? e.GetComponent<WorldItem>()?.ItemEntity?.GetComponent<Base>()?.Name : e.RenderName;
            var t = new NearThing(e.Type == EntityType.WorldItem ? "WorldItem:" + (e.GetComponent<WorldItem>()?.ItemEntity?.Path ?? p) : p,
                name, (int)gp.X, (int)gp.Y, Sightings.StateOf(e), e.GetComponent<Targetable>() is not { } tg || tg.isTargetable);
            near.TryAdd(t.Key, t);
        }
        return near;
    }

    private void StartHeadsUp(string intent)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var cell = _lastCell.x == int.MinValue ? (0, 0) : _lastCell;
        _capture = new HeadsUpCapture
        {
            Intent = intent, Note = _headsUpNote.Trim() is { Length: > 0 } n ? n : null, StartArea = _areaRaw, StartedAt = now,
            Quests = new Dictionary<string, int>(_quests), Flags = ReadFlagsSet() ?? new HashSet<QuestFlag>(),
            Items = _carriedQuest.Select(q => q.name + "|" + q.path).ToHashSet(), Near = NearThings(cell),
            ObjectiveBefore = _resolved.Label,
        };
        _headsUpNote = "";
        _headsUpOpen = false;
        Trace("heads-up-start", $"{intent}{(_capture.Note != null ? ": " + _capture.Note : "")}");
        Toast("Watching: go ahead. What changed goes to Claude once things settle (the heads-up key again ends it now)", 10);
    }

    private void EndHeadsUp(string endedBy)
    {
        if (_capture is not { } c) return;
        _capture = null;
        var now = _clock.Elapsed.TotalSeconds;
        var dto = new HeadsUpDto
        {
            Intent = c.Intent, Note = c.Note, StartArea = c.StartArea, EndArea = _areaRaw, Seconds = Math.Round(now - c.StartedAt, 1),
            EndedBy = endedBy, Dialogs = c.Dialogs.Distinct().ToList(), Events = c.Events.ToList(),
            ObjectiveBefore = c.ObjectiveBefore, ObjectiveAfter = _resolved.Label,
        };
        foreach (var (id, st) in _quests)
            if (!c.Quests.TryGetValue(id, out var was) || was != st)
            {
                var text = _questTexts.TryGetValue(id, out var tx) ? tx : "";
                var o = _book.Guide.Read(text);
                dto.QuestChanges.Add($"{id}: {(c.Quests.ContainsKey(id) ? was.ToString() : "new")} -> {st} '{text}' [{o.Kind}{(o.Subject != null ? ": " + o.Subject : "")}]");
            }
        foreach (var id in c.Quests.Keys.Where(id => !_quests.ContainsKey(id))) dto.QuestChanges.Add($"{id}: done");
        if (ReadFlagsSet() is { } flags)
        {
            dto.FlagsSet = flags.Where(f => !c.Flags.Contains(f)).Select(f => f.ToString()).ToList();
            dto.FlagsCleared = c.Flags.Where(f => !flags.Contains(f)).Select(f => f.ToString()).ToList();
        }
        _carriedAt = -1;
        RefreshCarried();
        var held = _carriedQuest.Select(q => q.name + "|" + q.path).ToHashSet();
        dto.ItemsGained = held.Where(h => !c.Items.Contains(h)).Select(ShowItem).ToList();
        dto.ItemsLost = c.Items.Where(h => !held.Contains(h)).Select(ShowItem).ToList();
        // Things near you: compared only within one area (another area's entities are all "new").
        if (c.StartArea == _areaRaw && _lastCell.x != int.MinValue) HeadsUpCapture.Diff(dto, c.Near, NearThings(_lastCell));
        var changes = dto.QuestChanges.Count + dto.FlagsSet.Count + dto.ItemsGained.Count + dto.ItemsLost.Count + dto.Dialogs.Count + dto.EntityChanges.Count;
        RouteStateDto? state = null;
        try { state = BuildState(); state.MapPath.Clear(); } catch { /* the capture still goes out */ }
        var fb = new FeedbackDto
        {
            Kind = "feedback", Event = "heads-up", Note = $"{c.Intent}{(c.Note != null ? ": " + c.Note : "")}", Area = _areaRaw, AreaName = _areaName,
            Target = _targetSpec, Label = _resolved.Label, StepIndex = _stepIndex,
            PlayerGrid = _lastCell.x == int.MinValue ? null : [_lastCell.x, _lastCell.y], State = state, Capture = dto,
        };
        // An F8 press's "Watch what I do next": the capture goes out as feedback.capture, Ref = the press (its handler waits for it).
        if (c.Ref is { } pressRef) { fb.Event = "capture"; fb.Ref = pressRef; }
        _feedback.Write(fb);
        var sent = _bridge.Publish(c.Ref != null ? AiBridgeLink.CaptureKind : "feedback", fb, keep: true);
        if (sent is { Ok: true } && c.Ref == null) AwaitClaim(sent);   // a heads-up is a press of its own (a capture is its press's)
        Toast(sent is { Ok: true }
            ? $"Heads-up sent (#{sent.Seq}): {changes} change{(changes == 1 ? "" : "s")} for Claude to map into the route"
            : $"Heads-up kept for Claude ({changes} changes): {_bridge.Why ?? "the AI Bridge did not take it"}", 10);
    }

    /// <summary>The heads-up panel: what you are about to do (one click starts watching), or the running capture.</summary>
    private void DrawHeadsUp(double now)
    {
        if (!_headsUpOpen || DrawHeadsUpStyled(now)) return;   // the compose panel's small sibling (WhatsARoute.Compose.cs)
        ImGui.SetNextWindowPos(new Vector2(Settings.Debug.StatusX.Value, Settings.Debug.StatusY.Value + 110), ImGuiCond.Appearing);
        ImGui.SetNextWindowBgAlpha(0.92f);
        if (!ImGui.Begin("Heads up##WhatsARoute", ref _headsUpOpen, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        ImGui.TextDisabled($"{_areaName}  ·  what are you about to do?");
        for (var i = 0; i < Intents.Length; i++)
        {
            if (i > 0 && i % 4 != 0) ImGui.SameLine();
            if (ImGui.Button(Intents[i].label)) StartHeadsUp(Intents[i].intent);
        }
        ImGui.SetNextItemWidth(360);
        if (ImGui.InputTextWithHint("##headsupnote", "what is it? e.g. 'give the shark fin to Kaimana' (Enter starts)", ref _headsUpNote, 300, ImGuiInputTextFlags.EnterReturnsTrue)
            && _headsUpNote.Trim().Length > 0)
            StartHeadsUp("Other");
        ImGui.End();
    }

    private void ReadQuests()
    {
        _quests.Clear();
        _questTexts.Clear();
        List<string>? changed = null;
        try
        {
            foreach (var (quest, state) in GameController.IngameState.IngameUi.UncompletedQuests)
            {
                if (quest?.Id is not { } id || state == null) continue;
                _quests[id] = state.QuestStateId;
                _questTexts[id] = state.QuestStateText ?? "";
                if (_questsBefore.Count > 0 && (!_questsBefore.TryGetValue(id, out var was) || was != state.QuestStateId))
                {
                    // The kind it was read as goes with it: a wrong kind in the trace is a rule to fix in objective-types.json.
                    var o = _book.Guide.Read(state.QuestStateText);
                    (changed ??= new()).Add($"{id}:{state.QuestStateId} {state.QuestStateText} [{o.Kind}{(o.Subject != null ? ": " + o.Subject : "")}{(o.AreaName != null ? " in " + o.AreaName : "")}]");
                    _capture?.Changed(_clock.Elapsed.TotalSeconds);
                }
            }
            if (_questsBefore.Count > 0)
                foreach (var id in _questsBefore.Keys)
                    if (!_quests.ContainsKey(id)) (changed ??= new()).Add($"{id}:done");
        }
        catch (Exception ex) { _why = $"IngameUi.UncompletedQuests: {ex.Message}"; }
        if (_quests.Count > 0)
        {
            _questsBefore.Clear();
            foreach (var kv in _quests) _questsBefore[kv.Key] = kv.Value;
        }
        if (changed != null) Trace("quest", quests: changed);
        _questObj = null;
        QuestObjective? here = null;
        _altAreas.Clear();
        foreach (var q in _book.Route.Quests)
        {
            if (q.Optional && !Settings.Route.IncludeOptional) continue;
            if (!_quests.TryGetValue(q.Quest, out var st)) continue;
            if (q.States is { Length: > 0 } && Array.IndexOf(q.States, st) < 0) continue;
            if (q.UnlessFlag is { Length: > 0 } uf && (_flagsSet ??= ReadFlagsSet())?.Contains(Enum.TryParse<QuestFlag>(uf, out var ufv) ? ufv : (QuestFlag)(-1)) == true) continue;
            if (_questObj == null) _questObj = q;
            else _altAreas.Add(RouteBook.FirstOf(q.Area));   // other open objectives: the only other choices worth a ring on the map
            // An open objective for the area you are in beats the planned order (Act 4: you sailed to Shrike before
            // Whakapanu; the route must not send you back to the ship).
            // Same quest only: across quests the file order is the plan (Act 3's snake pit before the waterways).
            here ??= q.Quest == _questObj.Quest && q.Area.Length > 0 && RouteBook.LikeAny(_areaRaw, q.Area) ? q : null;
        }
        if (here != null && _questObj != here) { _altAreas.Add(RouteBook.FirstOf(_questObj!.Area)); _questObj = here; }
        // The quests whose own words can lead (route.json Main and Follow; a Quests entry with Target "quest" pins its own).
        _autoHere.Clear(); _autoPlan.Clear();
        if (_questObj is { Target: "quest" } qo)
        {
            if (_questTexts.TryGetValue(qo.Quest, out var tx)) { _autoHere.Add((qo.Quest, _book.Guide.Read(tx))); _autoPlan.Add(_autoHere[0]); }
            return;
        }
        // Acting here: the nearest leads, the story first on a tie (one NPC for both: "hire her ship" over "travel to
        // Kedge Bay"). The plan when nothing is here: the story first.
        var side = Followed(_book.Route.Follow);
        var main = Followed(_book.Route.Main);
        _autoHere.AddRange(main); _autoHere.AddRange(side.Where(s => main.All(m => m.Quest != s.Quest)));
        _autoPlan.AddRange(main); _autoPlan.AddRange(side.Where(s => main.All(m => m.Quest != s.Quest)));
    }

    private List<(string Quest, QuestObjectiveText Read)> Followed(List<string> patterns)
    {
        var list = new List<(string, QuestObjectiveText)>();
        foreach (var f in patterns)
            foreach (var id in _quests.Keys.Where(k => RouteBook.Like(k, f)).OrderBy(k => k, StringComparer.Ordinal))
                if (_questTexts.TryGetValue(id, out var tx) && list.All(x => x.Item1 != id)) list.Add((id, _book.Guide.Read(tx)));
        return list;
    }

    private readonly Dictionary<string, string> _questTexts = new();   // open quest id -> its state's text
    private readonly List<(string Quest, QuestObjectiveText Read)> _autoHere = new(), _autoPlan = new();
    private (string Quest, QuestObjectiveText Read)? _auto;            // the quest whose text leads now, read as an objective
    private bool _textOnly;                                             // the objective has no place: the card says what to do

    private readonly HashSet<string> _altAreas = new(StringComparer.OrdinalIgnoreCase);
    private string? _sailTo;                  // a Sail objective's island: marked on Makoru's map while it is open

    private void ReadTravelMap(double now)
    {
        try { _map.Read(GameController, _areaRaw, now); }
        catch (Exception ex) { _map.Why = $"travel map: {ex.Message}"; }
        _chosenStop = null;
        if (!_map.Open) return;
        if ((_resolved.TravelTo ?? _sailTo) is { } dest)
        {
            foreach (var s in _map.Stops)
                if (s.Unlocked && string.Equals(s.DestId, dest, StringComparison.OrdinalIgnoreCase)) { _chosenStop = s; break; }
            return;
        }
        // No travel or sail in the leading text ("Speak to Makoru to ...", a side quest leading): the plan's next area
        // still gets its stop marked, its alternatives in route.json's order (Isle of Kin before Volcanic Warrens).
        if (_questObj is not { Area.Length: > 0 } plan || RouteBook.LikeAny(_areaRaw, plan.Area)) return;
        foreach (var area in plan.Area.Split('|'))
            if (_map.Stops.FirstOrDefault(s => s.Unlocked && RouteBook.Like(s.DestId, area)) is { } stop) { _chosenStop = stop; return; }
    }

    /// <summary>
    /// A step's filters on the resolved goals, all read from the game now: Within (cells from the player), State (the
    /// goal entity's StateMachine has "name=value"), ExcludeNear (no matching entity in that state within Radius cells:
    /// a seal whose portal is active is lit). Goals and GoalEntities are parallel for entity goals.
    /// </summary>
    private void ApplyStepFilters(RouteStep s, (int x, int y) cell)
    {
        if (s.Within == null && s.State == null && s.ExcludeNear == null && s.PlayerOn == null) return;
        var r = _resolved;
        if (s.PlayerOn is { Length: > 0 } on)
        {
            // Only while standing on that terrain tile (a seal platform): elsewhere the step has nothing to go to.
            var tile = _resolver.TileAt(GameController, _areaRaw, cell);
            if (tile == null || !(RouteBook.Like(tile.Path, on) || RouteBook.Like(tile.Name, on)))
            {
                r.Goals.Clear(); r.GoalEntities.Clear();
                r.Why = $"'{s.Target}': you are not on a '{on}' tile (on {tile?.Path ?? "no tile"})";
                return;
            }
        }
        List<Vector2>? exclude = null;
        if (s.ExcludeNear is { Entity.Length: > 0 } ex)
        {
            exclude = new List<Vector2>();
            foreach (var e in _resolver.Places)
            {
                if (!e.IsValid || e.Path?.Contains(ex.Entity, StringComparison.OrdinalIgnoreCase) != true) continue;
                if (ex.State != null && !HasState(e, ex.State)) continue;
                if (e.GetComponent<Positioned>() is { } p) exclude.Add(p.GridPos);
            }
            // Seen earlier this visit in that state and unloaded since (a lit seal far away stays lit).
            foreach (var sg in _resolver.Sightings.Area)
                if (sg.Path.Contains(ex.Entity, StringComparison.OrdinalIgnoreCase) && (ex.State == null || Sightings.HasState(sg.State, ex.State)))
                    exclude.Add(new Vector2(sg.X, sg.Y));
        }
        var paired = r.GoalEntities.Count == r.Goals.Count;
        for (var i = r.Goals.Count - 1; i >= 0; i--)
        {
            var g = r.Goals[i];
            var drop = s.Within is { } w && Math.Max(Math.Abs(g.x - cell.x), Math.Abs(g.y - cell.y)) > w;
            if (!drop && s.State != null) drop = paired ? !HasState(r.GoalEntities[i].Entity, s.State) : !RememberedInState(g, s.State);
            if (!drop && exclude != null)
                foreach (var p in exclude)
                    if (Vector2.Distance(p, new Vector2(g.x, g.y)) <= s.ExcludeNear!.Radius) { drop = true; break; }
            if (!drop) continue;
            r.Goals.RemoveAt(i);
            if (paired) r.GoalEntities.RemoveAt(i);
        }
        if (r.Goals.Count == 0 && r.Why == null) r.Why = $"'{s.Target}': nothing left after the step's filters (Within/State/ExcludeNear)";
    }

    /// <summary>A remembered goal (its entity unloaded) is in <paramref name="state"/> when its last sighting was.</summary>
    private bool RememberedInState((int x, int y) g, string state)
    {
        foreach (var sg in _resolver.Sightings.Area)
            if (sg.X == g.x && sg.Y == g.y && Sightings.HasState(sg.State, state)) return true;
        return false;
    }

    private static bool HasState(Entity e, string nameEqValue)
    {
        var sm = e.GetComponent<StateMachine>();
        if (sm?.States == null) return false;
        var eq = nameEqValue.IndexOf('=');
        var name = eq < 0 ? nameEqValue : nameEqValue[..eq];
        var want = eq < 0 ? null : nameEqValue[(eq + 1)..];
        foreach (var st in sm.States)
            if (string.Equals(st.Name, name, StringComparison.OrdinalIgnoreCase) && (want == null || st.Value.ToString() == want)) return true;
        return false;
    }

    private bool StepDone(int i, RouteStep s)
    {
        if (_reachedSteps.Contains(StepKey(s)) || (s.DoneFlag != null && _flags.TryGetValue(s.DoneFlag, out var v) && v)) return true;
        if (s.UntilDead is { Length: > 0 } boss && SeenDead(boss)) { _reachedSteps.Add(StepKey(s)); return true; }
        if (s.DoneWhenSeen is not { Entity.Length: > 0 } dw || !SeenEnough(dw)) return false;
        _reachedSteps.Add(StepKey(s));
        return true;
    }

    /// <summary>
    /// What a step is, for "reached": its target and label, not its index. 2026-10-11 Sanctum: indexes made a live edit
    /// that inserted a step hand the old first step's "reached" to the new one, which was then skipped.
    /// </summary>
    private static string StepKey(RouteStep s) => s.Target + "\u0001" + s.Label;

    /// <summary>
    /// A monster whose path contains <paramref name="part"/> loaded and dead (2026-10-11 Oubliette: after Blackjaw died the
    /// step kept pointing at his body). Kills are remembered for the area visit, so the step stays done once you walk off.
    /// </summary>
    private bool SeenDead(string part)
    {
        var key = $"{GameController.Area.CurrentArea?.Hash ?? 0:x8}:{part}";   // per area instance: a new instance has the boss again
        if (_deadSeen.Contains(key)) return true;
        // Dead, and none alive: a boss can have dead copies of itself loaded while it fights (Zicoatl, 2026-10-11: two
        // dead VaalStatueBoss entities next to the living one).
        string? dead = null;
        foreach (var e in GameController.EntityListWrapper.OnlyValidEntities)
        {
            if (e.Type != EntityType.Monster || e.Path?.Contains(part, StringComparison.OrdinalIgnoreCase) != true) continue;
            if (e.IsAlive) return false;
            dead = e.Path;
        }
        if (dead == null) return false;
        _deadSeen.Add(key);
        Trace("boss-dead", dead);
        return true;
    }

    // A boss fight (2026-10-11, the user fighting Zicoatl: "Why is it tracking anything right now? I'm in a boss fight and
    // it's distracting"): a living unique monster this close hides the route in the world until the fight ends.
    private const int BossFightCells = 110;
    private bool _bossFight;

    private void ScanBossFight((int x, int y) cell)
    {
        var fight = false;
        // The current step is "kill this boss" (UntilDead): that boss alive near you is the fight from its first second
        // (2026-10-11 Zicoatl: the fight started at full life and the route kept framing the arena's wall tiles).
        var steps = _book.StepsFor(_areaRaw);
        var bossStep = _stepIndex >= 0 && _stepIndex < steps.Count ? steps[_stepIndex].UntilDead : null;
        foreach (var e in GameController.EntityListWrapper.OnlyValidEntities)
        {
            if (e.Type != EntityType.Monster || !e.IsAlive || !e.IsHostile) continue;
            var stepBoss = bossStep is { Length: > 0 } && e.Path?.Contains(bossStep, StringComparison.OrdinalIgnoreCase) == true;
            if (!stepBoss)
            {
                if (e.Rarity != MonsterRarity.Unique) continue;
                // Any other unique: engaged only. A dormant boss (Zicoatl's statue on the Large Soul Core before it is
                // clicked) is alive at full life and hid the marker on the thing to click.
                if (e.GetComponent<Life>() is not { } life || life.CurHP >= life.MaxHP) continue;
            }
            if (e.GetComponent<Positioned>() is not { } p) continue;
            if (Math.Max(Math.Abs(p.GridPos.X - cell.x), Math.Abs(p.GridPos.Y - cell.y)) <= BossFightCells) { fight = true; break; }
        }
        if (fight != _bossFight) Trace(fight ? "boss-fight" : "boss-fight-over");
        _bossFight = fight;
    }

    private readonly HashSet<string> _deadSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Distinct entity paths matching <paramref name="dw"/> in its state, loaded now or remembered this visit. Loaded
    /// matches are noted first, so a state that changed while the step wasn't resolving (a seal lit while the portal
    /// step showed) still counts after the entity unloads.
    /// </summary>
    private bool SeenEnough(DoneWhenSeenRule dw)
    {
        // Several steps share one rule (all three G2_6 seal steps): answered once per resolve.
        if (_seenCache.TryGetValue(dw, out var known)) return known;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _resolver.Places)
        {
            if (!e.IsValid || e.Path?.Contains(dw.Entity, StringComparison.OrdinalIgnoreCase) != true) continue;
            _resolver.Sightings.Note(e, c => _resolver.TileAt(GameController, _areaRaw, c), _resolver.Now);
            if (dw.State == null || HasState(e, dw.State)) paths.Add(e.Path);
        }
        foreach (var sg in _resolver.Sightings.Area)
            if (sg.Path.Contains(dw.Entity, StringComparison.OrdinalIgnoreCase) && (dw.State == null || Sightings.HasState(sg.State, dw.State)))
                paths.Add(sg.Path);
        return _seenCache[dw] = paths.Count >= dw.Count;
    }

    private readonly Dictionary<DoneWhenSeenRule, bool> _seenCache = new();

    private readonly List<string> _carried = new();
    private readonly List<(string path, string name)> _carriedQuest = new();   // quest items carried: path and base name
    private double _carriedAt = -1;

    /// <summary>The inventories' item paths (and the quest items' names), read at most once a second.</summary>
    private void RefreshCarried()
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _carriedAt < 1) return;
        _carriedAt = now;
        _carried.Clear();
        _carriedQuest.Clear();
        try
        {
            foreach (var inv in GameController.IngameState.Data.ServerData.PlayerInventories)
                if (inv?.Inventory?.Items is { } items)
                    foreach (var it in items)
                    {
                        if (it?.Path is not { } p) continue;
                        _carried.Add(p);
                        if (p.Contains("/QuestItems/", StringComparison.Ordinal)) _carriedQuest.Add((p, it.GetComponent<Base>()?.Name ?? p.Split('/')[^1]));
                    }
        }
        catch (Exception ex) { _why = $"ServerData.PlayerInventories: {ex.Message}"; }
    }

    /// <summary>An item in your inventories whose metadata path contains <paramref name="part"/>.</summary>
    private bool Carrying(string part)
    {
        RefreshCarried();
        foreach (var p in _carried)
            if (p.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The path of a carried quest item whose name contains <paramref name="name"/> ("Tattoo of Tawhoa", "Rakiata's Lesson").</summary>
    private string? CarriedQuestItemNamed(string name)
    {
        RefreshCarried();
        foreach (var (p, n) in _carriedQuest)
            if (n.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(n, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    private const string QuestItemSpec = "item:/QuestItems/";
    private const int QuestItemCells = 150;

    /// <summary>A quest item (Metadata/Items/QuestItems/...) on the ground within <see cref="QuestItemCells"/>, resolved into _resolved and named.</summary>
    // Quest items seen this visit, kept until picked up (2026-10-11 Machinarium: a Soul Core flickered in and out as the
    // target while the player walked at the edge of the 150-cell radius, and was lost once it unloaded). An item that
    // vanishes while you stand next to it was picked up; one that vanishes while you are far away only unloaded.
    private readonly Dictionary<long, (int x, int y, string name)> _questItems = new();
    private const int PickedUpCells = 25;

    private bool QuestItemNear((int x, int y) cell)
    {
        _resolver.Resolve(GameController, _book, QuestItemSpec, _areaRaw, _resolved);
        var loaded = new HashSet<long>();
        for (var i = 0; i < _resolved.GoalEntities.Count; i++)
        {
            var g = _resolved.GoalEntities[i];
            loaded.Add(g.Entity.Address);
            var near = Math.Max(Math.Abs(g.X - cell.x), Math.Abs(g.Y - cell.y)) <= QuestItemCells;
            if (near || _questItems.ContainsKey(g.Entity.Address))
                _questItems[g.Entity.Address] = (g.X, g.Y, g.Entity.GetComponent<WorldItem>()?.ItemEntity?.GetComponent<Base>()?.Name ?? "the quest item");
        }
        foreach (var addr in _questItems.Keys.ToList())
        {
            if (loaded.Contains(addr)) continue;
            var it = _questItems[addr];
            if (Math.Max(Math.Abs(it.x - cell.x), Math.Abs(it.y - cell.y)) <= PickedUpCells) { _questItems.Remove(addr); Trace("quest-item-taken", it.name); }
        }
        if (_questItems.Count == 0) return false;
        // The nearest one leads; all of them are goals (the field picks the closest reachable).
        _resolved.Goals.Clear();
        for (var i = _resolved.GoalEntities.Count - 1; i >= 0; i--)   // loaded ones keep their marker
            if (!_questItems.ContainsKey(_resolved.GoalEntities[i].Entity.Address)) _resolved.GoalEntities.RemoveAt(i);
        var best = default((int x, int y, string name)); var bestD = int.MaxValue;
        foreach (var it in _questItems.Values)
        {
            _resolved.Goals.Add((it.x, it.y));
            var d = Math.Max(Math.Abs(it.x - cell.x), Math.Abs(it.y - cell.y));
            if (d < bestD) { bestD = d; best = it; }
        }
        _resolved.FromEntities = false;
        _resolved.FromMemory = bestD > QuestItemCells;
        _resolved.Label = $"Pick up {best.name}";
        _resolved.Why = null;
        return true;
    }

    private bool _useItem;   // the objective is an item to use from the inventory (no path)
    private string _useItemPart = "";
    private ExileCore2.PoEMemory.Element? _useSlot;   // the item's slot in the inventory panel, while it is open
    private double _nextUseScan;

    /// <summary>
    /// The item to use, framed in the open inventory (2026-10-11, the user: "highlight it in my inventory"). The slot is
    /// looked up 4x per second while the panel is open; its rect is read every frame (one call) so it follows the panel.
    /// </summary>
    private void DrawUseHighlight(Vector3 core, Vector3 glow, double time)
    {
        var panel = GameController.IngameState.IngameUi.InventoryPanel;
        if (panel is not { IsVisible: true }) { _useSlot = null; return; }
        if (time >= _nextUseScan || _useSlot is not { IsValid: true })
        {
            _nextUseScan = time + 0.25;
            _useSlot = null;
            var items = panel[ExileCore2.Shared.Enums.InventoryIndex.PlayerInventory]?.VisibleInventoryItems;
            if (items != null)
                foreach (var it in items)
                    if (it?.Item?.Path?.Contains(_useItemPart, StringComparison.OrdinalIgnoreCase) == true) { _useSlot = it; break; }
        }
        if (_useSlot == null) return;
        var r = _useSlot.GetClientRect();
        HighlightRect(new Vector2(r.X, r.Y), new Vector2(r.X + r.Width, r.Y + r.Height), core, glow, time, "Right-click", below: false);
    }

    /// <summary>A UI thing to click: a pulsing frame with a soft halo and a hint above it (or below, near the screen's top).</summary>
    private static void HighlightRect(Vector2 min, Vector2 max, Vector3 core, Vector3 glow, double time, string hint, bool below)
    {
        var dl = ImGui.GetForegroundDrawList();
        var pulse = 0.55f + 0.45f * MathF.Sin((float)(time * Math.PI * 2 / 1.2));
        for (var k = 3; k >= 1; k--)   // soft halo, widest first
            dl.AddRect(min - new Vector2(k * 3), max + new Vector2(k * 3), Glow.Col(glow, 0.10f * pulse * (4 - k)), 6f + k * 3, ImDrawFlags.None, 3f);
        dl.AddRect(min, max, Glow.Col(core, 0.65f + 0.35f * pulse), 5f, ImDrawFlags.None, 2.2f);
        var size = ImGui.GetFontSize();
        var ts = ImGui.GetFont().CalcTextSizeA(size, float.MaxValue, 0f, hint);
        var tp = new Vector2(MathF.Max((min.X + max.X - ts.X) * 0.5f, 8f), below ? max.Y + 8f : min.Y - ts.Y - 8f);
        dl.AddRectFilled(tp - new Vector2(6, 3), tp + ts + new Vector2(6, 3), Glow.Col(new Vector3(0.04f, 0.06f, 0.07f), 0.88f), 4f);
        dl.AddText(ImGui.GetFont(), size, tp, Glow.Col(core, 0.95f), hint);
    }

    /// <summary>A step of this area still to do (optional ones while they are on).</summary>
    private bool PendingHere(List<RouteStep> steps)
    {
        for (var i = 0; i < steps.Count; i++)
            if (!StepDone(i, steps[i]) && (Settings.Route.IncludeOptional || !steps[i].Optional)) return true;
        return false;
    }

    private bool _shipReturn;   // the objective is the party panel's Return to Ship (Act 4 islands, once they are done)
    private ExileCore2.PoEMemory.Element? _shipButton;
    private double _nextShipScan;

    /// <summary>
    /// The party panel's boat portrait (Return to Ship), while it shows: PartyElement[0][0][1] holds the
    /// NPCPartyPortraitBoat texture, [0][0][0] the "Return to Ship" labels (read live 2026-10-12, Shrike Island).
    /// Looked up 4x per second at most.
    /// </summary>
    private ExileCore2.PoEMemory.Element? ShipButton()
    {
        var time = _clock.Elapsed.TotalSeconds;
        if (time < _nextShipScan && (_shipButton == null || _shipButton is { IsValid: true, IsVisible: true })) return _shipButton;
        _nextShipScan = time + 0.25;
        _shipButton = null;
        try
        {
            if (GameController.IngameState.IngameUi.PartyElement is not { IsVisible: true } party) return null;
            var stack = new Stack<(ExileCore2.PoEMemory.Element e, int depth)>();
            stack.Push((party, 0));
            while (stack.Count > 0)
            {
                var (e, depth) = stack.Pop();
                if (e.TextureName?.Contains("NPCPartyPortraitBoat", StringComparison.OrdinalIgnoreCase) == true)
                    return _shipButton = e.Parent ?? e;
                if (depth >= 5) continue;
                var n = (int)Math.Min(e.ChildCount, 30);
                for (var k = 0; k < n; k++)
                    if (e.GetChildAtIndex(k) is { IsVisible: true } c) stack.Push((c, depth + 1));
            }
        }
        catch (Exception ex) { _why = $"IngameUi.PartyElement: {ex.Message}"; }
        return null;
    }

    /// <summary>The Return to Ship boat, framed and pulsing like the item to use (hint below it: it sits near the top).</summary>
    private void DrawShipHighlight(Vector3 core, Vector3 glow, double time)
    {
        if (_shipButton is not { IsValid: true, IsVisible: true } b) return;
        var r = b.GetClientRect();
        HighlightRect(new Vector2(r.X, r.Y), new Vector2(r.X + r.Width, r.Y + r.Height), core, glow, time, "Click", below: true);
    }

    /// <summary>An objective with nothing to walk to (an item to use, a dialog to wait through): no path, the card says what to do.</summary>
    private void NoPlaceObjective(string spec, string label)
    {
        _stepIndex = -1;
        _targetSpec = spec;
        _resolved.Goals.Clear(); _resolved.GoalEntities.Clear();
        _resolved.Label = label;
        _resolved.Why = null; _resolved.TravelTo = null; _resolved.ViaPortal = false; _resolved.FromMemory = false; _resolved.ViaTravelMap = false;
        _field = null; _goalSig = ""; _frame.Ready = false; _frame.HasGoalBox = false; _frame.Detected = false;
        _goalEntity = null; _goalRender = null;
        if (_targetSpec + "|" + _resolved.Label is var objective && objective != _lastObjective) { _lastObjective = objective; Trace("objective"); }
    }

    private enum AutoUse { No, Spec, TextOnly, UseItem }

    /// <summary>"|target" where route.json's NpcPlaces says <paramref name="name"/> stands in this area (out of sight), else "".</summary>
    private string NpcPlace(string name)
    {
        foreach (var (npc, places) in _book.Route.NpcPlaces)
            if (QuestGuide.NameMatches(npc, name))
                foreach (var (area, target) in places)
                    if (RouteBook.Like(_areaRaw, area)) return "|" + target;
        return "";
    }

    /// <summary>The card's line for a quest sentence: as the game wrote it, without the closing period.</summary>
    private static string Sentence(QuestObjectiveText o) => o.Sentence.TrimEnd('.', ' ');

    /// <summary>
    /// A quest state's sentence as this area's objective (the kinds and their handling: docs/objective-types.md).
    /// Things you act on (an NPC to talk to, an item, a thing to click, a boss in sight, an area named to return to) beat
    /// the area's steps once they resolve; ways onward (enter, travel, explore, a boss not in sight) lead only where the
    /// area has no steps, since the steps are the plan there (optional rewards first, then the exit). Resolves into
    /// _resolved and labels it with the sentence.
    /// </summary>
    private AutoUse AutoObjective(QuestObjectiveText o, bool hasSteps, out string? spec, out bool actHere)
    {
        spec = null;
        actHere = false;
        var act = GameController.Area.CurrentArea?.Act ?? 0;
        var strong = true;
        string? AreaId(string? name) => name == null ? null : _resolver.AreaIdByName(GameController, name, act);
        if (AreaId(o.AreaName) is { } there && !RouteBook.Like(_areaRaw, there))
            { spec = "travel:" + there; strong = false; }   // "Return to Kingsmarch and speak to ...": get there first (the plan, not an act here)
        else
            switch (o.Kind)
            {
                case ObjectiveKind.Talk when o.Subject is { } who && !who.Equals("someone", StringComparison.OrdinalIgnoreCase):
                    spec = $"npc:{who}|object:{who}{NpcPlace(who)}";   // a totem takes rewards too ("Take a Reward from the Hinekora Totem")
                    break;
                case ObjectiveKind.Talk when o.Subject == null && o.Names.Count > 0:
                    spec = string.Join("|", o.Names.Select(n => "npc:" + n)) + string.Concat(o.Names.Select(NpcPlace));   // "Take a reward from her": Kaimana, named earlier
                    break;
                case ObjectiveKind.Use when o.Subject == null && o.Names.Select(CarriedQuestItemNamed).FirstOrDefault(p => p != null) is { } named:
                    _useItemPart = named;                // "... rewarded you with a Tattoo of Hinekora. Use it."
                    actHere = true; return AutoUse.UseItem;
                case ObjectiveKind.Defeat when o.Subject == null:
                    spec = string.Join("|", o.Names.Select(n => "monster:" + n).Append("radar:Boss*|radar:*Boss*|radar:Arena"));
                    strong = false;
                    break;
                case ObjectiveKind.Sail:
                    spec = "npc:Makoru|npc:Rhodri" + NpcPlace("Makoru");   // the ship's captain opens the sailing map
                    // On the island the plan sails to, the crew stands on the ship you landed with: the island's steps lead
                    // (2026-10-12, the user on the Isle of Kin: "it tells me to go to Makoru instead, wrong").
                    if (_questObj is { } sailPlan && RouteBook.LikeAny(_areaRaw, sailPlan.Area)) strong = false;
                    break;
                case ObjectiveKind.PickUp:
                    spec = o.Subject is { } it ? $"item:{it}|{QuestItemSpec}" : QuestItemSpec;
                    break;
                case ObjectiveKind.Use when o.Subject is { } use:
                    if (CarriedQuestItemNamed(use) is { } path) { _useItemPart = path; actHere = true; return AutoUse.UseItem; }
                    spec = "object:" + use;             // "Use the Verisium Anvil": a thing in the world
                    break;
                case ObjectiveKind.Click when o.Subject is { } thing:
                    spec = "object:" + thing;
                    break;
                case ObjectiveKind.Defeat when o.Subject is { } foe:
                    spec = $"monster:{foe}|radar:Boss*|radar:*Boss*|radar:Arena";
                    strong = false;                     // strong only once the monster itself is in sight (below)
                    break;
                case ObjectiveKind.Enter or ObjectiveKind.Travel or ObjectiveKind.Explore when o.Subject is { } where:
                    if (AreaId(where) is { } id) { if (!RouteBook.Like(_areaRaw, id)) spec = "travel:" + id; strong = false; }
                    else if (o.Kind == ObjectiveKind.Enter) { spec = "transition:" + where; strong = false; }
                    else if (o.Kind == ObjectiveKind.Travel) spec = "npc:" + where;   // "Return to Dannig": a person, not an area
                    else strong = false;
                    break;
            }
        if (spec == null)
        {
            // Nothing to walk to: the area's steps lead where there are some (explore, find), else the sentence alone
            // (witness, survive, summon) instead of the old "Exit" fallback that sent the player anywhere.
            return hasSteps && o.Kind is not (ObjectiveKind.Wait or ObjectiveKind.Summon) ? AutoUse.No : AutoUse.TextOnly;
        }
        var rt = Stopwatch.GetTimestamp();
        _resolver.Resolve(GameController, _book, spec, _areaRaw, _resolved);
        _resolveUs = _resolveUs * 0.8 + 0.2 * Stopwatch.GetElapsedTime(rt).TotalMicroseconds;
        var found = _resolved.Goals.Count > 0;
        if (o.Kind == ObjectiveKind.Defeat && _resolved.GoalEntities.Any(g => g.Entity.Type == EntityType.Monster)) strong = true;
        actHere = strong && found;
        if (hasSteps && !actHere) return AutoUse.No;
        if (!hasSteps && !found && o.Kind is ObjectiveKind.Explore) return AutoUse.TextOnly;
        if (o.Kind == ObjectiveKind.Sail && _questObj is { } plan && !RouteBook.LikeAny(_areaRaw, plan.Area))
        {
            // Which island: the plan's next one (route.json's island order), named on the card and marked on Makoru's map.
            _sailTo = RouteBook.FirstOf(plan.Area);
            _resolved.Label = $"Speak to Makoru: set sail for {_resolver.AreaName(GameController, _sailTo)}";
        }
        else if (_resolved.TravelTo == null) _resolved.Label = Sentence(o);   // a travel keeps "Exit to X" / "Travel to X": the way, not the deed
        else if (o.AreaName != null) _resolved.Label = $"{Sentence(o)}: {_resolved.Label}";
        return AutoUse.Spec;
    }

    private void ResolveObjective(AreaGrid grid, (int x, int y) cell)
    {
        string spec;
        var steps = _book.StepsFor(_areaRaw);
        var route = Settings.Route.Mode.Value == "Route";
        var q = route ? _questObj : null;
        _useItem = false;
        _textOnly = false;
        _shipReturn = false;
        _sailTo = null;
        // An agent's route supersedes the normal one while it runs (WhatsARoute.AgentRoute.cs); the normal one is only paused.
        switch (AgentObjective(cell, out spec))
        {
            case 1: goto Resolved;
            case 2: return;
        }
        if (route)
            foreach (var u in _book.Route.Use)
            {
                if (u.Item.Length == 0 || !Carrying(u.Item)) continue;
                // A reward to use from the inventory (2026-10-11, the user after Blackjaw: "I looted the flame core, the next
                // step should be to open my inventory, then use it"): nothing to walk to, the card says what to do.
                _useItem = true;
                _useItemPart = u.Item;
                NoPlaceObjective("use:" + u.Item, u.Label ?? "Use it from your inventory");
                return;
            }
        if (Settings.Route.Mode.Value == "Route" && Settings.Route.PickUpQuestItems.Value && QuestItemNear(cell))
        {
            // A quest item on the ground beats the plan (2026-10-11, the user after Xyclucian: "detect quest items on the
            // ground in general and ask me to pick them up"). Gone from the ground = picked up, and the plan resumes.
            _stepIndex = -1;
            spec = QuestItemSpec;
            _targetSpec = spec;
            _frame.Detected = true;
            goto Resolved;
        }
        _frame.Detected = false;
        _auto = null;
        if (route && _autoHere.Count + _autoPlan.Count > 0)
        {
            // Steps still to do here: they are the plan in this area, so only acting here beats them.
            var pending = PendingHere(steps);
            // 1. A followed quest's thing right here (an NPC in sight, an item, a thing to click, a boss in view).
            // 2. Else, with no route.json objective for the state: the story's own words (travel, enter, wait...).
            AutoUse use = AutoUse.No; string? autoSpec = null;
            // Several can act here (Makoru for the story, Farrow for a side quest, both in town): the nearest leads, so
            // the player standing at the ship isn't sent across town first (2026-10-12). An item to use is nearest of all.
            var bestD = int.MaxValue;
            (string, QuestObjectiveText)? lastTried = null;
            foreach (var c in _autoHere)
            {
                // Only kinds that can act here are tried (each try is a resolve, twice a second).
                if (c.Read.Kind is not (ObjectiveKind.Talk or ObjectiveKind.PickUp or ObjectiveKind.Use or ObjectiveKind.Click
                    or ObjectiveKind.Defeat or ObjectiveKind.Sail or ObjectiveKind.Travel)) continue;
                var tried = AutoObjective(c.Read, pending, out var triedSpec, out var acts);
                lastTried = c;
                if (tried == AutoUse.No || !acts) continue;
                var d = tried == AutoUse.UseItem ? 0 : int.MaxValue - 1;
                foreach (var g in _resolved.Goals) d = Math.Min(d, Math.Max(Math.Abs(g.x - cell.x), Math.Abs(g.y - cell.y)));
                // Something loaded beats a remembered place or a tile (NpcPlaces): those can be nearer only on paper.
                if (tried != AutoUse.UseItem && (_resolved.FromMemory || !_resolved.FromEntities)) d += 100_000;
                if (d < bestD) { bestD = d; _auto = c; use = tried; autoSpec = triedSpec; }
            }
            // _resolved holds the last one tried: resolve the winner again unless it was the last.
            if (_auto is { } near && !Equals(lastTried, near)) use = AutoObjective(near.Read, pending, out autoSpec, out _);
            if (_auto == null && (q == null || q.Target == "quest"))
                foreach (var c in _autoPlan)
                    if ((use = AutoObjective(c.Read, pending, out autoSpec, out _)) != AutoUse.No) { _auto = c; break; }
            if (_auto is { } auto)
                switch (use)
                {
                    case AutoUse.TextOnly:
                        _textOnly = true;
                        NoPlaceObjective("quest:" + auto.Quest, Sentence(auto.Read));
                        return;
                    case AutoUse.UseItem:
                        _useItem = true;
                        NoPlaceObjective("use:" + _useItemPart, Sentence(auto.Read));
                        return;
                    case AutoUse.Spec:
                        _stepIndex = -1;
                        spec = autoSpec!;
                        _targetSpec = spec;
                        goto Resolved;   // AutoObjective resolved it, label included
                }
        }
        // A "quest" entry that its text did not place: its Area travel still applies, else the area's steps lead.
        if (q is { Target: "quest" } && (q.Area is "" or "*" || RouteBook.LikeAny(_areaRaw, q.Area))) q = null;
        if (q != null && !RouteBook.LikeAny(_areaRaw, q.Area))
        {
            _stepIndex = -1;
            spec = "travel:" + RouteBook.FirstOf(q.Area);
            if (q.Via != null)
                foreach (var (from, via) in q.Via)
                    if (RouteBook.Like(_areaRaw, from)) { spec = via; break; }
        }
        else if (q?.Target is { Length: > 0 } qt)
        {
            // In the quest's area its target is the objective: the quest state is live, the area's steps are a plan.
            _stepIndex = -1;
            spec = qt;
        }
        else if (Settings.Route.Mode.Value == "Route" && steps.Count > 0)
        {
            // The first step that is not done and, for SkipIfNone steps, has something to go to right now (after its
            // filters). Stateless: "take the seal's portal back" exists only while a lit seal's portal is near you.
            var rt = Stopwatch.GetTimestamp();
            var first = -1;
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                if (StepDone(i, s) || (!Settings.Route.IncludeOptional && s.Optional)) continue;
                if (s.Carrying is { Length: > 0 } carry && !Carrying(carry)) continue;
                if (s.SkipIfNone && _stepShift == 0)
                {
                    _resolver.Resolve(GameController, _book, s.Target, _areaRaw, _resolved);
                    ApplyStepFilters(s, cell);
                    if (_resolved.Goals.Count == 0) continue;
                    first = i;
                    break;
                }
                first = i;
                break;
            }
            if (first < 0) first = steps.Count - 1;
            _stepIndex = Math.Clamp(first + _stepShift, 0, steps.Count - 1);
            spec = steps[_stepIndex].Target;
            _targetSpec = spec;
            if (!(steps[_stepIndex].SkipIfNone && _stepShift == 0 && _resolved.Goals.Count > 0))
            {
                _resolver.Resolve(GameController, _book, spec, _areaRaw, _resolved);
                ApplyStepFilters(steps[_stepIndex], cell);
            }
            _resolveUs = _resolveUs * 0.8 + 0.2 * Stopwatch.GetElapsedTime(rt).TotalMicroseconds;
            goto Resolved;
        }
        else
        {
            _stepIndex = -1;
            spec = Settings.Route.ManualTarget.Value ?? "";
        }
        _targetSpec = spec;
        var rt0 = Stopwatch.GetTimestamp();
        _resolver.Resolve(GameController, _book, spec, _areaRaw, _resolved);
        _resolveUs = _resolveUs * 0.8 + 0.2 * Stopwatch.GetElapsedTime(rt0).TotalMicroseconds;
        Resolved:
        if (_stepIndex >= 0 && steps[_stepIndex].Label is { Length: > 0 } label) _resolved.Label = label;
        else if (q?.Label is { Length: > 0 } ql && spec == q.Target) _resolved.Label = ql;
        if (route && !_agent.OnCard &&!_frame.Detected && !PendingHere(steps) && ShipButton() != null
            && (_resolved.Goals.Count == 0 || _stepIndex >= 0 || _blockedField != null || spec.StartsWith("travel:", StringComparison.Ordinal)))
        {
            // Done on this island: the party panel's boat (Return to Ship) shows up only then (2026-10-12, the user after
            // the Scourge of the Skies: "highlight it animated for me to click it as the next step"). It beats a done
            // step's leftover target, a place out of reach and a trip elsewhere, not a quest thing to act on right here.
            _shipReturn = true;
            NoPlaceObjective("ui:Return to Ship", "Click Return to Ship (the boat at the top left)");
            return;
        }
        if (spec + "|" + _resolved.Label is var objective && objective != _lastObjective) { _lastObjective = objective; Trace("objective"); }
        if (_blockedField != null)
        {
            // Still cut off from this objective: the nearest exit reachable from here (loaded or seen this visit).
            if (_blockedSpec != spec || _blockedField.Grid != grid || _blockedField.Snap(cell) != null) _blockedField = null;
            else
            {
                var target = _resolved.Label;
                var portal = _resolved.ViaPortal;
                _resolver.Resolve(GameController, _book, "transition:", _areaRaw, _resolved);
                _resolved.ViaPortal = portal;   // a portal still beats walking out of the arena first
                _resolved.Label = $"Way out: {target} can't be reached from here";
                if (_resolved.Goals.Count == 0)
                {
                    // Landed on an Act 4 island: the ship's rope ladder (a quest object, not an area transition) is the way
                    // onto the island (2026-10-12, the user on the Isle of Kin: "get down near the ladder").
                    _resolver.Resolve(GameController, _book, "entity:ShipRopeLadderTransition", _areaRaw, _resolved);
                    _resolved.ViaPortal = portal;
                    _resolved.Label = _resolved.Goals.Count > 0 ? $"Climb down the rope ladder: {target}" : $"Way out: {target} can't be reached from here";
                }
                if (_resolved.Goals.Count == 0) _resolved.Why = $"cut off from '{target}' and no exit seen on this part of the area";
                spec = "exit-from:" + spec;
            }
        }
        if (_resolved.Goals.Count == 0)
        {
            _why = _resolved.Why;
            if (_resolved.FromEntities && _field != null && _goalSig.StartsWith(spec + "|")) return;   // keep the last known goal
            _field = null; _goalSig = ""; _frame.Ready = false; _frame.HasGoalBox = false; _goalEntity = null; _goalRender = null;
            return;
        }
        var sig = spec + "|" + string.Join(";", _resolved.Goals.OrderBy(g => g.x).ThenBy(g => g.y).Select(g => $"{g.x},{g.y}"));
        if (sig == _goalSig) return;
        // The marker goes on the goal entity nearest the player now; Replan moves it to the one the path ends at.
        SetGoalEntity(cell);
        _goalSig = sig;
        _fieldCts.Cancel();
        _fieldCts = new CancellationTokenSource();
        var ct = _fieldCts.Token;
        var goals = _resolved.Goals.ToArray();
        var sample = _dumpBase == null ? null : new GoalSampleDto { Spec = spec, Label = _resolved.Label, Goals = goals.Select(g => new[] { g.x, g.y }).ToList(), Player = [cell.x, cell.y] };
        var dumpBase = _dumpBase;
        Task.Run(() =>
        {
            var f = FlowField.Build(grid, goals, ct, out var fieldWhy);
            if (ct.IsCancellationRequested) return;
            if (f == null) { _why = $"cost map: {fieldWhy}"; return; }
            _field = f;
            if (sample != null) { sample.FieldBuildMs = Math.Round(f.BuildMs, 1); SaveGoalSample(dumpBase! + ".goals.json", sample); }
        }, ct);
    }

    private string? _dumpBase;   // terrain\<area>_<hash> while "Save terrain for benchmarks" is on
    private static readonly object GoalFileLock = new();

    /// <summary>Appends one objective (spec, goals, player cell, live field time) to the area's goals file, for replay.</summary>
    private void SaveGoalSample(string file, GoalSampleDto sample)
    {
        try
        {
            lock (GoalFileLock)
            {
                var list = System.IO.File.Exists(file)
                    ? JsonConvert.DeserializeObject<List<GoalSampleDto>>(System.IO.File.ReadAllText(file)) ?? new()
                    : new List<GoalSampleDto>();
                if (list.Count >= 50) return;
                list.Add(sample);
                System.IO.File.WriteAllText(file, JsonConvert.SerializeObject(list, Formatting.Indented));
            }
        }
        catch (Exception ex) { _why = $"saving goals: {ex.Message}"; }
    }

    public override void Render()
    {
        var t0 = Stopwatch.GetTimestamp();
        try { RenderBody(); }
        catch (Exception ex) { _why = $"render: {ex.Message}"; }
        _renderUs = _renderUs * 0.9 + 0.1 * Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
    }

    private readonly List<ExileCore2.PoEMemory.Element> _bigPanels = new(24);
    private long _bigPanelsUi;

    // The panel flag read straight from the element: the HUD's own Flags offset and IsVisibleLocal bit (GameOffsets2,
    // the struct its Element frame cache reads), one 8-byte read per panel. IsVisibleLocal refreshes the whole 1288-byte
    // element struct for one bit: measured live 2026-10-11 for the 19 panels, 2.15 KB and ~15 us per frame, against
    // 1.3 KB and ~4 us here.
    private static readonly string? PanelFlagsOffsetWhy;
    private static readonly long PanelFlagsOffset = FlagsOffset(out PanelFlagsOffsetWhy);
    private const ulong PanelVisibleBit = (ulong)GameOffsets2.ElementFlags.IsVisibleLocal;
    private readonly ulong[] _panelFlags = new ulong[1];
    // Off: live 2026-10-11 in the Jungle Ruins the raw read said a big panel was open while IsVisibleLocal saw none,
    // and the mismatch check never switched it off, so the route and the card drew nothing at all ("nothing shows us
    // what to click"). IsVisibleLocal is allocation-free already; the raw read comes back only with that fixed.
    private string? _rawPanelFlagsBroken = PanelFlagsOffsetWhy ?? "raw read off: it reported a big panel open while none was visible (2026-10-11)";
    private int _panelMismatches;           // checks where the raw read and IsVisibleLocal disagreed, this HUD run
    private double _nextPanelCheck;         // the raw read is checked against IsVisibleLocal once a second

    private static long FlagsOffset(out string? why)
    {
        why = null;
        try { return System.Runtime.InteropServices.Marshal.OffsetOf<GameOffsets2.ElementOffsets>(nameof(GameOffsets2.ElementOffsets.Flags)).ToInt64(); }
        catch (Exception ex) { why = $"GameOffsets2.ElementOffsets.Flags has no offset: {ex.Message}"; return -1; }
    }

    /// <summary>
    /// Any fullscreen or large panel open (passive tree, inventory, stash...): the world drawing hides. These are
    /// top-level panels whose parent is always shown, so their own flag (IsVisibleLocal) is the answer: IsVisible
    /// walks the parents and allocated ~3 KB per panel per frame (profiled 2026-10-10: 12.5 KB and 46 us per frame
    /// for the 19 panels). The panel lists are fetched again only when the UI object changes. The flag is read raw
    /// (see <see cref="PanelFlagsOffset"/>) and checked against the HUD's IsVisibleLocal when the lists are fetched and
    /// once a second; a check frame answers with IsVisibleLocal. A check that disagrees can be a panel opening between
    /// the HUD's frame-cached read and this live one; three of them mean a HUD update moved the bit: IsVisibleLocal from
    /// then on, and the log and GetState (Timings.PanelCheck) say which link broke. A read that fails (a panel freed
    /// during a load) or no ExileCore2.Memory is "not now": that frame uses IsVisibleLocal.
    /// </summary>
    private bool BigPanelOpen()
    {
        var ui = GameController.IngameState.IngameUi;
        if (ui.Address != _bigPanelsUi || _bigPanels.Count == 0)
        {
            _bigPanels.Clear();
            _bigPanels.AddRange(ui.FullscreenPanels);
            _bigPanels.AddRange(ui.LargePanels);
            _bigPanelsUi = ui.Address;
            _nextPanelCheck = 0;
        }
        var now = _clock.Elapsed.TotalSeconds;
        var mem = GameController.Memory as ExileCore2.Memory;
        if (_rawPanelFlagsBroken == null && now < _nextPanelCheck && mem != null)
        {
            var read = true;
            foreach (var p in _bigPanels)
            {
                if (p == null || p.Address == 0 || !mem.ReadMem<ulong>((IntPtr)(p.Address + PanelFlagsOffset), _panelFlags)) { read = false; break; }
                if ((_panelFlags[0] & PanelVisibleBit) != 0) return true;
            }
            if (read) return false;
        }
        var check = _rawPanelFlagsBroken == null && now >= _nextPanelCheck && mem != null;
        if (check) _nextPanelCheck = now + 1;
        var open = false;
        for (var i = 0; i < _bigPanels.Count; i++)
        {
            var p = _bigPanels[i];
            if (p == null) continue;
            open |= p.IsVisibleLocal;
            if (!check) { if (open) return true; continue; }
            if (p.Address == 0 || !mem!.ReadMem<ulong>((IntPtr)(p.Address + PanelFlagsOffset), _panelFlags)) continue;
            var raw = (_panelFlags[0] & PanelVisibleBit) != 0;
            var vis = p.IsVisibleLocal;
            if (raw == vis) continue;
            check = false;   // one count per check
            if (++_panelMismatches < 3) continue;
            _rawPanelFlagsBroken = $"panel {i} ({p.GetType().Name} at {p.Address:X}): Flags (+{PanelFlagsOffset}) bit 0x{PanelVisibleBit:X} reads {raw}, IsVisibleLocal {vis} ({_panelMismatches} disagreeing checks)";
            LogError($"WhatsARoute: the big-panel check reads IsVisibleLocal from now on: {_rawPanelFlagsBroken}", 10);
        }
        return open;
    }

    private SubMap? _largeMap;   // one object per LargeMap element: AsObject made a new one (and read it afresh) every frame, ~1.8 KB

    private void RenderBody()
    {
        // The overlay is a topmost window: with another window in front of the game, everything drawn here lands on
        // that window. Not IsForeGroundCache: the core's ForceForeground (on, so the bridge answers while the user is
        // elsewhere) makes it always true. The HUD's own window counts as the game, so its menu keeps showing the route.
        if (!GameController.IsForeGroundCache || !GameInFront.Check(GameController.Window.Process.Id)) return;
        if (Settings.Route.NextStep.PressedOnce()) { if (!AgentStepKey(+1)) _stepShift++; _nextResolve = 0; }   // an agent route that leads takes the keys
        if (Settings.Route.PreviousStep.PressedOnce()) { if (!AgentStepKey(-1)) _stepShift--; _nextResolve = 0; }
        if (Settings.Route.Feedback.PressedOnce()) _feedbackOpen = !_feedbackOpen;
        if (Settings.Route.HeadsUp.PressedOnce())
        {
            if (_capture != null) EndHeadsUp("press");
            else _headsUpOpen = !_headsUpOpen;
        }
        if (!GameController.InGame) return;
        var time = _clock.Elapsed.TotalSeconds;
        DrawFeedback();
        DrawHeadsUp(time);
        DrawToast(time);
        var core = Glow.Rgb(Settings.World.PathCore.Value);
        var glow = Glow.Rgb(Settings.World.PathGlow.Value);
        if (_map.Open && _chosenStop is { } stop) DrawTravelMap(stop, core, glow, time);
        else _stopShown = -1;
        // The frame's colours, resolved once: a detected objective (a quest item on the ground) gets the theme's green
        // sibling for the ribbon, the large-map line and its own marker. The travel map above and the door marker
        // below keep the plain theme: they are the planned road, not the found thing.
        Glow.Theme(Settings.World, _frame.Detected, out var fCore, out var fGlow);
        AgentColours(ref fCore, ref fGlow, time);   // an agent route on the card: the owner's hue
        if (_useItem) DrawUseHighlight(core, glow, time);   // before the big-panel check: the inventory is one
        if (BigPanelOpen()) return;
        if (_shipReturn) DrawShipHighlight(core, glow, time);
        var avoidUi = Settings.World.AvoidGameUi.Value;
        if (avoidUi)
        {
            _mask.Refresh(GameController, time, ImGui.GetIO().DisplaySize);
            if (_mask.Paused) return;
            if (Settings.Debug.ShowUiMask) _mask.DrawOutlines();
        }
        if (Settings.Debug.ShowStatus) DrawStatus();
        // A boss fight hides the ribbon and the door marker; a thing to click that is not a monster keeps its marker (the
        // user, 2026-10-11: "If it's clickable, you highlight it for me ... text only is reserved for when we can't give
        // any directions").
        if (_bossFight && (_goalEntity is not { IsValid: true } ge || ge.Type == EntityType.Monster)) return;
        var pr = _playerRender;
        if (pr == null) return;

        // The view at draw time: fresh camera and feet (FreshView), else the HUD's per-frame copy, drawn between the last
        // two game frames (smooth motion, ViewMotion) or as of DelayMs ago.
        _fresh.Sample(GameController, pr, Settings.World.DelayMs.Value, Settings.World.FreshView.Value, Settings.World.SmoothMotion.Value,
            out var m, out var half, out var player);
        var disp = ImGui.GetIO().DisplaySize;
        var largeMapEl = GameController.IngameState.IngameUi.Map.LargeMap;
        if (_largeMap == null || _largeMap.Address != largeMapEl.Address) _largeMap = largeMapEl.AsObject<SubMap>();
        var largeMap = _largeMap;
        var open = largeMap.IsVisible;

        // The thing to click, at its live position (a transition can move: Pos is read every frame from the cached
        // component). Render.Pos is the CENTRE of the model's box and Bounds.Z HALF its height (up is -Z): the
        // player's Pos.Z + Bounds.Z equals the terrain height exactly; a caravan door's is ~23 units off its ground
        // cell. The pad goes on the ground the path uses at that cell (AreaGrid Z), falling back to Pos.Z + Bounds.Z,
        // and the pin rises by the full height (2 x Bounds.Z). Measured 2026-10-10 by the Valley exit: drawn at Pos
        // the marker sat 110 px above the doorway floor.
        // A model-less goal (Positioned + ProximityTrigger only, e.g. a point of interest inside blocked ground) has
        // no Render: the pad then sits where the path ends (the nearest walkable cell), with nothing to frame.
        var showMarker = _frame.HasGoalBox && Settings.World.ShowGoalMarker.Value && !(open && Settings.World.HideWithLargeMap.Value);
        var modelless = false;
        if (showMarker)
        {
            if (_goalEntity is not { IsValid: true }) { _frame.HasGoalBox = false; showMarker = false; }
            else if (_goalRender is { } gr)
            {
                var live = gr.Pos;
                var g = _grid;
                var ground = g != null && g.InBounds((int)(live.X / AreaGrid.GridToWorld), (int)(live.Y / AreaGrid.GridToWorld))
                    ? g.HeightAt(live.X / AreaGrid.GridToWorld, live.Y / AreaGrid.GridToWorld)
                    : live.Z + _frame.GoalHalf.Z;
                _frame.GoalPos = new Vector3(live.X, live.Y, ground);
            }
            else if (_frame.Goal is { } pathEnd) { _frame.GoalPos = pathEnd; modelless = true; }
            else showMarker = false;   // the path's end is beyond the near part: the ribbon leads the way until then
        }
        // Approach emphasis: 0 beyond ~600 world units from the goal, 1 within 150 (the doorstep).
        Vector3? goal = showMarker ? _frame.GoalPos : _frame.Goal;
        var near = goal is { } gp ? 1f - Glow.Smooth((Vector2.Distance(new Vector2(player.X, player.Y), new Vector2(gp.X, gp.Y)) - 150f) / 450f) : 0f;

        var bg = ImGui.GetBackgroundDrawList();
        if (avoidUi) _mask.Begin(bg);
        if (_frame.Ready && !_bossFight)
            _renderer.Draw(_frame, _edges, Settings, m, half, player, goal, near, fCore, fGlow, open, open ? largeMap.MapCenter : default, open ? largeMap.MapScale : 0, time);
        if (!_bossFight && _doorAhead is { IsValid: true, IsTargetable: true } door && _doorRender is { } dr && Settings.World.ShowGoalMarker.Value)
        {
            var dl = ImGui.GetBackgroundDrawList();
            var p = dr.Pos;
            var g = _grid;
            var z = g != null && g.InBounds((int)(p.X / AreaGrid.GridToWorld), (int)(p.Y / AreaGrid.GridToWorld)) ? g.HeightAt(p.X / AreaGrid.GridToWorld, p.Y / AreaGrid.GridToWorld) : p.Z;
            _markers.DrawEntity(dl, m, half, disp, new Vector3(p.X, p.Y, z), MathF.Max(dr.Bounds.X, dr.Bounds.Y) * 0.8f, dr.Bounds.Z * 2f,
                "Open the door", core, glow, time, _doorAppearedAt, 1f);
        }
        if (showMarker)
        {
            var dl = ImGui.GetBackgroundDrawList();
            if ((dl.Flags & ImDrawListFlags.AntiAliasedLines) == 0) dl.Flags |= ImDrawListFlags.AntiAliasedLines;
            _markers.DrawEntity(dl, m, half, disp, _frame.GoalPos, modelless ? 40f : MathF.Max(_frame.GoalHalf.X, _frame.GoalHalf.Y) * 0.8f,
                modelless ? 0f : _frame.GoalHalf.Z * 2f, _frame.Label, fCore, fGlow, time, _goalAppearedAt, near);
        }
        if (avoidUi) _mask.End(bg);
    }

    /// <summary>
    /// The Desert Map: the marker glued to the stop's live rect (one GetClientRect per frame; the 4 Hz scan only
    /// identifies the nodes) and a quiet ring on the other unlocked stops. Nothing is drawn while the node no longer
    /// points at its area row (the map was rebuilt): the next scan picks it up again.
    /// </summary>
    private void DrawTravelMap(WorldMapReader.Stop stop, Vector3 core, Vector3 glow, double time)
    {
        if (!Settings.Travel.MarkChosenStop.Value) return;
        if (!_map.Refresh(GameController, stop)) return;
        if (_stopShown != stop.Index) { _stopShown = stop.Index; _stopAppearedAt = time; }
        var dl = ImGui.GetForegroundDrawList();
        if ((dl.Flags & ImDrawListFlags.AntiAliasedLines) == 0) dl.Flags |= ImDrawListFlags.AntiAliasedLines;
        var disp = ImGui.GetIO().DisplaySize;
        if (Settings.Travel.MarkOtherStops.Value)
            foreach (var s in _map.Stops)
                if (s != stop && s.Unlocked && _altAreas.Contains(s.DestId)) Markers.DrawGhostStop(dl, s.Center, s.Size, glow);
        _markers.DrawStop(dl, stop.Center, stop.Size, disp, stop.DestName.Length > 0 ? stop.DestName : _resolved.Label, core, glow, time, _stopAppearedAt);
    }

    private readonly StatusPanel _status = new();
    private StatusPanel.Input _statusIn;
    private string _statusGoalLabel = "";     // the objective's own name, kept from before the route turned to an exit
    private string? _portalKey;              // "Shift+T": the player's town portal binding, for the card
    private string? _statusDetail;
    private double _nextStatus;

    /// <summary>
    /// The objective card (Rendering/StatusPanel): the plugin's state is sampled 4x per second; the panel rebuilds its
    /// strings only when something it shows changed, and a frame is a few draw calls. The dev dump (timings, the raw
    /// reason) is built only while Debug "Detailed status" is on.
    /// </summary>
    private void DrawStatus()
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now >= _nextStatus)
        {
            _nextStatus = now + 0.25;
            var steps = _book.StepsFor(_areaRaw);
            var includeOptional = Settings.Route.IncludeOptional.Value;
            var shown = 0; var current = -1; var done = 0UL;
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                if (s.Optional && !includeOptional) continue;
                if (i == _stepIndex) current = shown;
                if (shown < 64 && StepReached(i, s)) done |= 1UL << shown;
                shown++;
            }
            if (_blockedField == null && _resolved.Label.Length > 0) _statusGoalLabel = _resolved.Label;
            var route = Settings.Route.Mode.Value == "Route";
            var grid = _grid;
            var field = _field;
            var travel = _resolved.TravelTo != null;
            var questTarget = route && _stepIndex < 0 && (_questObj != null || _auto != null) && !travel;
            if (Settings.Debug.DetailedStatus.Value)
                _statusDetail = $"{_areaRaw}  ·  {(_stepIndex >= 0 ? $"step {_stepIndex + 1}/{steps.Count}  ·  " : "")}{_targetSpec}\n"
                    + $"{_pathLen:0} units  ·  goals {_resolved.Goals.Count}  ·  field {field?.BuildMs:0} ms  ·  edges {_frame.EdgePieces.Count}\n"
                    + $"replan {_replanUs:0} µs  ·  tick {_tickUs:0} µs  ·  render {_renderUs:0} µs  ·  resolve {_resolveUs:0} µs\n"
                    + $"view {(_fresh.Fresh ? "fresh" : _fresh.Status)}  ·  ui {_mask.Occluders}/{_mask.FreeRects}/{_mask.LastCopies}"
                    + (_map.Open ? $"  ·  map scan {_map.LastScanMs:0.0} ms" : "") + (_mask.Why != null ? "\nui mask: " + _mask.Why : "")
                    + (_why != null ? "\nwhy: " + _why : "");
            else _statusDetail = null;
            _statusIn = new StatusPanel.Input
            {
                Area = _areaName,
                Label = _resolved.Label.Length > 0 ? _resolved.Label : _targetSpec,
                GoalLabel = _statusGoalLabel,
                Manual = !route,
                NoRoute = route && _stepIndex < 0 && !questTarget && !travel,
                QuestTarget = questTarget,
                StepIndex = current,
                StepCount = shown,
                DoneMask = done,
                Note = _stepIndex >= 0 && _stepIndex < steps.Count ? steps[_stepIndex].Note : null,
                Why = _why,
                AreaWhy = _areaWhy,
                GridReady = grid != null,
                FieldReady = field != null && field.Grid == grid,
                PathReady = _frame.Ready,
                Blocked = _blockedField != null,
                FromMemory = _resolved.FromMemory,
                HasTravelTo = travel,
                ViaTravelMap = _resolved.ViaTravelMap,
                ViaPortal = _resolved.ViaPortal,
                PortalKey = _portalKey,
                DoorAhead = _doorAhead is { IsValid: true, IsTargetable: true },
                UseItem = _useItem,
                TextOnly = _textOnly,
                ShipReturn = _shipReturn,
                BossFight = _bossFight,
                TravelHub =_areaRaw.StartsWith("G2_", StringComparison.OrdinalIgnoreCase) ? "the Desert Map" : "the waypoint",
                TravelMapOpen = _map.Open,
                StopChosen = _chosenStop != null,
                PathLen = _pathLen,
                PathCells = _cells.Count,
                Detail = _statusDetail,
            };
            AgentStatus(ref _statusIn, now);   // an agent route on the card: its byline, step and note
            _status.Update(_statusIn, Settings.Debug.StatusWidth.Value, now);
        }
        var cardCore = Glow.Rgb(Settings.World.PathCore.Value);
        var cardGlow = Glow.Rgb(Settings.World.PathGlow.Value);
        _agentView.Blend(ref cardCore, ref cardGlow);
        _status.Draw(ImGui.GetBackgroundDrawList(), new Vector2(Settings.Debug.StatusX.Value, Settings.Debug.StatusY.Value), cardCore, cardGlow, now);
        DrawAgentControls(now);
    }

    /// <summary>A step already reached or flagged done, from what Tick recorded: no entity scan (StepDone's SeenEnough) on the render path.</summary>
    private bool StepReached(int i, RouteStep s) =>
        _reachedSteps.Contains(StepKey(s)) || (s.DoneFlag != null && _flags.TryGetValue(s.DoneFlag, out var v) && v);

    private string GetStateJson()
    {
        try { return JsonConvert.SerializeObject(BuildState()); }
        catch (Exception ex) { return JsonConvert.SerializeObject(new RouteStateDto { Area = _areaRaw, Why = $"state: {ex.Message} (read while Tick was replanning; retry)" }); }
    }

    private RouteStateDto BuildState()
    {
        var steps = _book.StepsFor(_areaRaw);
        var grid = _grid;
        var dto = new RouteStateDto
        {
            Area = _areaRaw,
            AreaName = _areaName,
            Mode = Settings.Route.Mode.Value,
            StepIndex = _stepIndex,
            Steps = steps.Select((s, i) => new RouteStepDto { Target = s.Target, Label = s.Label, DoneFlag = s.DoneFlag, Optional = s.Optional, Done = StepDone(i, s) }).ToList(),
            Target = _targetSpec,
            Label = _resolved.Label,
            Goals = _resolved.Goals.Count,
            Why = _why,
            PlayerGrid = _lastCell.x == int.MinValue ? null : [_lastCell.x, _lastCell.y],
            AreaSize = grid == null ? null : [grid.W, grid.H],
            PathCells = _cells.Count,
            PathWorldLength = MathF.Round(_pathLen),
            EdgePieces = _frame.EdgePieces.Count,
            Timings = new RouteTimingsDto
            {
                GridBuildMs = Math.Round(_gridMs, 1),
                FieldBuildMs = Math.Round(_field?.BuildMs ?? 0, 1),
                FieldCellsReached = _field?.Reached ?? 0,
                EdgeBuildMs = Math.Round(_edges?.BuildMs ?? 0, 1),
                EdgeContours = _edges?.Contours ?? 0,
                TickUs = Math.Round(_tickUs, 1),
                RenderUs = Math.Round(_renderUs, 1),
                LastReplanUs = Math.Round(_replanUs, 1),
                ResolveUs = Math.Round(_resolveUs, 1),
                Entities = GameController.EntityListWrapper.OnlyValidEntities.Count,
                Places = _resolver.Places.Count,
                SightingsScanUs = Math.Round(_scanUs, 1),
                Goals = _resolved.Goals.Count,
                PanelCheck = _rawPanelFlagsBroken ?? "raw",
                Motion = new RouteMotionDto
                {
                    Smooth = Settings.World.SmoothMotion.Value,
                    LagMs = Math.Round(Settings.World.SmoothMotion.Value ? _fresh.Motion.Lag * 1000 : _fresh.Fresh ? Settings.World.DelayMs.Value : 0, 1),
                    GameFrameMs = Math.Round(_fresh.Motion.Interval * 1000, 2),
                    HudFramesPerGameFrame = Math.Round(_fresh.Motion.FramesPerAdvance, 2),
                    JitterRawPx = Math.Round(_fresh.Motion.JitterRaw, 3),
                    JitterShownPx = Math.Round(_fresh.Motion.JitterShown, 3),
                    JitterPointsPx = Math.Round(_fresh.Motion.JitterPoints, 3),
                },
            },
        };
        foreach (var p in _frame.MapPath) { dto.MapPath.Add((int)p.X); dto.MapPath.Add((int)p.Y); }
        dto.ActiveQuests = _quests.Select(kv => $"{kv.Key}:{kv.Value}").ToList();
        dto.QuestObjective = _questObj == null ? null : $"{_questObj.Quest} -> {_questObj.Area} ({_questObj.Label})";
        foreach (var (id, st) in _quests)
        {
            var text = _questTexts.TryGetValue(id, out var tx) ? tx : "";
            var o = _book.Guide.Read(text);
            dto.QuestReads.Add(new QuestReadDto { Quest = id, State = st, Text = text, Kind = o.Kind.ToString(), Subject = o.Subject, AreaName = o.AreaName });
        }
        if (_auto is { } au) dto.QuestText = $"{au.Quest}: {au.Read.Kind}{(au.Read.Subject != null ? " " + au.Read.Subject : "")}{(au.Read.AreaName != null ? " in " + au.Read.AreaName : "")} ('{au.Read.Sentence}')";
        dto.RulesWhy = _book.Guide.Why;
        if (_capture is { } cap) dto.HeadsUp = $"{cap.Intent}{(cap.Note != null ? ": " + cap.Note : "")} ({_clock.Elapsed.TotalSeconds - cap.StartedAt:0} s)";
        dto.TravelTo = _resolved.TravelTo;
        dto.TravelMapOpen = _map.Open;
        if (_map.Open) dto.Notes.Add($"map view: {_map.Mode}{(_map.Port ? " (port)" : "")} ('{_map.Title}')");
        if (_map.Why != null) dto.Notes.Add(_map.Why);
        dto.Notes.Add("view: " + _fresh.Status);
        foreach (var s in _map.Stops)
            dto.TravelMapStops.Add(new TravelStopDto { Index = s.Index, DestId = s.DestId, DestName = s.DestName, Unlocked = s.Unlocked, Chosen = s == _chosenStop, X = (int)s.Center.X, Y = (int)s.Center.Y });
        if (_book.RouteError != null) dto.Notes.Add(_book.RouteError);
        if (_book.RadarError != null) dto.Notes.Add(_book.RadarError);
        if (_lastCell.x != int.MinValue) dto.PlayerTile = _resolver.TileAt(GameController, _areaRaw, _lastCell)?.Path;
        dto.Sightings = _resolver.Sightings.Area.Count;
        if (_resolved.FromMemory) dto.Notes.Add("goals are remembered sightings (their entities are not loaded right now)");
        if (_resolver.Sightings.Why != null) dto.Notes.Add(_resolver.Sightings.Why);
        if (_book.LiveError != null) dto.Notes.Add(_book.LiveError);
        if (_book.LiveAreas + _book.LiveQuests > 0) dto.Notes.Add($"live route {_book.LivePath}: {_book.LiveAreas} areas, {_book.LiveQuests} quest objectives over route.json");
        if (_feedback.Why != null) dto.Notes.Add(_feedback.Why);
        dto.AgentRoute = AgentState();
        return dto;
    }
}
