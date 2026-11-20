using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OpenIsland.Core.Platform;

public static class Win32Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    // Windows 11 DWM attributes
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    // Hotkey modifiers
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const int WM_HOTKEY = 0x0312;

    // Toolhelp32 snapshot flags
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Applies the floating-overlay window style (tool window + topmost, no taskbar entry).
    /// </summary>
    /// <remarks>
    /// IMPORTANT: Do <b>not</b> call <c>DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)</c>
    /// on the island window. The island uses <c>AllowsTransparency="True"</c>, which makes WPF
    /// create a layered per-pixel-alpha window. If DWM's rounded-corner preference is applied to
    /// such a window, DWM stops honouring the per-pixel alpha and paints an opaque white
    /// rounded frame plus a system shadow around the layered surface — the "white box" artifact.
    /// </remarks>
    public static void ApplyWindows11OverlayStyle(IntPtr hwnd, bool noActivate = false)
    {
        if (hwnd == IntPtr.Zero) return;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        if (noActivate) exStyle |= WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Enables the immersive dark title bar. Safe for layered (transparent) windows.
    /// </summary>
    public static void ApplyDarkTitleBar(IntPtr hwnd, bool dark = true)
    {
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)); } catch { }
    }

    /// <summary>
    /// Requests DWM rounded corners. Only use on <b>opaque</b> (non-layered) windows; see the
    /// remark on <see cref="ApplyWindows11OverlayStyle"/> for why layered windows must skip it.
    /// </summary>
    public static void ApplyDwmRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int round = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)); } catch { }
    }

    public static bool ForceActivateWindow(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero) return false;
        try
        {
            if (IsIconic(targetHwnd))
            {
                ShowWindow(targetHwnd, SW_RESTORE);
            }
            else
            {
                ShowWindow(targetHwnd, SW_SHOW);
            }

            var fg = GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            uint curThread = GetCurrentThreadId();

            if (fgThread != 0 && fgThread != curThread)
            {
                AttachThreadInput(curThread, fgThread, true);
                SetForegroundWindow(targetHwnd);
                AttachThreadInput(curThread, fgThread, false);
            }
            else
            {
                SetForegroundWindow(targetHwnd);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public sealed record ProcessSnapshotItem(int Pid, int ParentPid, string ExeName);

    /// <summary>
    /// Fast, zero-NuGet process snapshot with parent PIDs via Win32 Toolhelp32.
    /// </summary>
    public static Dictionary<int, ProcessSnapshotItem> GetProcessSnapshot()
    {
        var map = new Dictionary<int, ProcessSnapshotItem>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref entry))
            {
                do
                {
                    int pid = (int)entry.th32ProcessID;
                    int ppid = (int)entry.th32ParentProcessID;
                    map[pid] = new ProcessSnapshotItem(pid, ppid, entry.szExeFile ?? "");
                } while (Process32Next(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    public static void SetLaunchAtStartup(bool enable, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (enable)
            {
                key.SetValue("OpenIsland", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("OpenIsland", throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore registry permission issues
        }
    }
}
