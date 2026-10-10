using System.Collections.Generic;
using System.Numerics;

namespace WhatsARoute.Rendering;

/// <summary>Everything Render needs, filled in Tick. World points are in world units (z = ground height).</summary>
public sealed class RouteFrame
{
    public bool Ready;
    public Vector3 Player;                 // world (Tick time; the renderer draws from the fresh position when it has one)
    public float PlayerUnclampedHeight;    // Render.UnclampedHeight (large-map projection)
    public readonly List<Vector3> Path = new(512);        // near part, shaped, from the player's cell
    public float PathChangedAt;            // seconds (plugin clock) when the shape last changed by a lot
    public Vector3? Goal;                  // world, when the goal is within the near part
    public string Label = "";
    public readonly List<Vector2> MapPath = new(1024);    // whole route, grid units, every few cells
    public readonly List<float> MapPathZ = new(1024);
    public readonly List<int> EdgePieces = new(256);
    public string Status = "";

    // The entity the route leads to, when the goal is something to click (a transition, the Desert Map table, a
    // waypoint): its position on the GROUND (x, y live from its Render component every frame; z = the path's ground
    // height at that cell, since Render.Pos.Z is the centre of the model's box) and Render.Bounds (half extents:
    // x, y on the ground, z = half the height; up is -z in world space). Unset when the goal is a tile.
    public bool HasGoalBox;
    public Vector3 GoalPos;
    public Vector3 GoalHalf;
    public long GoalAddress;               // the entity: the marker snaps in again when it changes
}
