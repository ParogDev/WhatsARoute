using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;
using Color = System.Drawing.Color;

namespace WhatsARoute;

public sealed class WhatsARouteSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);
    public RouteSettings Route { get; set; } = new();
    public WorldSettings World { get; set; } = new();
    public MapSettings Map { get; set; } = new();
    public DebugSettings Debug { get; set; } = new();
}

[Submenu]
public sealed class RouteSettings
{
    [Menu("Mode", "Route: follow route.json for this area. Manual: always go to the target below.")]
    public ListNode Mode { get; set; } = new() { Values = new List<string> { "Route", "Manual" }, Value = "Route" };

    [Menu("Manual / fallback target", "Used in Manual mode, and in Route mode for areas without steps. " +
        "checkpoint | waypoint | transition[:name] | tile:<pattern> | radar:<Radar target name> | entity:<path part>")]
    public TextNode ManualTarget { get; set; } = new("transition");

    [Menu("Include optional steps", "Off: optional rewards (passives, gems, resistances) are skipped for speed")]
    public ToggleNode IncludeOptional { get; set; } = new(false);

    [Menu("Next step", "Skip to the next route step in this area (your key, none by default)")]
    public HotkeyNodeV2 NextStep { get; set; } = new(Keys.None);

    [Menu("Previous step")]
    public HotkeyNodeV2 PreviousStep { get; set; } = new(Keys.None);

    [JsonIgnore]
    [Menu("Reload route.json")]
    public ButtonNode ReloadRoute { get; set; } = new();
}

[Submenu]
public sealed class WorldSettings
{
    public ToggleNode ShowPath { get; set; } = new(true);

    [Menu("Path width")]
    public RangeNode<float> PathWidth { get; set; } = new(1f, 0.5f, 3f);

    [Menu("Draw distance (world units)", "The path fades out beyond this far along it")]
    public RangeNode<int> DrawDistance { get; set; } = new(2200, 400, 6000);

    [Menu("Moving pulses", "Small comets slide along the path toward the target")]
    public ToggleNode ShowComets { get; set; } = new(true);

    public ColorNode PathCore { get; set; } = new(Color.FromArgb(255, 255, 230, 158));
    public ColorNode PathGlow { get; set; } = new(Color.FromArgb(255, 255, 168, 56));

    [Menu("Show terrain edges", "Outline the walkable ground (ledges, walls, bridge rails) along the route")]
    public ToggleNode ShowEdges { get; set; } = new(true);

    [Menu("Edge corridor (cells)", "How far either side of the route edges are drawn")]
    public RangeNode<int> EdgeCorridor { get; set; } = new(16, 4, 40);

    [Menu("Edges ahead (cells)", "How far along the route edges are drawn")]
    public RangeNode<int> EdgeAhead { get; set; } = new(180, 40, 600);

    [Menu("Ignore small obstacles (edges)", "Contours shorter than this many cell edges (props, rocks) are skipped")]
    public RangeNode<int> MinContourEdges { get; set; } = new(10, 4, 80);

    public ColorNode EdgeCore { get; set; } = new(Color.FromArgb(255, 102, 224, 245));
    public ColorNode EdgeGlow { get; set; } = new(Color.FromArgb(255, 31, 158, 199));

    [Menu("Hide while the large map is open")]
    public ToggleNode HideWithLargeMap { get; set; } = new(false);
}

[Submenu]
public sealed class MapSettings
{
    [Menu("Show on large map")]
    public ToggleNode ShowOnLargeMap { get; set; } = new(true);

    public ColorNode Color { get; set; } = new(System.Drawing.Color.FromArgb(230, 255, 200, 90));

    [Menu("Line width")]
    public RangeNode<float> Width { get; set; } = new(2.5f, 1f, 6f);
}

[Submenu(CollapsedByDefault = true)]
public sealed class DebugSettings
{
    [Menu("Status line", "Objective, path length and timings at the top-left")]
    public ToggleNode ShowStatus { get; set; } = new(true);

    [Menu("Status X")]
    public RangeNode<int> StatusX { get; set; } = new(12, 0, 3840);

    [Menu("Status Y")]
    public RangeNode<int> StatusY { get; set; } = new(140, 0, 2160);
}
