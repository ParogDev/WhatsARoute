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
    /// <summary>Each open quest's current state read as an objective (objective-types.json): what the route would do with it.</summary>
    public List<QuestReadDto> QuestReads { get; set; } = new();
    /// <summary>The quest whose own text leads right now (route.json Follow, or a Quests entry with Target "quest"), and how.</summary>
    public string? QuestText { get; set; }
    /// <summary>Objective rules that failed to load (objective-types.json), else null.</summary>
    public string? RulesWhy { get; set; }
    /// <summary>A heads-up capture running now (F7): its intent and seconds so far.</summary>
    public string? HeadsUp { get; set; }
    public string? TravelTo { get; set; }
    public bool TravelMapOpen { get; set; }
    public List<TravelStopDto> TravelMapStops { get; set; } = new();
    /// <summary>The .tdt under the player (what a step's PlayerOn matches against).</summary>
    public string? PlayerTile { get; set; }
    /// <summary>Places recorded in this area instance (sightings.json has them per area).</summary>
    public int Sightings { get; set; }
}

public sealed class QuestReadDto
{
    public string Quest { get; set; } = "";
    public int State { get; set; }
    public string Text { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Subject { get; set; }
    public string? AreaName { get; set; }
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
    /// <summary>Load condition: the non-monster entities path matches walk (TargetResolver.Places).</summary>
    public int Places { get; set; }
    /// <summary>Gathering places and recording sightings (2 Hz, before resolving), smoothed.</summary>
    public double SightingsScanUs { get; set; }
    /// <summary>Load condition: goal cells the objective resolved to (a Radar tile place can be hundreds).</summary>
    public int Goals { get; set; }
    /// <summary>How the big-panel check reads the panel flag: "raw" (Flags bit, one read per panel), or why it fell back to IsVisibleLocal.</summary>
    public string? PanelCheck { get; set; }
    /// <summary>Smooth motion of the world drawings (Rendering/ViewMotion.cs): lag and jitter.</summary>
    public RouteMotionDto Motion { get; set; } = new();
}

public sealed class RouteMotionDto
{
    public bool Smooth { get; set; }
    /// <summary>How far behind the newest view the world drawings are drawn, ms: 1.5 game frames when smooth, else the draw delay.</summary>
    public double LagMs { get; set; }
    /// <summary>The measured game frame interval, ms.</summary>
    public double GameFrameMs { get; set; }
    /// <summary>HUD frames per game frame (above 1: the HUD runs faster than the game, the case that stutters).</summary>
    public double HudFramesPerGameFrame { get; set; }
    /// <summary>
    /// Average second difference of a static world probe's screen position per HUD frame while it moves, px: how unevenly
    /// it moves. Raw is the projection with the newest view; Shown is what is drawn (smooth or not, so turning smooth
    /// motion off gives the before numbers). Points is the per-point alternative (each projection buffered and
    /// interpolated, as the health bars do), measured on the same motion for comparison.
    /// </summary>
    public double JitterRawPx { get; set; }
    public double JitterShownPx { get; set; }
    public double JitterPointsPx { get; set; }
}
