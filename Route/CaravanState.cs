using System;
using System.Collections.Generic;
using System.IO;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared.Enums;
using Newtonsoft.Json;

namespace WhatsARoute.Route;

/// <summary>
/// The Act 2 caravan's trip, from server state (read 2026-10-11 in G2_town, 1 Hz quest-flag observer layer and 2 s
/// entity snapshots):
/// <list type="bullet">
/// <item>QuestFlags Act2CaravanLocation0..3 are a 4-bit index (bit 0 first) of the stop the caravan was last sent
/// to. A pick sets it at once: picking Mawdun Quarry moved it 10 -> 8 the moment the Desert Map closed.</item>
/// <item>QuestFlags CaravanStopping turns 1 with that pick (an already-reached stop) and back to 0 when you leave the
/// town; on entering the town it is 0 with every exit closed (the caravan waits for a pick), so 0 alone is not
/// "travelling".</item>
/// <item>Its exits: one AreaTransition per destination at each of its two gates, all untargetable until it stops;
/// then the two for that stop open (ramps ramp=1 first, ~5 s after the pick).</item>
/// </list>
/// Heading = the index changed during this visit and no caravan exit is open yet: the caravan was sent somewhere and
/// has not opened its gates. The index's area comes from the exits that open when it stops there (learnt, kept in
/// caravan-stops.json); 8 = G2_10_1 was seen. NOT verified for a stop the caravan has not reached yet (the long
/// trip): whether CaravanStopping stays 0 during it is open; the rule above does not depend on it.
/// </summary>
public sealed class CaravanState
{
    public const string Town = "G2_town";
    private static readonly QuestFlag[] Bits = [QuestFlag.Act2CaravanLocation0, QuestFlag.Act2CaravanLocation1, QuestFlag.Act2CaravanLocation2, QuestFlag.Act2CaravanLocation3];

    private readonly Dictionary<int, string> _stops = new() { [8] = "G2_10_1" };   // seen 2026-10-11
    private string _file = "";
    private int _entryLoc = -1;

    public int Location { get; private set; } = -1;
    public bool Stopping { get; private set; }
    public bool Moved { get; private set; }
    public string? OpenExit { get; private set; }
    /// <summary>The caravan was sent to a stop this visit and its gates are not open yet: the stop's area id, "" when not learnt yet.</summary>
    public string? Heading { get; private set; }
    public string? Why;
    public string Signal => $"Act2CaravanLocation={Location} (entry {_entryLoc}) CaravanStopping={(Stopping ? 1 : 0)} openExit={OpenExit ?? "none"}";

    public void Load(string dir)
    {
        _file = Path.Combine(dir, "caravan-stops.json");
        try
        {
            if (File.Exists(_file) && JsonConvert.DeserializeObject<Dictionary<int, string>>(File.ReadAllText(_file)) is { } d)
                foreach (var (k, v) in d) _stops[k] = v;
        }
        catch (Exception ex) { Why = $"caravan-stops.json: {ex.Message}"; }
    }

    public void ResetArea() { _entryLoc = -1; Location = -1; Moved = false; Heading = null; OpenExit = null; }

    /// <summary>In the caravan, at the resolve rate (2 Hz): quest flags (the HUD caches the dictionary) and the AreaTransition list.</summary>
    public void Update(GameController gc, string areaRaw)
    {
        Heading = null;
        if (!string.Equals(areaRaw, Town, StringComparison.OrdinalIgnoreCase)) return;
        Dictionary<QuestFlag, bool>? flags;
        try { flags = gc.IngameState.Data.ServerData?.QuestFlags; }
        catch (Exception ex) { Why = $"ServerData.QuestFlags: {ex.Message}"; return; }
        if (flags == null) { Why = "ServerData.QuestFlags is null"; return; }
        var loc = 0;
        for (var i = 0; i < Bits.Length; i++)
        {
            if (!flags.TryGetValue(Bits[i], out var b)) { Why = $"QuestFlags has no {Bits[i]}: the caravan's location flags moved"; return; }
            if (b) loc |= 1 << i;
        }
        Stopping = flags.TryGetValue(QuestFlag.CaravanStopping, out var s) && s;
        Why = null;
        if (_entryLoc < 0) _entryLoc = loc;
        if (loc != _entryLoc) Moved = true;
        Location = loc;

        OpenExit = null;
        if (gc.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.AreaTransition, out var list))
            foreach (var e in list)
                if (e.IsValid && e.IsTargetable && e.GetComponent<AreaTransition>()?.WorldArea?.Id is { } id) { OpenExit = id; break; }

        if (OpenExit != null)
        {
            // Stopped with its gates open: the index names this destination from now on.
            if (Moved && Stopping && (!_stops.TryGetValue(loc, out var known) || known != OpenExit)) { _stops[loc] = OpenExit; Save(); }
            return;
        }
        if (Moved) Heading = _stops.TryGetValue(loc, out var dest) ? dest : "";
    }

    private void Save()
    {
        if (_file.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonConvert.SerializeObject(_stops, Formatting.Indented));
        }
        catch (Exception ex) { Why = $"caravan-stops.json: {ex.Message}"; }
    }
}
