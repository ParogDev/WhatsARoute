using System.Collections.Generic;

namespace WhatsARoute;

/// <summary>What the plugin is doing right now, for the control center and agents (PluginBridge "WhatsARoute.GetState").</summary>
public sealed class RouteStateDto
{
    public string Game { get; set; } = "poe2";
    public string? Area { get; set; }
    public string? AreaName { get; set; }
    public string Mode { get; set; } = "";
    public int StepIndex { get; set; } = -1;
    public List<RouteStepDto> Steps { get; set; } = new();
    public string? Target { get; set; }
    public string? Label { get; set; }
    public int Goals { get; set; }
    /// <summary>The first broken link when there is no path (target unresolved, terrain missing, unreachable).</summary>
    public string? Why { get; set; }
    public int[]? PlayerGrid { get; set; }
    public int[]? AreaSize { get; set; }
    public int PathCells { get; set; }
    public float PathWorldLength { get; set; }
    public int EdgePieces { get; set; }
    public RouteTimingsDto Timings { get; set; } = new();
    /// <summary>The whole route in grid cells, every few cells (x0, y0, x1, y1, ...).</summary>
    public List<int> MapPath { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    /// <summary>Open quests as "QuestId:StateId" (IngameUi.UncompletedQuests).</summary>
    public List<string> ActiveQuests { get; set; } = new();
    public string? QuestObjective { get; set; }
    public string? TravelTo { get; set; }
    public bool TravelMapOpen { get; set; }
    public List<TravelStopDto> TravelMapStops { get; set; } = new();
}

public sealed class TravelStopDto
{
    public int Index { get; set; }
    public string DestId { get; set; } = "";
    public string DestName { get; set; } = "";
    public bool Unlocked { get; set; }
    public bool Chosen { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class RouteStepDto
{
    public string Target { get; set; } = "";
    public string? Label { get; set; }
    public string? DoneFlag { get; set; }
    public bool Done { get; set; }
    public bool Optional { get; set; }
}

public sealed class RouteTimingsDto
{
    public double GridBuildMs { get; set; }
    public double FieldBuildMs { get; set; }
    public int FieldCellsReached { get; set; }
    public double EdgeBuildMs { get; set; }
    public int EdgeContours { get; set; }
    public double TickUs { get; set; }
    public double RenderUs { get; set; }
    public double LastReplanUs { get; set; }
    /// <summary>Objective resolution (2 Hz), smoothed.</summary>
    public double ResolveUs { get; set; }
    /// <summary>Load condition the timings were taken under: valid entities right now.</summary>
    public int Entities { get; set; }
}
