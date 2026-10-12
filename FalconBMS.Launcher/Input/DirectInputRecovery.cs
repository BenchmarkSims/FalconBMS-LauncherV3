using SharpGen.Runtime;
using System;

namespace FalconBMS.Launcher.Input;

/// <summary>
/// Shared helpers for recovering a DirectInput session whose acquisition was lost
/// </summary>
internal static class DirectInputRecovery
{

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