using System;
using System.Collections.Generic;
using System.Linq;

namespace WhatsARoute.Route;

/// <summary>
/// The F8 compose panel's state (research/f8-agent-ux.md 2): what the drawer binds to and what Send turns into the press.
/// The plugin opens it (suggestions computed once), the drawer toggles chips and edits the texts, the plugin sends. No
/// drawing here: WhatsARoute.Feedback.cs has a plain ImGui version; a styled drawer reads the same properties.
/// </summary>
public sealed class ComposeState
{
    public const double ClickGuardSec = 0.5, ReceiptHoldSec = 1.2, ReceiptFadeSec = 0.3, CollapseSec = 0.25;
    public const int NoteMax = 300, WatchForMax = 200;

    /// <summary>The flag taxonomy (2.3), in display order. "works" is the footer pill, not a chip.</summary>
    public static readonly Chip[] FlagChips =
    [
        new("wrong-place", "Wrong place"), new("not-advancing", "Not advancing"), new("missing-step", "Missing step"),
        new("cant-reach", "Can't reach it"), new("wrong-words", "Wrong words"), new("wrong-marker", "Wrong marker"),
        new("no-marker", "No marker"), new("too-early", "Too early"), new("already-done", "Already done"),
        new("odd-path", "Odd path"), new("something-else", "Something else"),
    ];

    public readonly record struct Chip(string Id, string Label);

    /// <summary>Which row a digit-addressable chip is in.</summary>
    public enum ChipKind { Suggestion, Flag, Scope, Hint }

    public bool Open { get; private set; }
    public double OpenedAt { get; private set; }

    /// <summary>The title row's context run: "THE GRELWOOD - PULL THE WATERWAYS LEVER" (or "- MANUAL TARGET").</summary>
    public string Context { get; private set; } = "";

    public List<SuggestionDto> Suggestions { get; private set; } = new();
    public readonly HashSet<string> Flags = new();

    /// <summary>The scope chips offered now (step, panel when one is open, area, marker when one exists, route when none).</summary>
    public List<Chip> ScopeChips { get; } = new();
    public string Scope { get; set; } = "step";
    public string? ScopePanel { get; private set; }

    public List<Chip> HintChips { get; } = new();
    public readonly HashSet<string> Hints = new();

    public string Note = "";
    public string WatchFor = "";

    /// <summary>The verify row (2.6): an earlier press of this HUD in LiveFix / Verifying, the most recent; null hides the row.</summary>
    public VerifyItem? Verify { get; set; }
    public sealed record VerifyItem(long Seq, string Stage, string? Text)
    {
        public string Lead => $"About #{Seq}'s fix:";
    }

    /// <summary>The drawer: a scope chip hovered (Marker pulses the marker, Open panel frames the panel), else null.</summary>
    public string? HoverScope { get; set; }

    // ── Receipt: the one-line local echo after Send (held 1.2 s, fades 0.3 s) ──

    public string? ReceiptText { get; private set; }
    /// <summary>The bridge did not take it: ToneWarn instead of the accent.</summary>
    public bool ReceiptWarn { get; private set; }
    public double ReceiptAt { get; private set; } = double.NegativeInfinity;

    public float ReceiptAlpha(double now)
    {
        if (ReceiptText == null) return 0f;
        var t = now - ReceiptAt;
        if (t < 0 || t > ReceiptHoldSec + ReceiptFadeSec) return 0f;
        return t <= ReceiptHoldSec ? 1f : (float)(1 - (t - ReceiptHoldSec) / ReceiptFadeSec);
    }

    /// <summary>The panel collapsing into the receipt: 1 = the panel, 0 = the receipt line (over 0.25 s after Send).</summary>
    public float Collapse(double now) => (float)Math.Clamp(1 - (now - ReceiptAt) / CollapseSec, 0, 1);

    /// <summary>Clicks inside the panel are ignored for 0.5 s after it opens (a double F8 cannot send).</summary>
    public bool Guarded(double now) => now - OpenedAt < ClickGuardSec;

    public void OpenWith(double now, string context, List<SuggestionDto> suggestions, string? panelName, bool markerExists)
    {
        Open = true;
        OpenedAt = now;
        Context = context;
        Suggestions = suggestions;
        Flags.Clear(); Hints.Clear();
        Note = ""; WatchFor = ""; HoverScope = null;
        ScopePanel = panelName;
        ScopeChips.Clear();
        ScopeChips.Add(new("step", "This step"));
        if (panelName != null) ScopeChips.Add(new("panel", AgentRoute.Cut("Open panel: " + panelName, 30)!));
        ScopeChips.Add(new("area", "This area"));
        ScopeChips.Add(markerExists ? new Chip("marker", "Marker") : new Chip("route", "Whole route"));
        Scope = panelName != null ? "panel" : "step";
        HintChips.Clear();
        if (panelName != null) HintChips.Add(new("panel-only", "Only the open panel"));
        HintChips.Add(new("path-ok", "Pathing is fine"));
        HintChips.Add(new("watch-next", "Watch what I do next"));
    }

    public void Close() { Open = false; HoverScope = null; }

    public bool FlagOn(string id) => Flags.Contains(id);
    public void ToggleFlag(string id) { if (!Flags.Remove(id)) Flags.Add(id); }
    public void ToggleSuggestion(int i) { if (i >= 0 && i < Suggestions.Count) Suggestions[i].Kept = !Suggestions[i].Kept; }
    public void SetScope(string id) => Scope = id;
    public bool HintOn(string id) => Hints.Contains(id);
    public void ToggleHint(string id) { if (!Hints.Remove(id)) Hints.Add(id); }

    /// <summary>
    /// The chips a digit toggles while no text field is active, in order: 1-9 then 0 (suggestions, then flags, then scope,
    /// then hints; the first ten).
    /// </summary>
    public List<(ChipKind kind, int index)> DigitChips()
    {
        var l = new List<(ChipKind, int)>(10);
        for (var i = 0; i < Suggestions.Count && l.Count < 10; i++) l.Add((ChipKind.Suggestion, i));
        for (var i = 0; i < FlagChips.Length && l.Count < 10; i++) l.Add((ChipKind.Flag, i));
        for (var i = 0; i < ScopeChips.Count && l.Count < 10; i++) l.Add((ChipKind.Scope, i));
        for (var i = 0; i < HintChips.Count && l.Count < 10; i++) l.Add((ChipKind.Hint, i));
        return l;
    }

    /// <summary>The digit label for the chip (1-9, 0), or null when it has none.</summary>
    public string? DigitOf(ChipKind kind, int index)
    {
        var l = DigitChips();
        var k = l.IndexOf((kind, index));
        return k < 0 ? null : ((k + 1) % 10).ToString();
    }

    /// <summary>The digit pressed (1-9, 0 = tenth): toggles that chip.</summary>
    public void PressDigit(int digit)
    {
        var l = DigitChips();
        var k = digit == 0 ? 9 : digit - 1;
        if (k < 0 || k >= l.Count) return;
        var (kind, i) = l[k];
        switch (kind)
        {
            case ChipKind.Suggestion: ToggleSuggestion(i); break;
            case ChipKind.Flag: ToggleFlag(FlagChips[i].Id); break;
            case ChipKind.Scope: SetScope(ScopeChips[i].Id); break;
            case ChipKind.Hint: ToggleHint(HintChips[i].Id); break;
        }
    }

    /// <summary>
    /// The press's F8 fields into <paramref name="dto"/>. <paramref name="works"/>: the Works here pill (adds "works").
    /// <paramref name="verdict"/> with <paramref name="refSeq"/>: the verify row. Flags are never empty: nothing picked
    /// sends ["something-else"]; "Works now" alone sends ["works"].
    /// </summary>
    public void Fill(FeedbackDto dto, bool works, string? verdict = null, long? refSeq = null)
    {
        var flags = FlagChips.Where(c => Flags.Contains(c.Id)).Select(c => c.Id).ToList();
        var worksNow = verdict == "works";
        if (works || worksNow) flags.Add("works");
        if (flags.Count == 0) flags.Add("something-else");
        dto.Flags = worksNow ? ["works"] : flags;
        dto.Suggested = Suggestions.Count > 0 ? Suggestions.ToList() : null;
        dto.Scope = Scope;
        dto.ScopePanel = Scope == "panel" ? ScopePanel : null;
        dto.Note = AgentRoute.Cut(Note.Trim(), NoteMax) is { Length: > 0 } n ? n : null;
        dto.WatchFor = AgentRoute.Cut(WatchFor.Trim(), WatchForMax) is { Length: > 0 } w ? w : null;
        var hints = HintChips.Where(c => Hints.Contains(c.Id)).Select(c => c.Id).ToList();
        dto.Hints = hints.Count > 0 ? hints : null;
        dto.WatchNext = Hints.Contains("watch-next") ? true : null;
        dto.Ref = refSeq;
        dto.Verdict = verdict;
    }

    /// <summary>"WRONG PLACE, NOT ADVANCING - THIS STEP" for the receipt.</summary>
    public string Summary(FeedbackDto dto)
    {
        var flags = dto.Flags?.Select(f => f == "works" ? "Works here" : FlagChips.FirstOrDefault(c => c.Id == f).Label ?? f) ?? [];
        var scope = ScopeChips.FirstOrDefault(c => c.Id == dto.Scope).Label ?? dto.Scope ?? "";
        var s = string.Join(", ", flags);
        if (dto.Verdict is { } v && dto.Ref is { } r) s = (v == "works" ? $"#{r} WORKS NOW" : $"#{r} STILL WRONG") + (dto.Flags is ["works"] ? "" : " - " + s);
        return (s + (scope.Length > 0 ? " - " + scope : "")).ToUpperInvariant();
    }

    public void Sent(double now, string text, bool warn)
    {
        ReceiptText = text; ReceiptWarn = warn; ReceiptAt = now;
        Close();
    }
}
