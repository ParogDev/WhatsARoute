using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ExileCore2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsARoute.Route;

/// <summary>What AIBridge.Publish answered: the event's sequence and who listens to its kind right now.</summary>
public sealed class PublishResult
{
    public bool Ok { get; set; }
    public long Seq { get; set; }
    public List<JObject> Subscribers { get; set; } = new();
    public string? Error { get; set; }
    public string? Message { get; set; }
}

/// <summary>A reply to an F8 press, published by the MCP's dispatcher or the handler session (kind feedback.reply).</summary>
public sealed class FeedbackReplyDto
{
    /// <summary>The bridge sequence of the press it answers.</summary>
    public long Ref { get; set; }
    /// <summary>queued | claimed | working | done | failed</summary>
    public string Status { get; set; } = "";
    public string? Text { get; set; }
}

/// <summary>
/// The route plugin's side of the AI Bridge's pub/sub (PluginBridge only, no compile-linking): presses and the trace go out
/// as events through AIBridge.Publish, replies come back through AIBridge.Subscribe. The bridge may load after this plugin,
/// reload, or be absent: the methods are looked up again every 2 s, and a new bridge load (a different delegate) gets the
/// subscription again. Replies arrive on the bridge's thread and are queued for Tick.
/// </summary>
public sealed class AiBridgeLink
{
    public const string PublishName = "AIBridge.Publish", SubscribeName = "AIBridge.Subscribe", Subscriber = "WhatsARoute", ReplyKind = "feedback.reply";

    private Func<string, string, string>? _publish;
    private Func<string, string, Action<string>, string>? _subscribe;
    private Delegate? _subscribedTo;
    private double _nextLookup;
    private readonly ConcurrentQueue<string> _inbox = new();
    private static readonly JsonSerializerSettings Json = new() { NullValueHandling = NullValueHandling.Ignore };

    /// <summary>Why the bridge can't be used right now (null when it can).</summary>
    public string? Why { get; private set; } = "the AI Bridge plugin is not loaded yet";

    public bool Available => _publish != null;

    /// <summary>From Tick: find the bridge's methods (every 2 s) and subscribe to replies once per bridge load.</summary>
    public void Refresh(PluginBridge bridge, double now)
    {
        if (now < _nextLookup) return;
        _nextLookup = now + 2;
        try
        {
            _publish = bridge.GetMethod<Func<string, string, string>>(PublishName);
            _subscribe = bridge.GetMethod<Func<string, string, Action<string>, string>>(SubscribeName);
        }
        catch (Exception ex) { _publish = null; _subscribe = null; Why = $"PluginBridge lookup of {PublishName}: {ex.Message}"; return; }
        if (_publish == null || _subscribe == null) { Why = $"PluginBridge has no {(_publish == null ? PublishName : SubscribeName)}: Whats An AI Bridge is not loaded, or a build before pub/sub"; return; }
        Why = null;
        if (ReferenceEquals(_subscribedTo, _subscribe)) return;
        try
        {
            var r = JObject.Parse(_subscribe(Subscriber, ReplyKind, json => _inbox.Enqueue(json)));
            if (r["ok"]?.Value<bool>() != true) { Why = $"{SubscribeName}: {r["message"] ?? r["error"]}"; return; }
            _subscribedTo = _subscribe;
        }
        catch (Exception ex) { Why = $"{SubscribeName}: {ex.Message}"; }
    }

    /// <summary>Publishes <paramref name="data"/> as an event of <paramref name="kind"/>; null when the bridge is absent (Why says why).</summary>
    public PublishResult? Publish(string kind, object data)
    {
        var publish = _publish;
        if (publish == null) return null;
        try
        {
            var r = JsonConvert.DeserializeObject<PublishResult>(publish(kind, JsonConvert.SerializeObject(data, Json)));
            if (r == null || !r.Ok) Why = $"{PublishName}({kind}): {r?.Message ?? r?.Error ?? "no answer"}";
            if (r?.Error == "bridge_reloaded") { _publish = null; _nextLookup = 0; }   // the bridge reloaded: find its new methods
            return r;
        }
        catch (Exception ex) { Why = $"{PublishName}({kind}): {ex.Message}"; return null; }
    }

    /// <summary>From OnClose: the bridge drops this plugin's handler (it would pin the unloaded plugin and keep calling it).</summary>
    public void Close()
    {
        try { _subscribe?.Invoke(Subscriber, ReplyKind, null!); } catch { }
        _subscribedTo = null;
    }

    /// <summary>Replies received since the last call (Tick, main thread).</summary>
    public IEnumerable<FeedbackReplyDto> TakeReplies()
    {
        while (_inbox.TryDequeue(out var json))
        {
            FeedbackReplyDto? reply = null;
            try { reply = (JObject.Parse(json)["data"] as JObject)?.ToObject<FeedbackReplyDto>(); } catch { }
            if (reply != null) yield return reply;
        }
    }
}
