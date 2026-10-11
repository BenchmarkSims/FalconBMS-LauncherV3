using SharpGen.Runtime;
using System;

namespace FalconBMS.Launcher.Input;

/// <summary>
/// Shared helpers for recovering a DirectInput session whose acquisition was lost
/// </summary>
internal static class DirectInputRecovery
{
    // The listener normally sleeps until DirectInput signals new data. A device
    // whose acquisition is silently dropped never signals again, so this loop 
    // wakes at this interval to run a buffered read. This read is what
    // surfaces DIERR_INPUTLOST / DIERR_NOTACQUIRED. It also covers any missed
    // event notification. A quiet device costs one peek every 2 seconds.
    public const int WatchdogIntervalMs = 2000;

    private const int DIERR_INPUTLOST = unchecked((int)0x8007001E);
    private const int DIERR_NOTACQUIRED = unchecked((int)0x8007000C);
    private const int DIERR_OTHERAPPHASPRIO = unchecked((int)0x80070005);

    public static int GetHResult(Exception ex)
    {
        return ex is SharpGenException sg ? sg.ResultCode.Code : ex.HResult;
    }

    public static bool IsAcquisitionLost(Exception ex)
    {
        int code = GetHResult(ex);

        return code == DIERR_INPUTLOST ||
               code == DIERR_NOTACQUIRED ||
               code == DIERR_OTHERAPPHASPRIO;
    }
}