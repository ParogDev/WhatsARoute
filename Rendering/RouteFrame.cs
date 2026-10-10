using System.Collections.Generic;
using System.Numerics;

namespace WhatsARoute.Rendering;

/// <summary>Everything Render needs, filled in Tick. World points are in world units (z = ground height).</summary>
public sealed class RouteFrame
{
    public bool Ready;
    public Vector3 Player;                 // world
    public float PlayerUnclampedHeight;    // Render.UnclampedHeight (large-map projection)
    public readonly List<Vector3> Path = new(512);        // near part, shaped, from the player's cell
    public float PathChangedAt;            // seconds (plugin clock) when the shape last changed by a lot
    public Vector3? Goal;                  // world, when the goal is within the near part
    public string Label = "";
    public readonly List<Vector2> MapPath = new(1024);    // whole route, grid units, every few cells
    public readonly List<float> MapPathZ = new(1024);
    public readonly List<int> EdgePieces = new(256);
    public string Status = "";
}
