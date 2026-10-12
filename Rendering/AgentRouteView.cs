using System;
using System.Numerics;

namespace WhatsARoute.Rendering;

/// <summary>
/// The agent route as the route card and the world draw it (research/f8-agent-ux.md 4.1): plain values, rebuilt by the plugin
/// 4x per second (WhatsARoute.AgentRoute.cs, BuildAgentView). The drawer reads it; it never reaches into the engine. Times
/// are the plugin clock (seconds, the same clock Draw gets as <c>time</c>).
/// </summary>
public sealed class AgentRouteView
{
    /// <summary>The agent palette (the bridge's AttnHuePalette): violet, rose, sky, orange, sand, slate.</summary>
    public static readonly Vector3[] HuePalette =
    [
        Hex(0xA78BFA), Hex(0xF472B6), Hex(0x60A5FA), Hex(0xFB923C), Hex(0xD9B36C), Hex(0x94A3B8),
    ];

    /// <summary>The card's gold wash for a hand-back (path glow gold).</summary>
    public static readonly Vector3 Gold = Hex(0xFFA838);

    public static Vector3 HueRgb(int hue) => HuePalette[((hue % HuePalette.Length) + HuePalette.Length) % HuePalette.Length];

    /// <summary>The path and marker pair for an agent hue: glow = the hue, core = the hue 55% toward white.</summary>
    public static (Vector3 core, Vector3 glow) PathPair(Vector3 hue) => (Vector3.Lerp(hue, Vector3.One, 0.55f), hue);

    // ── The card while an agent route leads (or holds its last step) ──

    /// <summary>The card belongs to the agent route now: byline row, hue bar, agent step as the title.</summary>
    public bool OnCard;
    /// <summary>"CLAUDE 2'S ROUTE - F8 #12"; for 1.5 s after a hand-back "CLAUDE 2'S ROUTE - DONE" / "- ENDED" (then OnCard is false but Byline stays set until the flash ends).</summary>
    public string Byline = "";
    /// <summary>The owner's hue index (0-5) and its RGB.</summary>
    public int HueIndex;
    public Vector3 Hue;
    /// <summary>The step's Label (the card's title).</summary>
    public string Title = "";
    /// <summary>The step's Note (the meta row's first slot), or null.</summary>
    public string? Note;
    /// <summary>0-based current step and the count, for "1 of 3" and the dots (drawn in the hue).</summary>
    public int StepIndex, StepCount;
    public string StepText = "";
    /// <summary>The byline's hover: title WHY, body Reason, foot ("Ends by itself in 8 min, or when its 3 steps are done.").</summary>
    public string? Reason;
    public string HoverFoot = "";
    /// <summary>The current step waits on the player: the primary "Did it" pill.</summary>
    public bool DidItVisible;
    /// <summary>The secondary "Back to route" pill (parks the agent route).</summary>
    public bool BackToRouteVisible;
    public string BackToRouteTooltip = "";
    /// <summary>Hold: the last step stays as plain text ("Stay here: ..."); no path, no pills but Back to route.</summary>
    public bool Holding;
    public string? HoldText;

    // ── The parked route (Back to route was pressed): a pill at the meta row's right end, click = resume ──

    public bool ParkedVisible;
    /// <summary>"Claude 2's route (2 left)" (the drawer adds the hue dot).</summary>
    public string ParkedText = "";
    public Vector3 ParkedHue;

    // ── Flashes ──

    /// <summary>When the agent route took over the card (wash in the hue 0.9 s).</summary>
    public double TookOverAt = double.NegativeInfinity;
    /// <summary>When it handed back (gold wash 0.9 s, byline DONE / ENDED 1.5 s), and how: done | left | expired | ended | owner-gone.</summary>
    public double HandBackAt = double.NegativeInfinity;
    public string? HandBackEvent;

    // ── The world ──

    /// <summary>The path and marker colours while the agent route leads; the drawer blends toward them by <see cref="PathBlend"/>.</summary>
    public Vector3 PathCore, PathGlow;
    /// <summary>0 = the route's own colours, 1 = the agent pair; cross-fades over 0.6 s at take-over and hand-back.</summary>
    public float PathBlend;
    /// <summary>When the path's colour last switched (start of the 0.6 s cross-fade).</summary>
    public double PathSwitchedAt = double.NegativeInfinity;

    public void Clear()
    {
        OnCard = false; Byline = ""; Title = ""; Note = null; StepIndex = 0; StepCount = 0; StepText = ""; Reason = null; HoverFoot = "";
        DidItVisible = false; BackToRouteVisible = false; Holding = false; HoldText = null; ParkedVisible = false; ParkedText = "";
    }

    /// <summary>The route's own colours blended toward the agent pair by <see cref="PathBlend"/>.</summary>
    public void Blend(ref Vector3 core, ref Vector3 glow)
    {
        if (PathBlend <= 0f) return;
        core = Vector3.Lerp(core, PathCore, PathBlend);
        glow = Vector3.Lerp(glow, PathGlow, PathBlend);
    }

    private static Vector3 Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
}
