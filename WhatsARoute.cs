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
public sealed class WhatsARoute : BaseSettingsPlugin<WhatsARouteSettings>
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
    private readonly HashSet<int> _reachedSteps = new();
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
    private string DataDir
    {
        get
        {
            var dir = DirectoryFullName;
            var i = dir.IndexOf(@"\Plugins\Temp\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return dir;
            var src = dir[..i] + @"\Plugins\Source\" + dir[(i + @"\Plugins\Temp\".Length)..];
            return System.IO.Directory.Exists(src) ? src : dir;
        }
    }

    public override bool Initialise()
    {
        Name = "Whats A Route";
        _book.Load(DataDir);
        Settings.Route.ReloadRoute.OnPressed = () => { _book.Load(DataDir); _goalSig = ""; _nextResolve = 0; };
        GameController.PluginBridge.SaveMethod("WhatsARoute.GetState", (Func<string>)GetStateJson);
        return true;
    }

    public override void OnClose() => _resolver.Sightings.Flush(DataDir);

    public override void AreaChange(AreaInstance area)
    {
        _areaCts.Cancel();
        _areaCts = new CancellationTokenSource();
        _fieldCts.Cancel();
        _grid = null; _edges = null; _field = null; _blockedField = null; _goalSig = ""; _why = null; _areaWhy = null;
        _frame.Ready = false; _frame.Path.Clear(); _frame.MapPath.Clear(); _frame.MapPathZ.Clear(); _frame.EdgePieces.Clear();
        _lastCell = (int.MinValue, 0); _lastFieldUsed = null; _lastEdgesUsed = null;
        _stepShift = 0; _stepIndex = -1; _reachedSteps.Clear(); _nextResolve = 0; _nextFlags = 0;
        _goalEntity = null; _goalRender = null; _frame.HasGoalBox = false; _playerRender = null; _chosenStop = null; _stopShown = -1;
        _fresh.Reset();
        _resolver.ResetArea();
        _areaRaw = area?.Area?.RawName ?? "";
        _areaName = area?.Area?.Name ?? "";
        _resolver.Sightings.ResetArea(_areaRaw, area?.Hash ?? 0, DataDir);   // writes the old area's sightings first

        var data = GameController.IngameState.Data;
        int[][]? pf; float[][]? height;
        try { pf = data.RawPathfindingData; height = data.RawTerrainHeightData; }
        catch (Exception ex) { _areaWhy = $"reading terrain: {ex.Message}"; return; }
        var ct = _areaCts.Token;
        var minEdges = Settings.World.MinContourEdges.Value;
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
        if (!GameController.InGame || GameController.Player == null) { _frame.Ready = false; return; }
        var t = _clock.Elapsed.TotalSeconds;
        if (t >= _nextQuests) { _nextQuests = t + 1; ReadQuests(); }
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
        }

        var field = _field;
        if (field == null || field.Grid != grid) { _frame.Ready = false; return; }
        var edgeAhead = Settings.World.EdgeAhead.Value;
        var corridor = Settings.World.EdgeCorridor.Value;
        var edgesNow = _edges;
        if (cell == _lastCell && field == _lastFieldUsed && edgeAhead == _lastEdgeAhead && corridor == _lastEdgeCorridor && edgesNow == _lastEdgesUsed) return;
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
        _replanUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;

        // A step without a quest flag counts as done once you stand on its goal. SkipIfNone steps are stateless and
        // repeat (light the next seal, take its portal back): they are never marked reached.
        if (_stepIndex >= 0 && _cells.Count <= 4)
        {
            var steps = _book.StepsFor(_areaRaw);
            if (_stepIndex >= steps.Count || !steps[_stepIndex].SkipIfNone) _reachedSteps.Add(_stepIndex);
        }
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

    private void ReadQuests()
    {
        _quests.Clear();
        try
        {
            foreach (var (quest, state) in GameController.IngameState.IngameUi.UncompletedQuests)
                if (quest?.Id is { } id && state != null) _quests[id] = state.QuestStateId;
        }
        catch (Exception ex) { _why = $"IngameUi.UncompletedQuests: {ex.Message}"; }
        _questObj = null;
        _altAreas.Clear();
        foreach (var q in _book.Route.Quests)
        {
            if (q.Optional && !Settings.Route.IncludeOptional) continue;
            if (!_quests.TryGetValue(q.Quest, out var st)) continue;
            if (q.States is { Length: > 0 } && Array.IndexOf(q.States, st) < 0) continue;
            if (_questObj == null) _questObj = q;
            else _altAreas.Add(q.Area);   // other open objectives: the only other choices worth a ring on the map
        }
    }

    private readonly HashSet<string> _altAreas = new(StringComparer.OrdinalIgnoreCase);

    private void ReadTravelMap(double now)
    {
        try { _map.Read(GameController, _areaRaw, now); }
        catch (Exception ex) { _map.Why = $"travel map: {ex.Message}"; }
        _chosenStop = null;
        if (!_map.Open || _resolved.TravelTo is not { } dest) return;
        foreach (var s in _map.Stops)
            if (s.Unlocked && string.Equals(s.DestId, dest, StringComparison.OrdinalIgnoreCase)) { _chosenStop = s; break; }
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
        if (_reachedSteps.Contains(i) || (s.DoneFlag != null && _flags.TryGetValue(s.DoneFlag, out var v) && v)) return true;
        if (s.DoneWhenSeen is not { Entity.Length: > 0 } dw || !SeenEnough(dw)) return false;
        _reachedSteps.Add(i);
        return true;
    }

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

    private void ResolveObjective(AreaGrid grid, (int x, int y) cell)
    {
        string spec;
        var steps = _book.StepsFor(_areaRaw);
        var q = Settings.Route.Mode.Value == "Route" ? _questObj : null;
        if (q != null && !RouteBook.Like(_areaRaw, q.Area))
        {
            _stepIndex = -1;
            spec = "travel:" + q.Area;
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
        if (_blockedField != null)
        {
            // Still cut off from this objective: the nearest exit reachable from here (loaded or seen this visit).
            if (_blockedSpec != spec || _blockedField.Grid != grid || _blockedField.Snap(cell) != null) _blockedField = null;
            else
            {
                var target = _resolved.Label;
                _resolver.Resolve(GameController, _book, "transition:", _areaRaw, _resolved);
                _resolved.Label = $"Way out ({target} is not reachable from here)";
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
        Task.Run(() =>
        {
            var f = FlowField.Build(grid, goals, ct, out var fieldWhy);
            if (ct.IsCancellationRequested) return;
            if (f == null) { _why = $"cost map: {fieldWhy}"; return; }
            _field = f;
        }, ct);
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

    /// <summary>
    /// Any fullscreen or large panel open (passive tree, inventory, stash...): the world drawing hides. These are
    /// top-level panels whose parent is always shown, so their own flag (IsVisibleLocal) is the answer: IsVisible
    /// walks the parents and allocated ~3 KB per panel per frame (profiled 2026-10-10: 12.5 KB and 46 us per frame
    /// for the 19 panels). The panel lists are fetched again only when the UI object changes.
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
        }
        foreach (var p in _bigPanels)
            if (p.IsVisibleLocal) return true;
        return false;
    }

    private void RenderBody()
    {
        if (Settings.Route.NextStep.PressedOnce()) { _stepShift++; _nextResolve = 0; }
        if (Settings.Route.PreviousStep.PressedOnce()) { _stepShift--; _nextResolve = 0; }
        if (!GameController.InGame) return;
        var time = _clock.Elapsed.TotalSeconds;
        var core = Glow.Rgb(Settings.World.PathCore.Value);
        var glow = Glow.Rgb(Settings.World.PathGlow.Value);
        if (_map.Open && _chosenStop is { } stop) DrawTravelMap(stop, core, glow, time);
        else _stopShown = -1;
        if (BigPanelOpen()) return;
        var avoidUi = Settings.World.AvoidGameUi.Value;
        if (avoidUi)
        {
            _mask.Refresh(GameController, time, ImGui.GetIO().DisplaySize);
            if (_mask.Paused) return;
            if (Settings.Debug.ShowUiMask) _mask.DrawOutlines();
        }
        if (Settings.Debug.ShowStatus) DrawStatus();
        var pr = _playerRender;
        if (pr == null) return;

        // The view at draw time: fresh camera and feet, as of DelayMs ago (FreshView), else the HUD's per-frame copy.
        _fresh.Sample(GameController, pr, Settings.World.DelayMs.Value, Settings.World.FreshView.Value, out var m, out var half, out var player);
        var disp = ImGui.GetIO().DisplaySize;
        var largeMap = GameController.IngameState.IngameUi.Map.LargeMap.AsObject<SubMap>();
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
        if (_frame.Ready)
            _renderer.Draw(_frame, _edges, Settings, m, half, player, goal, near, open, open ? largeMap.MapCenter : default, open ? largeMap.MapScale : 0, time);
        if (showMarker)
        {
            var dl = ImGui.GetBackgroundDrawList();
            if ((dl.Flags & ImDrawListFlags.AntiAliasedLines) == 0) dl.Flags |= ImDrawListFlags.AntiAliasedLines;
            _markers.DrawEntity(dl, m, half, disp, _frame.GoalPos, modelless ? 40f : MathF.Max(_frame.GoalHalf.X, _frame.GoalHalf.Y) * 0.8f,
                modelless ? 0f : _frame.GoalHalf.Z * 2f, _frame.Label, core, glow, time, _goalAppearedAt, near);
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

    private string _statusLine1 = "", _statusLine2 = "";
    private double _nextStatus;

    /// <summary>The debug status line: its strings are rebuilt 4x per second, not per frame (no per-frame garbage).</summary>
    private void DrawStatus()
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now >= _nextStatus)
        {
            _nextStatus = now + 0.25;
            var steps = _book.StepsFor(_areaRaw);
            var step = _stepIndex >= 0 ? $"step {_stepIndex + 1}/{steps.Count}: " : "";
            _statusLine1 = $"Route  {_areaName} ({_areaRaw})  {step}{(_resolved.Label is { Length: > 0 } l ? l : _targetSpec)}";
            _statusLine2 = _why != null
                ? $"  ! {_why}"
                : $"  {_pathLen:0} units  goals {_resolved.Goals.Count}  field {_field?.BuildMs:0}ms  replan {_replanUs:0}us  tick {_tickUs:0}us  render {_renderUs:0}us  edges {_frame.EdgePieces.Count}  view {(_fresh.Fresh ? "fresh" : _fresh.Status)}{(_map.Open ? $"  map scan {_map.LastScanMs:0.0}ms" : "")}  ui {_mask.Occluders}/{_mask.FreeRects}/{_mask.LastCopies}{(_mask.Why != null ? "  ! " + _mask.Why : "")}";
        }
        var dl = ImGui.GetBackgroundDrawList();
        var p = new Vector2(Settings.Debug.StatusX.Value, Settings.Debug.StatusY.Value);
        var size = Vector2.Max(ImGui.CalcTextSize(_statusLine1), ImGui.CalcTextSize(_statusLine2));
        dl.AddRectFilled(p - new Vector2(6, 4), p + new Vector2(size.X + 6, size.Y * 2 + 6), 0xB0101418, 4f);
        dl.AddText(p, 0xFFD8F0FF, _statusLine1);
        dl.AddText(p + new Vector2(0, size.Y + 2), _why != null ? 0xFF7AB4FFu : 0xFFB0B8C0u, _statusLine2);
    }

    private string GetStateJson()
    {
        try { return BuildStateJson(); }
        catch (Exception ex) { return JsonConvert.SerializeObject(new RouteStateDto { Area = _areaRaw, Why = $"state: {ex.Message} (read while Tick was replanning; retry)" }); }
    }

    private string BuildStateJson()
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
            },
        };
        foreach (var p in _frame.MapPath) { dto.MapPath.Add((int)p.X); dto.MapPath.Add((int)p.Y); }
        dto.ActiveQuests = _quests.Select(kv => $"{kv.Key}:{kv.Value}").ToList();
        dto.QuestObjective = _questObj == null ? null : $"{_questObj.Quest} -> {_questObj.Area} ({_questObj.Label})";
        dto.TravelTo = _resolved.TravelTo;
        dto.TravelMapOpen = _map.Open;
        if (_map.Open) dto.Notes.Add($"map view: {_map.Mode} ('{_map.Title}')");
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
        return JsonConvert.SerializeObject(dto);
    }
}
