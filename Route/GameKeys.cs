using System;
using System.IO;
using System.Windows.Forms;

namespace WhatsARoute.Route;

/// <summary>
/// The player's own key bindings, read from the game's config (Documents\My Games\Path of Exile 2\
/// poe2_production_Config.ini, section [WASD_ACTION_KEYS] or [ACTION_KEYS]), so a hint can say "Shift+T" instead of
/// "your portal key". A binding is "&lt;virtual key&gt; [modifiers]": use_portal_scroll=84 1 is Shift+T (verified live
/// 2026-10-10). Modifier bits: 1 Shift (verified), 2 Ctrl, 4 Alt (assumed from the same field). Read once per area
/// change; the file is small. Null when the file or the binding is missing (the hint then names the action only).
/// </summary>
public static class GameKeys
{
    public const string ConfigName = "poe2_production_Config.ini";

    private static string? _config;
    private static bool _searched;

    /// <summary>
    /// The config path, found once per HUD run (the profile scan walks C:\Users, too much for every area change on
    /// the game thread). Searched again only when the file it found has gone.
    /// </summary>
    public static string? Config()
    {
        if (_searched && (_config == null || File.Exists(_config))) return _config;
        _config = FindConfig();
        _searched = true;
        return _config;
    }

    /// <summary>The config the game writes: the HUD user's Documents first, else the newest one under C:\Users.</summary>
    public static string? FindConfig()
    {
        try
        {
            var mine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile 2", ConfigName);
            string? best = File.Exists(mine) && new FileInfo(mine).Length > 0 ? mine : null;
            var bestTime = best != null ? File.GetLastWriteTimeUtc(best) : DateTime.MinValue;
            var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (users == null) return best;
            foreach (var dir in Directory.EnumerateDirectories(users))
            {
                var f = Path.Combine(dir, "Documents", "My Games", "Path of Exile 2", ConfigName);
                try
                {
                    if (!File.Exists(f) || new FileInfo(f).Length == 0) continue;   // the game's own user may have an empty one
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime) { best = f; bestTime = t; }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return best;
        }
        catch (Exception) { return null; }
    }

    /// <summary>"Shift+T" for an action such as use_portal_scroll, or null (unbound, no config, unreadable).</summary>
    public static string? Binding(string? configPath, string action)
    {
        if (configPath == null) return null;
        try
        {
            var inKeys = false;
            foreach (var raw in File.ReadLines(configPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inKeys = line is "[WASD_ACTION_KEYS]" or "[ACTION_KEYS]";
                    continue;
                }
                if (!inKeys || !line.StartsWith(action, StringComparison.Ordinal) || line.Length <= action.Length || line[action.Length] != '=') continue;
                return Format(line[(action.Length + 1)..]);
            }
        }
        catch (Exception) { }
        return null;
    }

    private static string? Format(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var vk) || vk <= 0) return null;
        var mods = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        var key = KeyName((Keys)vk);
        return (mods & 2) != 0 ? "Ctrl+" + Mod(mods & ~2, key) : Mod(mods, key);
    }

    private static string Mod(int mods, string key) =>
        ((mods & 1) != 0 ? "Shift+" : "") + ((mods & 4) != 0 ? "Alt+" : "") + key;

    private static string KeyName(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        >= Keys.A and <= Keys.Z => k.ToString(),
        Keys.Oemtilde => "`",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.OemPipe => "\\",
        Keys.Space => "Space",
        Keys.Menu => "Alt",
        Keys.ControlKey => "Ctrl",
        Keys.ShiftKey => "Shift",
        _ => k.ToString(),
    };
}
