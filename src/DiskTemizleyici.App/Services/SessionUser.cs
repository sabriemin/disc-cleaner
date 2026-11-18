using System.Runtime.InteropServices;

namespace DiskTemizleyici.App.Services;

/// <summary>
/// The account signed in to this Windows session. On a standard-user PC, "Run as administrator" can start the app
/// under a different (admin) account; then %TEMP%, %LOCALAPPDATA% and the protected folders belong to that account.
/// </summary>
public static class SessionUser
{
    public static string? SignedInUserName()
    {
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, WtsUserName, out var buffer, out _)) return null;
            try
            {
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private const int WtsCurrentSession = -1;
    private const int WtsUserName = 5;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
