using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace WhatsARoute.Route;

/// <summary>
/// Waypoints each character has unlocked, learnt from the waypoint map's Teleport view (a lit orb is an unlocked
/// waypoint: <see cref="WorldMapReader.Stop.Unlocked"/>). ServerData.WaypointsUnlockState has the bits, but no
/// WorldAreas column maps an area to its bit on this build (checked 2026-10-11: the int at row +38 repeats across
/// areas), so the map is the source. Kept per character in waypoints.json in the live folder, written when it grows.
/// </summary>
public sealed class WaypointMemory
{
    private Dictionary<string, HashSet<string>> _all = new(StringComparer.Ordinal);
    private HashSet<string> _mine = new(StringComparer.OrdinalIgnoreCase);
    private string _character = "";
    private string _file = "";
    public string? Why;

    public int Count => _mine.Count;
    public bool HasCharacter => _character.Length > 0;

    /// <summary>Loads the file once, and picks the character's set when the character changes.</summary>
    public void Use(string dir, string character)
    {
        if (_file.Length == 0)
        {
            _file = Path.Combine(dir, "waypoints.json");
            try
            {
                if (File.Exists(_file) && JsonConvert.DeserializeObject<Dictionary<string, HashSet<string>>>(File.ReadAllText(_file)) is { } d)
                    foreach (var (k, v) in d) _all[k] = new HashSet<string>(v, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Why = $"waypoints.json: {ex.Message}"; }
        }
        if (character == _character) return;
        _character = character;
        if (!_all.TryGetValue(character, out var set)) _all[character] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _mine = set;
    }

    public bool Unlocked(string areaId) => _mine.Contains(areaId);

    /// <summary>The unlocked waypoints of an open Teleport view; written to disk when one is new.</summary>
    public void Learn(List<WorldMapReader.Stop> stops)
    {
        if (_character.Length == 0) return;
        var added = false;
        foreach (var s in stops)
            if (s.Unlocked && s.DestId.Length > 0 && _mine.Add(s.DestId)) added = true;
        if (!added) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonConvert.SerializeObject(_all, Formatting.Indented));
        }
        catch (Exception ex) { Why = $"waypoints.json: {ex.Message}"; }
    }
}
