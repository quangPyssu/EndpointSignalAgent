// src/Shared/Utilities/WindowsActions.cs
using System.Runtime.InteropServices;

namespace EndpointSignalAgent.Shared.Utilities;

public static class WindowsActions
{
    public static void LockWorkstation() => NativeMethods.LockWorkStation();

    public static void ForceSleep() => NativeMethods.SetSuspendState(false, false, false);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool LockWorkStation();

        [DllImport("PowrProf.dll")]
        internal static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }
}
