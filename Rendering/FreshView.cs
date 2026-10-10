using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ExileCore2;
using ExileCore2.PoEMemory.Components;

namespace WhatsARoute.Rendering;

/// <summary>
/// The view at draw time: the camera matrix and the player's position read fresh from the game at the moment of
/// drawing, kept as a short history and sampled as of DelayMs ago (the game image's own latency), so world drawings
/// land on the image instead of leading it (research/render-fidelity.md; the AI Bridge render lab's method).
/// Calibration, once per camera / player Render address: the HUD's own values are located in a fresh copy of each
/// object's first 0x400 bytes; then every frame reads 64 + 12 bytes through the HUD's leaf memory backend (no page
/// cache, no allocation). When the backend can't be reached or a value isn't found, the HUD's cached snapshot is used
/// and Status says so.
/// </summary>
public sealed class FreshView
{
    private delegate bool RawReadFn(IntPtr address, Span<byte> target);
    private RawReadFn? _raw;
    private bool _rawTried;

    private long _camAddress, _playerRender;
    private int _camOffset = -1, _posOffset = -1;
    private readonly (long t, Matrix4x4 m, Vector3 pos)[] _hist = new (long, Matrix4x4, Vector3)[64];
    private int _histCount, _histHead;

    public string Status = "off";
    public bool Fresh;   // the last Sample came from fresh memory

    public void Reset() { _camOffset = -1; _posOffset = -1; _histCount = 0; _histHead = 0; }

    /// <summary>The matrix, half size and player position to draw with this frame.</summary>
    public void Sample(GameController gc, Render player, double delayMs, bool enabled, out Matrix4x4 m, out Vector2 half, out Vector3 pos)
    {
        var cam = gc.IngameState.Camera;
        var snap = cam.Snapshot;
        m = snap.Matrix; half = snap.HalfSize; pos = player.Pos;
        Fresh = false;
        if (!enabled) { Status = "off"; return; }
        var read = Raw(gc);
        if (read == null) { Status = "leaf memory backend not reachable: drawing from the HUD's cached view"; return; }
        if (_camOffset < 0 || _camAddress != cam.Address || _playerRender != player.Address)
        {
            _camAddress = cam.Address; _playerRender = player.Address; _histCount = 0; _histHead = 0;
            Span<byte> buf = stackalloc byte[0x400];
            _camOffset = read(new IntPtr(cam.Address), buf) ? FindMatrix(buf, m) : -1;
            _posOffset = read(new IntPtr(player.Address), buf) ? FindVec3(buf, pos, 0.5f) : -1;
            if (_camOffset < 0) { Status = "camera matrix not found in Camera's first 0x400 bytes: drawing from the HUD's cached view"; return; }
            if (_posOffset < 0) { Status = "player position not found in its Render's first 0x400 bytes: drawing from the HUD's cached view"; return; }
        }
        if (_camOffset < 0 || _posOffset < 0) return;
        Span<byte> mb = stackalloc byte[64];
        Span<byte> pb = stackalloc byte[12];
        if (!read(new IntPtr(_camAddress + _camOffset), mb) || !read(new IntPtr(_playerRender + _posOffset), pb))
        { Status = "fresh read failed this frame: HUD's cached view"; return; }
        var fm = MemoryMarshal.Read<Matrix4x4>(mb);
        var fp = new Vector3(BitConverter.ToSingle(pb[..4]), BitConverter.ToSingle(pb[4..8]), BitConverter.ToSingle(pb[8..12]));
        if (!float.IsFinite(fm.M11) || !float.IsFinite(fp.X)) { Status = "fresh read returned garbage: HUD's cached view"; return; }
        var now = Stopwatch.GetTimestamp();
        Push(now, fm, fp);
        var t = now - (long)(delayMs * Stopwatch.Frequency / 1000);
        StateAt(t, out m, out pos);
        Fresh = true;
        Status = "fresh";
    }

    private void Push(long t, in Matrix4x4 m, Vector3 p)
    {
        _hist[_histHead] = (t, m, p);
        _histHead = (_histHead + 1) % _hist.Length;
        if (_histCount < _hist.Length) _histCount++;
    }

    private (long t, Matrix4x4 m, Vector3 pos) At(int i) => _hist[(_histHead - _histCount + i + _hist.Length * 2) % _hist.Length];

    /// <summary>The state as of time t: interpolated between the two samples around it (oldest-first ring).</summary>
    private void StateAt(long t, out Matrix4x4 m, out Vector3 pos)
    {
        var first = At(0);
        if (t <= first.t) { m = first.m; pos = first.pos; return; }
        for (var k = _histCount - 1; k > 0; k--)
        {
            var a = At(k - 1);
            if (a.t > t) continue;
            var b = At(k);
            var f = b.t == a.t ? 1f : (float)(t - a.t) / (b.t - a.t);
            m = Matrix4x4.Lerp(a.m, b.m, f); pos = Vector3.Lerp(a.pos, b.pos, f);
            return;
        }
        var last = At(_histCount - 1);
        m = last.m; pos = last.pos;
    }

    private RawReadFn? Raw(GameController gc)
    {
        if (_rawTried) return _raw;
        _rawTried = true;
        try
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var mem = (object)gc.Memory;
            var paged = mem.GetType().GetField("_backend", inst)?.GetValue(mem);
            var leaf = paged?.GetType().GetFields(inst).FirstOrDefault(f => f.FieldType.Name == "IMemoryBackend")?.GetValue(paged) ?? paged;
            var m = leaf?.GetType().GetMethod("TryReadMemory", [typeof(IntPtr), typeof(Span<byte>)]);
            if (leaf != null && m != null) _raw = (RawReadFn)Delegate.CreateDelegate(typeof(RawReadFn), leaf, m);
        }
        catch { _raw = null; }
        return _raw;
    }

    private static int FindMatrix(ReadOnlySpan<byte> buf, in Matrix4x4 want)
    {
        Span<float> w = stackalloc float[16];
        w[0] = want.M11; w[1] = want.M12; w[2] = want.M13; w[3] = want.M14; w[4] = want.M21; w[5] = want.M22; w[6] = want.M23; w[7] = want.M24;
        w[8] = want.M31; w[9] = want.M32; w[10] = want.M33; w[11] = want.M34; w[12] = want.M41; w[13] = want.M42; w[14] = want.M43; w[15] = want.M44;
        for (var off = 0; off + 64 <= buf.Length; off += 4)
        {
            var ok = true;
            for (var i = 0; i < 16 && ok; i++) ok = MathF.Abs(BitConverter.ToSingle(buf.Slice(off + i * 4, 4)) - w[i]) <= 1e-3f;
            if (ok) return off;
        }
        return -1;
    }

    private static int FindVec3(ReadOnlySpan<byte> buf, Vector3 want, float tol)
    {
        for (var off = 0; off + 12 <= buf.Length; off += 4)
            if (MathF.Abs(BitConverter.ToSingle(buf.Slice(off, 4)) - want.X) <= tol
                && MathF.Abs(BitConverter.ToSingle(buf.Slice(off + 4, 4)) - want.Y) <= tol
                && MathF.Abs(BitConverter.ToSingle(buf.Slice(off + 8, 4)) - want.Z) <= tol) return off;
        return -1;
    }
}
