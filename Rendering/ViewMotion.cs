using System;
using System.Numerics;

namespace WhatsARoute.Rendering;

/// <summary>
/// Smooth motion for the world drawings (the health bar plugin's Motion.cs, applied to the view instead of each point).
/// The overlay is a separate window on its own clock: each HUD frame reads whatever game frame was last written, so when
/// the two rates differ, some HUD frames show the same game frame twice and others skip one. Every projection is right,
/// yet the ribbon, edges and markers stutter while the camera moves.
///
/// Playback buffer: each HUD frame checks whether the game advanced (the camera matrix or the player's position changed)
/// and timestamps it. The view (matrix and feet) at the last three game frames is kept, and the frame is drawn with the
/// view at a render time <see cref="DelayFrames"/> game frames behind now, between the two samples around it: evenly
/// spaced motion at a constant lag. Every world point is projected with that one view, so the whole drawing moves
/// together for one matrix lerp a frame, whatever the number of vertices (the path is static in the world; the camera
/// is the mover). A big jump (a teleport, the first frame after the drawing was hidden) snaps.
///
/// Jitter measurement: a static world probe (where the player stood, re-picked once they walk away) is projected with
/// the newest view (raw) and with the drawn one (shown); the second difference of its screen position per HUD frame,
/// averaged, says how unevenly it moves. JitterPoints is the per-point alternative (the probe's own projection buffered
/// and interpolated, as the health bars do), computed alongside so both approaches are measured on the same motion.
/// </summary>
public sealed class ViewMotion
{
    /// <summary>How many game frames behind the newest one the view is drawn.</summary>
    public const float DelayFrames = 1.5f;
    private const float SnapPx = 160f;
    private const float ProbeRepickWorld = 500f;

    private readonly double[] _t = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity }; // advances, oldest first
    private readonly Matrix4x4[] _m = new Matrix4x4[3];
    private readonly Vector3[] _p = new Vector3[3];
    private Matrix4x4 _lastM;
    private Vector3 _lastP;
    private bool _primed;
    private double _lastCall = double.NegativeInfinity;
    private float _interval = 1 / 60f;
    private int _framesSinceAdvance;

    // The probe: a static world point, its raw / shown / per-point projections over the last HUD frames.
    private Vector3 _probe;
    private int _probeFrames;
    private readonly Vector2[] _ps = new Vector2[3];   // per-point variant: the probe's projection at the last three advances
    private Vector2 _raw1, _raw2, _shown1, _shown2, _pts1, _pts2;

    /// <summary>The measured game frame interval (seconds).</summary>
    public float Interval => _interval;
    /// <summary>The constant lag of the smoothed view (seconds).</summary>
    public float Lag => _interval * DelayFrames;
    /// <summary>HUD frames per detected game advance (above 1: the HUD runs faster than the game, the case that stutters).</summary>
    public float FramesPerAdvance { get; private set; } = 1;
    /// <summary>Average second difference of the probe's screen position, px: newest view, drawn view, per-point interpolation.</summary>
    public float JitterRaw { get; private set; }
    public float JitterShown { get; private set; }
    public float JitterPoints { get; private set; }

    public void Reset()
    {
        _primed = false;
        _probeFrames = 0;
        for (var i = 0; i < 3; i++) _t[i] = double.NegativeInfinity;
    }

    /// <summary>
    /// Call once per drawn HUD frame. <paramref name="newestM"/> / <paramref name="newestP"/> are the latest view read
    /// from the game; <paramref name="m"/> / <paramref name="pos"/> come in as what would be drawn without smoothing and,
    /// when <paramref name="smooth"/>, go out as the view at the render time. The jitter is measured either way, so
    /// turning the setting off gives the before numbers.
    /// </summary>
    public void Apply(double now, in Matrix4x4 newestM, Vector3 newestP, Vector2 half, bool smooth, ref Matrix4x4 m, ref Vector3 pos)
    {
        var advanced = !newestM.Equals(_lastM) || newestP != _lastP;
        _lastM = newestM; _lastP = newestP;
        // Not drawn last frame (hidden, a menu, a load): start fresh instead of sliding from old samples.
        var stale = now - _lastCall > 0.1;
        _lastCall = now;
        // A jump: where the feet were drawn moves more than SnapPx between the newest view and the last sample.
        var jump = _primed && Vector2.DistanceSquared(Glow.Project(newestM, half, _p[2]), Glow.Project(_m[2], half, _p[2])) is var d && !(d <= SnapPx * SnapPx);
        _framesSinceAdvance++;
        if (!_primed || stale || jump)
        {
            for (var i = 0; i < 3; i++) { _m[i] = newestM; _p[i] = newestP; }
            _primed = true;
            _probeFrames = 0;
        }
        else if (advanced)
        {
            var gap = now - _t[2];
            // Game frames are 3-50 ms apart; longer gaps are pauses (nothing moved), not a frame rate.
            if (gap is > 0.002 and < 0.05) _interval += ((float)gap - _interval) * 0.1f;
            FramesPerAdvance += (_framesSinceAdvance - FramesPerAdvance) * 0.05f;
            _framesSinceAdvance = 0;
            _t[0] = _t[1]; _t[1] = _t[2]; _t[2] = now;
            _m[0] = _m[1]; _m[1] = _m[2]; _m[2] = newestM;
            _p[0] = _p[1]; _p[1] = _p[2]; _p[2] = newestP;
        }

        // The render time, and where it falls between the last three advances.
        var render = now - Lag;
        int seg; float u;
        if (render >= _t[1]) { seg = 1; u = Frac(render, _t[1], _t[2]); }
        else { seg = 0; u = Frac(render, _t[0], _t[1]); }
        if (smooth)
        {
            m = seg == 1 ? Matrix4x4.Lerp(_m[1], _m[2], u) : Matrix4x4.Lerp(_m[0], _m[1], u);
            pos = seg == 1 ? Vector3.Lerp(_p[1], _p[2], u) : Vector3.Lerp(_p[0], _p[1], u);
        }
        Measure(newestM, newestP, half, m, advanced, seg, u);
    }

    private void Measure(in Matrix4x4 newestM, Vector3 newestP, Vector2 half, in Matrix4x4 shownM, bool advanced, int seg, float u)
    {
        if (_probeFrames == 0 || Vector2.DistanceSquared(new Vector2(_probe.X, _probe.Y), new Vector2(newestP.X, newestP.Y)) > ProbeRepickWorld * ProbeRepickWorld)
        {
            _probe = newestP;
            _probeFrames = 0;
        }
        var raw = Glow.Project(newestM, half, _probe);
        var shown = Glow.Project(shownM, half, _probe);
        if (!float.IsFinite(raw.X) || !float.IsFinite(shown.X)) { _probeFrames = 0; return; }
        if (_probeFrames == 0) _ps[0] = _ps[1] = _ps[2] = raw;
        else if (advanced) { _ps[0] = _ps[1]; _ps[1] = _ps[2]; _ps[2] = raw; }
        var pts = seg == 1 ? Vector2.Lerp(_ps[1], _ps[2], u) : Vector2.Lerp(_ps[0], _ps[1], u);
        if (_probeFrames >= 2)
        {
            var dRaw = (raw - 2 * _raw1 + _raw2).Length();
            // Count moving frames only: at rest every jitter is 0 and would dilute the average.
            if ((raw - _raw1).LengthSquared() > 0.01f || dRaw > 0.1f)
            {
                var dShown = (shown - 2 * _shown1 + _shown2).Length();
                var dPts = (pts - 2 * _pts1 + _pts2).Length();
                if (float.IsFinite(dRaw) && float.IsFinite(dShown) && float.IsFinite(dPts))
                {
                    JitterRaw += (dRaw - JitterRaw) * 0.05f;
                    JitterShown += (dShown - JitterShown) * 0.05f;
                    JitterPoints += (dPts - JitterPoints) * 0.05f;
                }
            }
        }
        _raw2 = _raw1; _raw1 = raw;
        _shown2 = _shown1; _shown1 = shown;
        _pts2 = _pts1; _pts1 = pts;
        _probeFrames++;
    }

    /// <summary>Where t falls between a and b, 0-1; 1 (the newer sample) when either is unset (fewer than three advances seen).</summary>
    private static float Frac(double t, double a, double b) =>
        double.IsFinite(a) && double.IsFinite(b) && b > a ? (float)Math.Clamp((t - a) / (b - a), 0, 1) : 1;
}
