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
    private string _goalSig = "";
    private CancellationTokenSource _fieldCts = new();
    private double _nextResolve;
    private string _targetSpec = "";
    private int _stepIndex = -1;
    private int _stepShift;                     // hotkey nudges in this area
    private readonly HashSet<int> _reachedSteps = new();
    private readonly Dictionary<string, int> _quests = new();   // open quest id -> state id
    private double _nextQuests, _nextMap;
    private QuestObjective? _questObj;
    private readonly WorldMapReader _map = new();
    private WorldMapReader.Stop? _chosenStop;
    private readonly Dictionary<string, bool> _flags = new();
    private double _nextFlags;
    private string? _why;

    // per step
    private (int x, int y) _lastCell = (int.MinValue, 0);
    private FlowField? _lastFieldUsed;
    private EdgeMap? _lastEdgesUsed;
    private int _lastEdgeAhead, _lastEdgeCorridor;
    private double _replanUs, _tickUs, _renderUs;
    private float _pathLen;

    public override bool Initialise()
    {
        Name = "Whats A Route";
        _book.Load(DirectoryFullName);
        Settings.Route.ReloadRoute.OnPressed = () => { _book.Load(DirectoryFullName); _goalSig = ""; _nextResolve = 0; };
        GameController.PluginBridge.SaveMethod("WhatsARoute.GetState", (Func<string>)GetStateJson);
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _areaCts.Cancel();
        _areaCts = new CancellationTokenSource();
        _fieldCts.Cancel();
        _grid = null; _edges = null; _field = null; _goalSig = ""; _why = null; _areaWhy = null;
        _frame.Ready = false; _frame.Path.Clear(); _frame.MapPath.Clear(); _frame.MapPathZ.Clear(); _frame.EdgePieces.Clear();
        _lastCell = (int.MinValue, 0); _lastFieldUsed = null; _lastEdgesUsed = null;
        _stepShift = 0; _stepIndex = -1; _reachedSteps.Clear(); _nextResolve = 0; _nextFlags = 0;
        _resolver.ResetArea();
        _areaRaw = area?.Area?.RawName ?? "";
        _areaName = area?.Area?.Name ?? "";

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
        if (t >= _nextMap) { _nextMap = t + 0.25; ReadTravelMap(); }
        var grid = _grid;
        if (grid == null) { _why = _areaWhy ?? "building the area grid"; _frame.Ready = false; return; }
        var r = GameController.Player.GetComponent<Render>();
        if (r == null) { _why = "Player has no Render component"; return; }
        _frame.Player = r.Pos;
        _frame.PlayerUnclampedHeight = r.UnclampedHeight;
        var cell = ((int)(r.Pos.X / AreaGrid.GridToWorld), (int)(r.Pos.Y / AreaGrid.GridToWorld));
        var now = _clock.Elapsed.TotalSeconds;

        if (now >= _nextFlags) { _nextFlags = now + 2; ReadFlags(); }
        if (now >= _nextResolve) { _nextResolve = now + 0.5; ResolveObjective(grid, cell); }

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
        _replanUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;

        // A step without a quest flag counts as done once you stand on its goal.
        if (_stepIndex >= 0 && _cells.Count <= 4) _reachedSteps.Add(_stepIndex);
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
        foreach (var q in _book.Route.Quests)
        {
            if (q.Optional && !Settings.Route.IncludeOptional) continue;
            if (!_quests.TryGetValue(q.Quest, out var st)) continue;
            if (q.States is { Length: > 0 } && Array.IndexOf(q.States, st) < 0) continue;
            _questObj = q;
            break;
        }
    }

    private void ReadTravelMap()
    {
        try { _map.Read(GameController, _areaRaw); }
        catch (Exception ex) { _map.Why = $"travel map: {ex.Message}"; }
        _chosenStop = null;
        if (!_map.Open || _resolved.TravelTo is not { } dest) return;
        foreach (var s in _map.Stops)
            if (s.Unlocked && string.Equals(s.DestId, dest, StringComparison.OrdinalIgnoreCase)) { _chosenStop = s; break; }
    }

    private bool StepDone(int i, RouteStep s) =>
        _reachedSteps.Contains(i) || (s.DoneFlag != null && _flags.TryGetValue(s.DoneFlag, out var v) && v);

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
        else if (q?.Target is { Length: > 0 } qt && steps.Count == 0)
        {
            _stepIndex = -1;
            spec = qt;
        }
        else if (Settings.Route.Mode.Value == "Route" && steps.Count > 0)
        {
            var first = -1;
            for (var i = 0; i < steps.Count; i++)
                if (!StepDone(i, steps[i]) && (Settings.Route.IncludeOptional || !steps[i].Optional)) { first = i; break; }
            if (first < 0) first = steps.Count - 1;
            _stepIndex = Math.Clamp(first + _stepShift, 0, steps.Count - 1);
            spec = steps[_stepIndex].Target;
        }
        else
        {
            _stepIndex = -1;
            spec = Settings.Route.ManualTarget.Value ?? "";
        }
        _targetSpec = spec;
        _resolver.Resolve(GameController, _book, spec, _areaRaw, _resolved);
        if (_stepIndex >= 0 && steps[_stepIndex].Label is { Length: > 0 } label) _resolved.Label = label;
        else if (q?.Label is { Length: > 0 } ql && spec == q.Target) _resolved.Label = ql;
        if (_resolved.Goals.Count == 0)
        {
            _why = _resolved.Why;
            if (_resolved.FromEntities && _field != null && _goalSig.StartsWith(spec + "|")) return;   // keep the last known goal
            _field = null; _goalSig = ""; _frame.Ready = false;
            return;
        }
        var sig = spec + "|" + string.Join(";", _resolved.Goals.OrderBy(g => g.x).ThenBy(g => g.y).Select(g => $"{g.x},{g.y}"));
        if (sig == _goalSig) return;
        _goalSig = sig;
        _fieldCts.Cancel();
        _fieldCts = new CancellationTokenSource();
        var ct = _fieldCts.Token;
        var goals = _resolved.Goals.ToArray();
        Task.Run(() =>
        {
            var f = FlowField.Build(grid, goals, ct);
            if (f == null || ct.IsCancellationRequested) return;
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

    private void RenderBody()
    {
        if (Settings.Route.NextStep.PressedOnce()) { _stepShift++; _nextResolve = 0; }
        if (Settings.Route.PreviousStep.PressedOnce()) { _stepShift--; _nextResolve = 0; }
        if (!GameController.InGame) return;
        if (_map.Open && _chosenStop is { } stop) DrawChosenStop(stop);
        var ui = GameController.IngameState.IngameUi;
        if (ui.FullscreenPanels.Any(x => x.IsVisible) || ui.LargePanels.Any(x => x.IsVisible)) return;
        if (Settings.Debug.ShowStatus) DrawStatus();
        if (!_frame.Ready) return;
        var snap = GameController.IngameState.Camera.Snapshot;
        var largeMap = ui.Map.LargeMap.AsObject<SubMap>();
        var open = largeMap.IsVisible;
        _renderer.Draw(_frame, _edges, Settings, snap.Matrix, snap.HalfSize, open,
            open ? largeMap.MapCenter : default, open ? largeMap.MapScale : 0, _clock.Elapsed.TotalSeconds);
    }

    /// <summary>Placeholder look (design pass pending): rings around the stop to pick on the open travel map.</summary>
    private void DrawChosenStop(WorldMapReader.Stop stop)
    {
        var dl = ImGui.GetForegroundDrawList();
        var t = (float)_clock.Elapsed.TotalSeconds;
        var r = stop.Size * 0.5f + 4f + 3f * MathF.Sin(t * 3f);
        var gold = Glow.Col(new Vector3(1f, 0.85f, 0.45f), 0.95f);
        dl.AddCircle(stop.Center, r + 4f, Glow.Col(Glow.Ink, 0.5f), 48, 5f);
        dl.AddCircle(stop.Center, r, gold, 48, 2.5f);
        var label = _resolved.Label;
        var size = ImGui.CalcTextSize(label) + new Vector2(12f, 6f);
        var pos = new Vector2(stop.Center.X - size.X * 0.5f, stop.Center.Y + stop.Size * 0.5f + 10f);
        dl.AddRectFilled(pos, pos + size, Glow.Col(new Vector3(0.04f, 0.06f, 0.07f), 0.85f), size.Y * 0.5f);
        dl.AddText(pos + new Vector2(6f, 3f), gold, label);
    }

    private void DrawStatus()
    {
        var steps = _book.StepsFor(_areaRaw);
        var step = _stepIndex >= 0 ? $"step {_stepIndex + 1}/{steps.Count}: " : "";
        var line1 = $"Route  {_areaName} ({_areaRaw})  {step}{(_resolved.Label is { Length: > 0 } l ? l : _targetSpec)}";
        var line2 = _why != null
            ? $"  ! {_why}"
            : $"  {_pathLen:0} units  goals {_resolved.Goals.Count}  field {_field?.BuildMs:0}ms  replan {_replanUs:0}us  tick {_tickUs:0}us  render {_renderUs:0}us  edges {_frame.EdgePieces.Count}";
        var dl = ImGui.GetBackgroundDrawList();
        var p = new Vector2(Settings.Debug.StatusX.Value, Settings.Debug.StatusY.Value);
        var size = Vector2.Max(ImGui.CalcTextSize(line1), ImGui.CalcTextSize(line2));
        dl.AddRectFilled(p - new Vector2(6, 4), p + new Vector2(size.X + 6, size.Y * 2 + 6), 0xB0101418, 4f);
        dl.AddText(p, 0xFFD8F0FF, line1);
        dl.AddText(p + new Vector2(0, size.Y + 2), _why != null ? 0xFF7AB4FFu : 0xFFB0B8C0u, line2);
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
            },
        };
        foreach (var p in _frame.MapPath) { dto.MapPath.Add((int)p.X); dto.MapPath.Add((int)p.Y); }
        dto.ActiveQuests = _quests.Select(kv => $"{kv.Key}:{kv.Value}").ToList();
        dto.QuestObjective = _questObj == null ? null : $"{_questObj.Quest} -> {_questObj.Area} ({_questObj.Label})";
        dto.TravelTo = _resolved.TravelTo;
        dto.TravelMapOpen = _map.Open;
        if (_map.Why != null) dto.Notes.Add(_map.Why);
        foreach (var s in _map.Stops)
            dto.TravelMapStops.Add(new TravelStopDto { Index = s.Index, DestId = s.DestId, DestName = s.DestName, Unlocked = s.Unlocked, Chosen = s == _chosenStop, X = (int)s.Center.X, Y = (int)s.Center.Y });
        if (_book.RouteError != null) dto.Notes.Add(_book.RouteError);
        if (_book.RadarError != null) dto.Notes.Add(_book.RadarError);
        return JsonConvert.SerializeObject(dto);
    }
}
