using System;
using System.Runtime.InteropServices;

namespace WhatsARoute.Rendering;

/// <summary>
/// Is the game (or the HUD's own window, for its menu) the window in front? The HUD's IsForeGroundCache can't say:
/// with the core's ForceForeground on (kept on so the bridge and the HUD keep working while the user is in another
/// window) it is always true, and the topmost overlay then draws the route over whatever window is in front.
/// One GetForegroundWindow + GetWindowThreadProcessId per frame, ~1 us.
/// </summary>
internal static class GameInFront
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static readonly uint HudPid = (uint)Environment.ProcessId;

    public static bool Check(int gamePid)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;   // switching windows, or a secure desktop (UAC, lock screen)
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)gamePid || pid == HudPid;
    }
}
