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
    /// <summary>Agents reading this kind by name (the F8 dispatcher for feedback); watchers of every kind are not counted.</summary>
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

    public bool Final => Status is "done" or "failed";
}

/// <summary>
/// The route plugin's side of the AI Bridge's pub/sub (PluginBridge only, no compile-linking): presses and the trace go out
/// as events through AIBridge.Publish, replies come back through AIBridge.Subscribe. The bridge may load after this plugin,
/// reload, or be absent: the methods are looked up again every 2 s, a new bridge load (a different delegate) gets the
/// subscription again, and the subscription is renewed every 30 s anyway (the bridge drops a handler that threw). Presses
/// that could not be published wait (up to 20) and go out once the bridge is back. Replies arrive on the bridge's thread
/// and are queued for Tick. Each plugin load subscribes under its own name, so an old load's OnClose can't remove the new
/// load's subscription.
/// </summary>
public sealed class AiBridgeLink
{
    public const string PublishName = "AIBridge.Publish", SubscribeName = "AIBridge.Subscribe", ReplyKind = "feedback.reply";
    private const int UnsentMax = 20;

    private readonly string _subscriber = "WhatsARoute#" + Guid.NewGuid().ToString("N")[..8];
    private Func<string, string, string>? _publish;
    private Func<string, string, Action<string>, string>? _subscribe;
    private Delegate? _deadPublish;      // a bridge load that answered bridge_reloaded: PluginBridge may still hand it out
    private double _nextLookup, _nextSubscribe;
    private readonly ConcurrentQueue<string> _inbox = new();
    private readonly Queue<(string kind, string json)> _unsent = new();
    private static readonly JsonSerializerSettings Json = new() { NullValueHandling = NullValueHandling.Ignore };

    /// <summary>Why the bridge can't be used right now (null when it can).</summary>
    public string? Why { get; private set; } = "the AI Bridge plugin is not loaded yet";

    public bool Available => _publish != null;

    /// <summary>Presses waiting for the bridge.</summary>
    public int Unsent => _unsent.Count;

    /// <summary>Presses that waited for the bridge and went out on this Refresh (their sequence numbers).</summary>
    public List<long> Flushed { get; } = new();

    /// <summary>From Tick: find the bridge's methods (every 2 s), keep the reply subscription, send what waited.</summary>
    public void Refresh(PluginBridge bridge, double now)
    {
        Flushed.Clear();
        if (now < _nextLookup) return;
        _nextLookup = now + 2;
        Func<string, string, string>? publish; Func<string, string, Action<string>, string>? subscribe;
        try
        {
            publish = bridge.GetMethod<Func<string, string, string>>(PublishName);
            subscribe = bridge.GetMethod<Func<string, string, Action<string>, string>>(SubscribeName);
        }
        catch (Exception ex) { _publish = null; _subscribe = null; Why = $"PluginBridge lookup of {PublishName}: {ex.Message}"; return; }
        if (publish == null || subscribe == null)
        {
            _publish = null; _subscribe = null;
            Why = $"PluginBridge has no {(publish == null ? PublishName : SubscribeName)}: Whats An AI Bridge is not loaded, or a build before pub/sub";
            return;
        }
        if (ReferenceEquals(publish, _deadPublish)) { _publish = null; _subscribe = null; Why = "Whats An AI Bridge was unloaded (its methods answer bridge_reloaded)"; return; }
        var newLoad = !ReferenceEquals(subscribe, _subscribe);
        _publish = publish; _subscribe = subscribe; Why = null;
        if (newLoad || now >= _nextSubscribe)
        {
            _nextSubscribe = now + 30;
            try
            {
                var r = JObject.Parse(subscribe(_subscriber, ReplyKind, json => _inbox.Enqueue(json)));
                if (r["ok"]?.Value<bool>() != true) Why = $"{SubscribeName}: {r["message"] ?? r["error"]}";
            }
            catch (Exception ex) { Why = $"{SubscribeName}: {ex.Message}"; }
        }
        while (_unsent.Count > 0 && _publish != null)
        {
            var (kind, json) = _unsent.Peek();
            var r = Send(kind, json);
            if (r is not { Ok: true }) break;
            _unsent.Dequeue();
            Flushed.Add(r.Seq);
        }
    }

    /// <summary>
    /// Publishes <paramref name="data"/> as an event of <paramref name="kind"/>. Null (Why says why) when the bridge is absent
    /// or refused it; with <paramref name="keep"/> it is then kept and sent once the bridge is back.
    /// </summary>
    public PublishResult? Publish(string kind, object data, bool keep = false)
    {
        var json = JsonConvert.SerializeObject(data, Json);
        var r = _publish != null ? Send(kind, json) : null;
        if (r is { Ok: true }) return r;
        if (keep)
        {
            if (_unsent.Count >= UnsentMax) _unsent.Dequeue();
            _unsent.Enqueue((kind, json));
        }
        return r;
    }

    private PublishResult? Send(string kind, string json)
    {
        var publish = _publish;
        if (publish == null) return null;
        try
        {
            var r = JsonConvert.DeserializeObject<PublishResult>(publish(kind, json));
            if (r == null || !r.Ok) Why = $"{PublishName}({kind}): {r?.Message ?? r?.Error ?? "no answer"}";
            if (r?.Error == "bridge_reloaded") { _deadPublish = publish; _publish = null; _subscribe = null; _nextLookup = 0; }
            return r;
        }
        catch (Exception ex) { Why = $"{PublishName}({kind}): {ex.Message}"; return null; }
    }

    /// <summary>From OnClose: the bridge drops this load's handler (it would pin the unloaded plugin and keep calling it).</summary>
    public void Close()
    {
        try { _subscribe?.Invoke(_subscriber, ReplyKind, null!); } catch { }
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
