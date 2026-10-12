using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace WhatsARoute.Route;

/// <summary>What a quest state asks for. How each kind is handled: docs/objective-types.md.</summary>
public enum ObjectiveKind
{
    Unknown,
    /// <summary>Speak to / meet up with / show it to / take a reward from an NPC (or a totem): the NPC by name.</summary>
    Talk,
    /// <summary>Call an NPC to you (no place to walk to): the card says so.</summary>
    Summon,
    /// <summary>A quest item on the ground: the quest-item rule leads to it.</summary>
    PickUp,
    /// <summary>An item in the inventory (framed there), else a thing in the world of that name.</summary>
    Use,
    /// <summary>A thing in the world to click (a door, an anvil, a well): by name.</summary>
    Click,
    /// <summary>A monster to kill: by name, the boss place of the area until it loads.</summary>
    Defeat,
    /// <summary>An area to go into: its exit.</summary>
    Enter,
    /// <summary>Another area: the way there.</summary>
    Travel,
    /// <summary>The ship's sailing map: the card names the island the route picks.</summary>
    Sail,
    /// <summary>Explore / find / search: the area's own steps lead (or the area named).</summary>
    Explore,
    /// <summary>Witness / wait / survive: nothing to walk to.</summary>
    Wait,
}

public sealed class ObjectiveRule
{
    public ObjectiveKind Kind { get; set; }
    public string Pattern { get; set; } = "";
    [JsonIgnore] public Regex? Regex { get; set; }
}

public sealed class ObjectiveRulesFile
{
    public string? About { get; set; }
    public List<ObjectiveRule> Rules { get; set; } = new();
}

/// <summary>A quest state read as an objective: its kind, the thing it names, and the sentence that says so.</summary>
public sealed class QuestObjectiveText
{
    public ObjectiveKind Kind { get; init; }
    /// <summary>The NPC, item, monster, object or area the sentence names ("Makoru", "the Hooded One").</summary>
    public string? Subject { get; init; }
    /// <summary>An area the sentence names ("Return to Kingsmarch and speak to ..."), as written.</summary>
    public string? AreaName { get; init; }
    /// <summary>The sentence the rule matched: the card's label.</summary>
    public string Sentence { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>The capitalised names in the whole text: the candidates when the subject is a pronoun ("Return it to her").</summary>
    public List<string> Names { get; init; } = new();
}

/// <summary>
/// Reads quest-state texts as objectives with objective-types.json's rules. Stateless: one call per quest state change.
/// Fail at the broken link: a rules file that does not load or a pattern that does not compile is named in <see cref="Why"/>,
/// and the kinds that still load keep working.
/// </summary>
public sealed class QuestGuide
{
    private List<ObjectiveRule> _rules = new();
    private readonly Dictionary<string, QuestObjectiveText> _cache = new();
    public string? Why { get; private set; }
    public int RuleCount => _rules.Count;

    private static readonly Regex Sentences = new(@"(?<=[.!?])\s+(?=[A-Z])", RegexOptions.Compiled);

    public void Load(string pluginDir)
    {
        _cache.Clear();
        Why = null;
        var path = Path.Combine(pluginDir, "objective-types.json");
        try
        {
            var file = File.Exists(path) ? JsonConvert.DeserializeObject<ObjectiveRulesFile>(File.ReadAllText(path)) : null;
            if (file == null) { _rules = new(); Why = $"{path} not found: quest texts are not read as objectives"; return; }
            var bad = new List<string>();
            foreach (var r in file.Rules)
                try { r.Regex = new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled); }
                catch (Exception ex) { bad.Add($"{r.Kind} '{r.Pattern}': {ex.Message}"); }
            _rules = file.Rules.Where(r => r.Regex != null).ToList();
            if (bad.Count > 0) Why = "objective-types.json patterns that do not compile: " + string.Join("; ", bad);
        }
        catch (Exception ex) { _rules = new(); Why = $"objective-types.json: {ex.Message}"; }
    }

    /// <summary>The objective a quest state's text asks for (Unknown, with the whole text as the sentence, when no rule matches).</summary>
    public QuestObjectiveText Read(string? text)
    {
        text = (text ?? "").Trim();
        if (_cache.TryGetValue(text, out var known)) return known;
        var parts = Sentences.Split(text);
        QuestObjectiveText? found = null;
        // The order to act is usually the last sentence ("You have obtained a weapon piece. Speak to Makoru ...").
        for (var i = parts.Length - 1; i >= 0 && found == null; i--)
            foreach (var rule in _rules)
            {
                var m = rule.Regex!.Match(parts[i]);
                if (!m.Success) continue;
                var area = Clean(m.Groups["area"].Success ? m.Groups["area"].Value : null);
                // "Return to Kingsmarch and bring it to him": the area is in the text, not in the matched clause.
                if (area == null && ReturnTo.Match(text) is { Success: true } rm) area = Clean(rm.Groups["a"].Value);
                found = new QuestObjectiveText
                {
                    Kind = rule.Kind,
                    Subject = Clean(m.Groups["x"].Success ? m.Groups["x"].Value : null),
                    AreaName = area,
                    Sentence = parts[i].Trim(),
                    Text = text,
                    Names = NamesIn(text),
                };
                break;
            }
        found ??= new QuestObjectiveText { Kind = ObjectiveKind.Unknown, Sentence = text, Text = text, Names = NamesIn(text) };
        if (_cache.Count > 500) _cache.Clear();
        return _cache[text] = found;
    }

    private static readonly Regex ReturnTo = new(@"\bReturn to (?<a>[A-Z][\w']+(?: [A-Z][\w']+)*)", RegexOptions.Compiled);
    private static readonly Regex Name = new(@"\b[A-Z][a-z'\-]+(?:(?: of the| of| the)? [A-Z][a-z'\-]+)*", RegexOptions.Compiled);
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "You", "The", "This", "There", "Something", "As", "A", "An", "It", "Speak", "Talk", "Take", "Use", "Return", "Kill", "Slay",
        "Defeat", "Defeating", "Pick", "Explore", "Find", "Search", "Enter", "Travel", "Meet", "Await", "Witness", "Survive", "Complete",
        "Obtain", "Bring", "Show", "Turn", "Place", "Open", "Listen", "Follow", "Summon", "Investigate", "Locate", "Clear", "Ask",
        "Attack", "Confront", "Go", "Quest", "Reward", "Weapon", "Islands",
    };
    private static readonly HashSet<string> Pronouns = new(StringComparer.OrdinalIgnoreCase) { "it", "him", "her", "them", "one", "there" };

    /// <summary>Capitalised names in a quest text ("Kaimana", "Tattoo of Hinekora", "Captain Hartlin"), for a pronoun subject.</summary>
    private static List<string> NamesIn(string text) =>
        Name.Matches(text).Select(m => m.Value).Where(n => !NotNames.Contains(n)).Distinct().ToList();

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('.', ',', '!');
        if (Pronouns.Contains(s)) return null;   // "Slay it", "Speak to her": the names in the text stand in (Names)
        if (s.StartsWith("the ", StringComparison.OrdinalIgnoreCase) && s.Length > 4 && !char.IsUpper(s[4])) s = s[4..];
        return s.Length == 0 ? null : s;
    }

    /// <summary>A name as the game shows it ("The Hooded One") matches a sentence's subject ("the Hooded One", "Hooded One").</summary>
    public static bool NameMatches(string? name, string subject)
    {
        if (string.IsNullOrEmpty(name)) return false;
        static string Bare(string s) => s.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
        var n = Bare(name.Trim());
        var w = Bare(subject.Trim());
        return n.Equals(w, StringComparison.OrdinalIgnoreCase)
               || (w.Length >= 4 && n.StartsWith(w + ",", StringComparison.OrdinalIgnoreCase))   // "Krutog, the Kin Tyrant"
               || (n.Length >= 4 && w.StartsWith(n + ",", StringComparison.OrdinalIgnoreCase))
               || (n.Length >= 4 && w.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase) && w.Length - n.Length <= 24);   // "Yama the White" vs "Yama"
    }
}
