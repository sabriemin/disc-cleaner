using System.Runtime.InteropServices;

namespace DiskTemizleyici.Core.Cleanup;

/// <summary>Windows Recycle Bin: query its size, empty it, or send files to it.</summary>
public static class RecycleBin
{
    // shellapi.h uses natural packing on 64-bit Windows, which is what LayoutKind.Sequential gives us.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Total size and item count in the Recycle Bin of all drives.</summary>
    public static (long Size, long Count) Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        var hr = SHQueryRecycleBin(null, ref info);
        return hr == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin of all drives. Returns true on success (or when it was already empty).</summary>
    public static bool Empty()
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        // 0x8000FFFF (E_UNEXPECTED) is returned when the bin is already empty.
        return hr == 0 || unchecked((uint)hr) == 0x8000FFFF;
    }

    /// <summary>
    /// Sends files or folders to the Recycle Bin. If something is too big for the Recycle Bin, Windows asks
    /// the user before deleting it permanently (it is never removed permanently without a warning).
    /// </summary>
    /// <returns>True if the shell reported success and the user did not cancel.</returns>
    public static bool Send(IReadOnlyList<string> paths, IntPtr ownerWindow = default)
    {
        if (paths.Count == 0) return true;
        var from = string.Join('\0', paths) + "\0\0";
        var op = new SHFILEOPSTRUCT
        {
            hwnd = ownerWindow,
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        var result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }
}
