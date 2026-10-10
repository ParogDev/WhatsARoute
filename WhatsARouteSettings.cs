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
    public TravelSettings Travel { get; set; } = new();
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

    [Menu("Keep off the game UI", "Never draw over the minimap, quest tracker, orbs, flasks, skill bar or buffs; hide while the Esc menu is open")]
    public ToggleNode AvoidGameUi { get; set; } = new(true);

    [Menu("Hide while the large map is open")]
    public ToggleNode HideWithLargeMap { get; set; } = new(false);

    [Menu("Fresh camera at draw time", "Read the camera and your position from the game when drawing, not the HUD's per-frame copy, " +
        "and draw them as of a few ms ago so the overlay lands on the game image")]
    public ToggleNode FreshView { get; set; } = new(true);

    [Menu("Draw delay (ms)", "How far behind the fresh state the overlay is drawn: the game image's own latency (5 measured at 200 fps)")]
    public RangeNode<int> DelayMs { get; set; } = new(5, 0, 30);

    [Menu("Mark the objective", "A gold marker around the thing to click (exit, Desert Map table, waypoint): footprint, brackets, name")]
    public ToggleNode ShowGoalMarker { get; set; } = new(true);
}

[Submenu]
public sealed class TravelSettings
{
    [Menu("Mark the stop to pick", "On the Desert Map: a gold marker around the caravan stop the route wants, with the destination's name")]
    public ToggleNode MarkChosenStop { get; set; } = new(true);

    [Menu("Ring the alternatives", "A quiet ring on other destinations an open quest also wants (e.g. Keth vs Valley of the Titans); nothing else")]
    public ToggleNode MarkOtherStops { get; set; } = new(true);
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
    [Menu("Objective card", "The current objective, its step in the area, the distance left and, when the route is not just routing, one line saying what it is doing")]
    public ToggleNode ShowStatus { get; set; } = new(true);

    [Menu("Card X")]
    public RangeNode<int> StatusX { get; set; } = new(12, 0, 3840);

    [Menu("Card Y")]
    public RangeNode<int> StatusY { get; set; } = new(140, 0, 2160);

    [Menu("Card max width (px)", "Long names are shortened with an ellipsis; the card is narrower when its text is")]
    public RangeNode<int> StatusWidth { get; set; } = new(330, 220, 480);

    [Menu("Detailed status", "Under the card: area id, target spec, path length, goals, timings (field/replan/tick/render/resolve), view state, UI mask counts and the raw reason behind a problem")]
    public ToggleNode DetailedStatus { get; set; } = new(false);

    [Menu("Outline the UI the route avoids", "Red boxes around each game UI element the world drawing keeps off")]
    public ToggleNode ShowUiMask { get; set; } = new(false);
}
