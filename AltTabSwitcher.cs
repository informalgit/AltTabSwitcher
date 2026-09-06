// AltTabSwitcher - macOS-AltTab-style application switcher for Windows.
// Alt+Tab is fully taken over: one entry per application (grouped by exe,
// Z-order MRU), live DWM thumbnails, click an entry to switch, click outside /
// press Esc to cancel, hold Alt and tap Tab to cycle, Shift+Tab reverses.
// Alt+` (PowerToys Window Hopper) and Win+Tab stay untouched.
//
// The switcher UI is a faithful port of PowerToys Window Hopper's overlay
// (src/modules/AltWindowCycle, MIT): two stacked windows - an opaque rounded
// panel that hosts the live DWM thumbnails, and an UpdateLayeredWindow chrome
// on top that draws the WinUI-style cards, headers, accent focus ring and page
// indicator. Theme (light/dark) and the accent color follow the OS. Only the
// floating UI is ever touched; no other window's styles, visibility, taskbar
// or virtual-desktop assignment are modified.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AltTabSwitcher
{
    static class Program
    {
        // ================= Win32 =================
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const int WH_KEYBOARD_LL = 13;
        const int WH_MOUSE_LL = 14;
        const uint LLKHF_UP = 0x80;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_MOUSEWHEEL = 0x020A;
        const int VK_TAB = 0x09;
        const int VK_MENU = 0x12;
        const int VK_LMENU = 0xA4;
        const int VK_RMENU = 0xA5;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;
        const int VK_SHIFT = 0x10;
        const int VK_ESCAPE = 0x1B;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_APPWINDOW = 0x00040000;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_LAYERED = 0x00080000;
        const uint GW_OWNER = 4;
        const int GCLP_HICONSM = -34;
        const int GCLP_HICON = -14;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int MDT_EFFECTIVE_DPI = 0;
        const int SW_RESTORE = 9;
        const uint SHGFI_ICON = 0x100;
        const uint SHGFI_LARGEICON = 0x0;
        const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        const int ULW_ALPHA = 2;
        const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
        const int ICON_SMALL2 = 2, ICON_BIG = 1;
        const uint WM_GETICON = 0x7F;
        const uint SMTO_ABORTIFHUNG = 0x2;

        const int WM_APP_START = 0x8000 + 1;
        const int WM_APP_NEXT = 0x8000 + 2;
        const int WM_APP_PREV = 0x8000 + 3;
        const int WM_APP_COMMIT = 0x8000 + 4;
        const int WM_APP_CANCEL = 0x8000 + 5;
        const int WM_APP_COMMITAT = 0x8000 + 6;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            public bool fVisible;
            public bool fSourceClientAreaOnly;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFOW
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint nCmd);
        [DllImport("user32.dll")]
        static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
        static extern int GetClassLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextW(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassNameW(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int maxCount);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
        [DllImport("dwmapi.dll")]
        static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);
        [DllImport("dwmapi.dll")]
        static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnc);
        [DllImport("dwmapi.dll")]
        static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);
        [DllImport("dwmapi.dll")]
        static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out SIZE psize);
        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        // ================= IVirtualDesktopManager (read-only use) =================
        [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b0f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopManager
        {
            [PreserveSig]
            int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);
        }

        [ComImport, Guid("9acda8ce-73d8-41a4-9373-4030c759a53e")]
        class VirtualDesktopManagerClass { }

        static IVirtualDesktopManager _vdm;

        static bool OnCurrentDesktop(IntPtr hwnd)
        {
            try
            {
                if (_vdm == null) return true;
                bool on;
                if (_vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out on) != 0) return true;
                return on;
            }
            catch { return true; }
        }

        // ================= Hopper port: layout =================
        const int MaxColumns = 6;

        struct OverlayLayout
        {
            public double scale;
            public int pad, gap, tileW, tileH, headerH, previewH, inner, radius, iconSize;
            public int cols, rows, pageSize;
            public int panelX, panelY, panelW, panelH;
        }

        static int Scaled(double scale, int v) { return (int)(v * scale + 0.5); }

        static RECT TileRect(ref OverlayLayout L, int index)
        {
            int col = index % L.cols, row = index / L.cols;
            int left = L.pad + col * (L.tileW + L.gap);
            int top = L.pad + row * (L.tileH + L.gap);
            return new RECT { Left = left, Top = top, Right = left + L.tileW, Bottom = top + L.tileH };
        }

        static RECT PreviewRect(ref OverlayLayout L, RECT tile)
        {
            int stroke = Scaled(L.scale, 1);
            return new RECT { Left = tile.Left + stroke, Top = tile.Top + L.headerH, Right = tile.Right - stroke, Bottom = tile.Bottom - stroke };
        }

        static RECT HeaderRect(ref OverlayLayout L, RECT tile)
        {
            int margin = Scaled(L.scale, 12);
            return new RECT { Left = tile.Left + margin, Top = tile.Top, Right = tile.Right - margin, Bottom = tile.Top + L.headerH };
        }

        static RECT CoverSource(RECT dest, RECT avail)
        {
            int aw = avail.Right - avail.Left, ah = avail.Bottom - avail.Top;
            int dw = dest.Right - dest.Left, dh = dest.Bottom - dest.Top;
            if (aw <= 0 || ah <= 0 || dw <= 0 || dh <= 0) return avail;
            double destA = (double)dw / dh, srcA = (double)aw / ah;
            if (srcA > destA)
            {
                int cw = (int)(ah * destA + 0.5); if (cw < 1) cw = 1;
                int x = avail.Left + (aw - cw) / 2;
                return new RECT { Left = x, Top = avail.Top, Right = x + cw, Bottom = avail.Bottom };
            }
            int ch = (int)(aw / destA + 0.5); if (ch < 1) ch = 1;
            int y = avail.Top + (ah - ch) / 2;
            return new RECT { Left = avail.Left, Top = y, Right = avail.Right, Bottom = y + ch };
        }

        static void ComputeLayout(RECT work, int windowCount, double scale, ref OverlayLayout L)
        {
            L.scale = scale;
            L.pad = Scaled(scale, 32);
            L.gap = Scaled(scale, 26);
            L.tileW = Scaled(scale, 270);
            L.headerH = Scaled(scale, 48);
            L.previewH = Scaled(scale, 142);
            L.inner = Scaled(scale, 6);
            L.radius = Scaled(scale, 10);
            L.iconSize = Scaled(scale, 16);
            L.tileH = L.headerH + L.inner + L.previewH + L.inner;

            int workW = work.Right - work.Left, workH = work.Bottom - work.Top;
            int count = windowCount < 0 ? 0 : windowCount;
            int colsFromWork = (workW - 2 * L.pad + L.gap) / (L.tileW + L.gap);
            if (colsFromWork < 1) colsFromWork = 1;
            L.cols = Math.Min(count, Math.Min(MaxColumns, colsFromWork));
            if (L.cols < 1) L.cols = 1;

            int totalRows = (count + L.cols - 1) / L.cols;
            int rowsFromWork = (workH - 2 * L.pad + L.gap) / (L.tileH + L.gap);
            if (rowsFromWork < 1) rowsFromWork = 1;
            L.rows = Math.Min(totalRows, rowsFromWork);
            L.pageSize = Math.Min(count, L.cols * L.rows);
            L.panelW = 2 * L.pad + L.cols * L.tileW + (L.cols - 1) * L.gap;
            L.panelH = 2 * L.pad + L.rows * L.tileH + Math.Max(0, L.rows - 1) * L.gap;
            L.panelX = work.Left + (workW - L.panelW) / 2;
            L.panelY = work.Top + (workH - L.panelH) / 2;
            if (L.panelX < work.Left) L.panelX = work.Left;
            if (L.panelY < work.Top) L.panelY = work.Top;
        }

        static int PageStartFor(int selected, int windowCount, int pageSize)
        {
            if (windowCount <= 0 || pageSize <= 0) return 0;
            int idx = Math.Max(0, Math.Min(selected, windowCount - 1));
            return (idx / pageSize) * pageSize;
        }

        // ================= theme / accent =================
        static bool LightTheme()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v != 0;
                }
            }
            catch { return false; }
        }

        static Color AccentColor()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    var v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int)
                    {
                        int c = (int)v & 0x00FFFFFF;
                        return Color.FromArgb(255, c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF);
                    }
                }
            }
            catch { }
            return Color.FromArgb(255, 0, 120, 215);
        }

        static Color CardColor(bool light) { return light ? Color.FromArgb(179, 255, 255, 255) : Color.FromArgb(210, 18, 18, 18); }
        static Color CardSolid(bool light) { return light ? Color.FromArgb(255, 248, 248, 248) : Color.FromArgb(255, 20, 20, 20); }
        static Color CardStrokeC(bool light) { return light ? Color.FromArgb(15, 0, 0, 0) : Color.FromArgb(25, 0, 0, 0); }
        static Color PanelStrokeC(bool light) { return light ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(64, 255, 255, 255); }
        static Color PanelFillC(bool light) { return light ? Color.FromArgb(255, 243, 243, 243) : Color.FromArgb(255, 84, 84, 84); }
        static Color HeaderTextC(bool light) { return light ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 235, 235, 235); }
        static Color FocusShadowC(bool light) { return light ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0); }

        // ================= state =================
        class AppEntry
        {
            public IntPtr ReprHwnd;
            public string Exe;
            public string Title;
            public Icon Icon;      // shared handle (window icons) or owned clone
            public IntPtr Thumb = IntPtr.Zero;
        }

        static IntPtr _hook = IntPtr.Zero;
        static IntPtr _mouseHook = IntPtr.Zero;
        static HookProc _hookProc;
        static HookProc _mouseHookProc;
        static Mutex _mutex;
        static StreamWriter _log;
        static bool _enabled = true;
        static bool _exitRequested;

        static PanelForm _panel;
        static ChromeForm _chrome;
        static MsgForm _msg;

        static List<AppEntry> _apps = new List<AppEntry>();
        static List<IntPtr> _thumbs = new List<IntPtr>();
        static bool _session;
        static int _index;
        static int _pageStart;
        static IntPtr _fgHwnd;
        static OverlayLayout _layout;
        static RECT _panelRect;
        static double _scale = 1.0;

        static void Log(string msg)
        {
            if (_log == null) return;
            try { _log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + msg); _log.Flush(); }
            catch { }
        }

        static void Post(int m) { PostMessageW(_msg.Handle, (uint)m, IntPtr.Zero, IntPtr.Zero); }
        static void PostAt(int m, int i) { PostMessageW(_msg.Handle, (uint)m, (IntPtr)i, IntPtr.Zero); }

        // ================= windows =================
        class MsgForm : Form
        {
            public MsgForm() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; Opacity = 0; }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; }
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg >= WM_APP_START && m.Msg <= WM_APP_COMMITAT) { HandleAppMsg(m.Msg, m.WParam); return; }
                base.WndProc(ref m);
            }
        }

        // opaque rounded panel hosting the live DWM thumbnails (thumbHost role)
        class PanelForm : Form
        {
            public PanelForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST; return cp; }
            }
        }

        // layered chrome on top of the panel (cards, headers, focus ring, pages)
        class ChromeForm : Form
        {
            public ChromeForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_LAYERED; return cp; }
            }
        }

        // ================= enumeration =================
        static string ExePathOfPid(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref size) && size > 0)
                    return sb.ToString(0, size);
                return null;
            }
            finally { CloseHandle(h); }
        }

        static IntPtr FindWindowByClass(IntPtr parent, string className)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, delegate(IntPtr h, IntPtr lp)
            {
                var sb = new StringBuilder(256);
                GetClassNameW(h, sb, sb.Capacity);
                if (sb.ToString() == className) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static string WindowExe(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return null;
            string exe = ExePathOfPid(pid);
            if (exe != null && exe.EndsWith("applicationframehost.exe", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr child = FindWindowByClass(hwnd, "Windows.UI.Core.CoreWindow");
                if (child != IntPtr.Zero)
                {
                    uint cpid;
                    GetWindowThreadProcessId(child, out cpid);
                    string cexe = ExePathOfPid(cpid);
                    if (cexe != null) return cexe;
                }
                return null;
            }
            return exe;
        }

        static bool AltTabEligible(IntPtr hwnd)
        {
            if (!IsWindowVisible(hwnd)) return false;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
            var title = new StringBuilder(256);
            GetWindowTextW(hwnd, title, 256);
            if (title.Length == 0) return false;
            IntPtr owner = GetWindow(hwnd, GW_OWNER);
            if (owner != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;
            RECT r;
            if (GetWindowRect(hwnd, out r) && r.Right - r.Left <= 1 && r.Bottom - r.Top <= 1) return false;
            return true;
        }

        static Icon GetAppIcon(IntPtr hwnd, string exe)
        {
            // prefer the window's own icons (exactly what the taskbar shows)
            IntPtr res;
            if (SendMessageTimeoutW(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out res) != IntPtr.Zero && res != IntPtr.Zero)
                return Icon.FromHandle(res);
            if (SendMessageTimeoutW(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out res) != IntPtr.Zero && res != IntPtr.Zero)
                return Icon.FromHandle(res);
            int cls = GetClassLong(hwnd, GCLP_HICONSM);
            if (cls != 0) return Icon.FromHandle((IntPtr)cls);
            cls = GetClassLong(hwnd, GCLP_HICON);
            if (cls != 0) return Icon.FromHandle((IntPtr)cls);

            try
            {
                var fi = new SHFILEINFOW();
                IntPtr r2 = SHGetFileInfoW(exe, 0x80, ref fi, (uint)Marshal.SizeOf(typeof(SHFILEINFOW)), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                if (r2 != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                    return Icon.FromHandle(fi.hIcon).Clone() as Icon;
            }
            catch { }
            return SystemIcons.Application;
        }

        // ================= state machine =================
        static void HandleAppMsg(int msg, IntPtr wparam)
        {
            switch (msg)
            {
                case WM_APP_START:
                    if (!_session) StartSession();
                    if (_session && !AltDown()) Commit();  // quick tap: Alt already released
                    break;
                case WM_APP_NEXT: if (_session) MoveIndex(1); break;
                case WM_APP_PREV: if (_session) MoveIndex(-1); break;
                case WM_APP_COMMIT: if (_session) Commit(); break;
                case WM_APP_CANCEL: if (_session) Cancel(); break;
                case WM_APP_COMMITAT: if (_session) { _index = (int)wparam; RenderChrome(); Commit(); } break;
            }
        }

        static double MonitorScale(IntPtr hwnd, out RECT work)
        {
            IntPtr m = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (m != IntPtr.Zero && GetMonitorInfoW(m, ref mi)) work = mi.rcWork;
            else work = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            uint dx, dy;
            if (m != IntPtr.Zero && GetDpiForMonitor(m, MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dx != 0)
                return dx / 96.0;
            return 1.0;
        }

        static void StartSession()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            _fgHwnd = fg;
            string fgExe = WindowExe(fg);
            if (fgExe == null) return;

            var order = new List<AppEntry>();
            var byExe = new Dictionary<string, AppEntry>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lp)
            {
                if (!AltTabEligible(hwnd)) return true;
                if (!OnCurrentDesktop(hwnd)) return true;
                string exe = WindowExe(hwnd);
                if (exe == null) return true;
                AppEntry e;
                if (!byExe.TryGetValue(exe, out e))
                {
                    e = new AppEntry { Exe = exe, ReprHwnd = hwnd };
                    var t = new StringBuilder(256);
                    GetWindowTextW(hwnd, t, 256);
                    e.Title = t.ToString();
                    byExe[exe] = e;
                    order.Add(e);
                }
                return true;
            }, IntPtr.Zero);
            if (order.Count < 2) return;

            RECT work;
            _scale = MonitorScale(fg, out work);
            ComputeLayout(work, order.Count, _scale, ref _layout);

            int fgIdx = 0;
            for (int i = 0; i < order.Count; i++)
                if (order[i].ReprHwnd == fg) { fgIdx = i; break; }
            if (fgIdx != 0) { var e = order[fgIdx]; order.RemoveAt(fgIdx); order.Insert(0, e); }
            _apps = order;
            _index = 1;
            _pageStart = 0;

            foreach (var e in _apps) e.Icon = GetAppIcon(e.ReprHwnd, e.Exe);

            ShowPanel();
            RegisterThumbnails();
            RenderChrome();
            InstallMouseHook();

            _session = true;
            Log("session start, apps=" + _apps.Count + " scale=" + _scale.ToString("0.##")
                + " panel=" + _layout.panelW + "x" + _layout.panelH + " tile=" + _layout.tileW + "x" + _layout.tileH);
        }

        static void ShowPanel()
        {
            _panel.BackColor = PanelFillC(LightTheme());
            SetWindowPos(_panel.Handle, IntPtr.Zero, _layout.panelX, _layout.panelY, _layout.panelW, _layout.panelH, 0x0040 /*SWP_SHOWWINDOW*/);
            IntPtr rgn = CreateRoundRectRgn(0, 0, _layout.panelW + 1, _layout.panelH + 1, 2 * Scaled(_scale, 8), 2 * Scaled(_scale, 8));
            if (rgn != IntPtr.Zero)
            {
                if (!SetWindowRgn(_panel.Handle, rgn, false)) DeleteObject(rgn);
            }
        }

        static void MoveIndex(int delta)
        {
            _index = ((_index + delta) % _apps.Count + _apps.Count) % _apps.Count;
            int ps = PageStartFor(_index, _apps.Count, _layout.pageSize);
            if (ps != _pageStart)
            {
                _pageStart = ps;
                RegisterThumbnails();
            }
            RenderChrome();
        }

        static void RegisterThumbnails()
        {
            UnregisterThumbnails();
            int pageEnd = Math.Min(_pageStart + _layout.pageSize, _apps.Count);
            for (int i = _pageStart, slot = 0; i < pageEnd; ++i, ++slot)
            {
                var app = _apps[i];
                RECT tile = TileRect(ref _layout, slot);
                RECT pv = PreviewRect(ref _layout, tile);
                IntPtr tid;
                if (DwmRegisterThumbnail(_panel.Handle, app.ReprHwnd, out tid) != 0 || tid == IntPtr.Zero)
                    continue;
                _thumbs.Add(tid);

                RECT client = new RECT();
                SIZE srcSize;
                bool clientOnly = !IsIconic(app.ReprHwnd) && GetClientRect(app.ReprHwnd, out client);
                if (clientOnly) clientOnly = client.Right - client.Left > 0 && client.Bottom - client.Top > 0;
                if (clientOnly)
                {
                    srcSize.cx = client.Right - client.Left;
                    srcSize.cy = client.Bottom - client.Top;
                }
                else if (DwmQueryThumbnailSourceSize(tid, out srcSize) != 0)
                {
                    srcSize.cx = 0; srcSize.cy = 0;
                }

                RECT avail = new RECT { Left = 0, Top = 0, Right = srcSize.cx, Bottom = srcSize.cy };
                if (clientOnly)
                {
                    int ix = Math.Min(2, (avail.Right - avail.Left) / 4);
                    int iy = Math.Min(2, (avail.Bottom - avail.Top) / 4);
                    avail.Left += ix; avail.Right -= ix;
                    avail.Top += iy; avail.Bottom -= iy;
                }
                RECT rcSrc = CoverSource(pv, avail);

                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10, // DEST|SOURCE|OPACITY|VISIBLE|CLIENTONLY
                    rcDestination = pv,
                    rcSource = rcSrc,
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = clientOnly
                };
                DwmUpdateThumbnailProperties(tid, ref props);
            }
        }

        static void UnregisterThumbnails()
        {
            foreach (var t in _thumbs) { try { DwmUnregisterThumbnail(t); } catch { } }
            _thumbs.Clear();
        }

        static void Commit()
        {
            if (_apps.Count == 0) { Cancel(); return; }
            var app = _apps[_index];
            EndSession();
            ForceForeground(app.ReprHwnd);
            Log("commit -> 0x" + app.ReprHwnd.ToInt64().ToString("X") + " " + Path.GetFileName(app.Exe));
        }

        static void Cancel()
        {
            EndSession();
            Log("cancel");
        }

        static void EndSession()
        {
            UninstallMouseHook();
            UnregisterThumbnails();
            _panel.Hide();
            _chrome.Hide();
            foreach (var e in _apps)
            {
                if (e.Icon != null) e.Icon.Dispose();
                e.Icon = null;
            }
            _apps.Clear();
            _session = false;
        }

        static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                IntPtr fg = GetForegroundWindow();
                uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
                uint myThread = GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != myThread) attached = AttachThreadInput(myThread, fgThread, true);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetFocus(hwnd);
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }
            catch { }
        }

        // ================= chrome rendering (UpdateLayeredWindow, Hopper-style) =================
        static void RenderChrome()
        {
            int w = _layout.panelW, h = _layout.panelH;
            if (w <= 0 || h <= 0) return;
            bool light = LightTheme();
            Color accent = AccentColor();

            IntPtr screenDc = GetDC(IntPtr.Zero);
            var bmi = new BITMAPINFOHEADER();
            bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.biWidth = w; bmi.biHeight = -h; bmi.biPlanes = 1; bmi.biBitCount = 32; bmi.biCompression = 0;
            IntPtr bits;
            IntPtr dib = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) { ReleaseDC(IntPtr.Zero, screenDc); return; }

            try
            {
                using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    var panelPath = RoundRect(0, 0, w, h, Scaled(_scale, 8));
                    using (var pen = new Pen(PanelStrokeC(light), Math.Max(1, Scaled(_scale, 1))))
                        g.DrawPath(pen, panelPath);
                    panelPath.Dispose();

                    Color textClr = HeaderTextC(light);
                    int pageEnd = Math.Min(_pageStart + _layout.pageSize, _apps.Count);
                    for (int i = _pageStart, slot = 0; i < pageEnd; ++i, ++slot)
                    {
                        var app = _apps[i];
                        RECT tile = TileRect(ref _layout, slot);
                        bool sel = i == _index;
                        RECT pv = PreviewRect(ref _layout, tile);

                        GraphicsPath cardPath = RoundRect(tile.Left, tile.Top, tile.Right, tile.Bottom, _layout.radius);
                        using (var b = new SolidBrush(CardColor(light))) g.FillPath(b, cardPath);
                        using (var p = new Pen(CardStrokeC(light), Math.Max(1, Scaled(_scale, 1)))) g.DrawPath(p, cardPath);

                        int pw = pv.Right - pv.Left, ph = pv.Bottom - pv.Top;
                        if (pw > 0 && ph > 0)
                        {
                            var pvF = new RectangleF(pv.Left, pv.Top, pw, ph);
                            GraphicsPath hole = BottomRoundRect(pvF, _layout.radius);
                            using (var back = new SolidBrush(CardSolid(light)))
                            {
                                g.SetClip(cardPath);
                                g.FillRectangle(back, pv.Left, pv.Top, pw, ph);
                                g.ResetClip();
                            }
                            g.CompositingMode = CompositingMode.SourceCopy;
                            using (var tr = new SolidBrush(Color.FromArgb(0, 0, 0, 0)))
                                g.FillPath(tr, hole);
                            g.CompositingMode = CompositingMode.SourceOver;
                            hole.Dispose();
                        }

                        RECT hdr = HeaderRect(ref _layout, tile);
                        int textLeft = hdr.Left;
                        if (app.Icon != null)
                        {
                            int iy = tile.Top + (_layout.headerH - _layout.iconSize) / 2;
                            g.DrawIcon(app.Icon, new Rectangle(hdr.Left, iy, _layout.iconSize, _layout.iconSize));
                            textLeft = hdr.Left + _layout.iconSize + Scaled(_scale, 8);
                        }
                        string title = string.IsNullOrEmpty(app.Title) ? Path.GetFileName(app.Exe) : app.Title;
                        using (var f = new Font("Segoe UI", Scaled(_scale, 14), GraphicsUnit.Pixel))
                        using (var b = new SolidBrush(textClr))
                        {
                            var rect = new RectangleF(textLeft, tile.Top, hdr.Right - textLeft, _layout.headerH);
                            var sf = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
                            g.DrawString(title, f, b, rect, sf);
                            sf.Dispose();
                        }

                        if (sel)
                        {
                            int gPad = Scaled(_scale, 6), gOut = gPad + Scaled(_scale, 2);
                            int outerR = Scaled(_scale, 18), innerR = outerR - Scaled(_scale, 2);
                            GraphicsPath ringIn = RoundRect(tile.Left - gPad, tile.Top - gPad, tile.Right + gPad, tile.Bottom + gPad, innerR);
                            GraphicsPath ringOut = RoundRect(tile.Left - gOut, tile.Top - gOut, tile.Right + gOut, tile.Bottom + gOut, outerR);
                            using (var p1 = new Pen(FocusShadowC(light), Math.Max(1, Scaled(_scale, 1))))
                                g.DrawPath(p1, ringIn);
                            using (var p2 = new Pen(accent, Math.Max(2, Scaled(_scale, 4))))
                                g.DrawPath(p2, ringOut);
                            ringIn.Dispose(); ringOut.Dispose();
                        }
                        cardPath.Dispose();
                    }

                    int pageSize = _layout.pageSize > 0 ? _layout.pageSize : 1;
                    int totalPages = (_apps.Count + pageSize - 1) / pageSize;
                    if (totalPages > 1)
                    {
                        int cur = _pageStart / pageSize + 1;
                        using (var f = new Font("Segoe UI", Scaled(_scale, 14), GraphicsUnit.Pixel))
                        using (var b = new SolidBrush(textClr))
                        {
                            var rect = new RectangleF(_layout.pad, h - _layout.pad, w - 2 * _layout.pad, _layout.pad);
                            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
                            g.DrawString(cur + " / " + totalPages, f, b, rect, sf);
                            sf.Dispose();
                        }
                    }
                    g.Flush(FlushIntention.Sync);
                }

                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr old = SelectObject(memDc, dib);
                var dst = new POINT { X = _layout.panelX, Y = _layout.panelY };
                var size = new SIZE { cx = w, cy = h };
                var src = new POINT { X = 0, Y = 0 };
                var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                bool ulw = UpdateLayeredWindow(_chrome.Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
                SelectObject(memDc, old);
                DeleteDC(memDc);
                if (ulw)
                {
                    // UpdateLayeredWindow does NOT make a hidden window visible;
                    // Hopper shows both windows explicitly (SWP_SHOWWINDOW).
                    SetWindowPos(_chrome.Handle, IntPtr.Zero, 0, 0, 0, 0,
                                 0x1 | 0x2 | 0x10 | 0x40 /*NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW*/);
                    // keep the opaque panel strictly below the chrome layer
                    SetWindowPos(_panel.Handle, _chrome.Handle, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
                }
                else
                {
                    Log("UpdateLayeredWindow failed err=" + Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                DeleteObject(dib);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static GraphicsPath RoundRect(int l, int t, int r, int b, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 0 || d >= r - l || d >= b - t) { p.AddRectangle(new Rectangle(l, t, r - l, b - t)); return p; }
            p.AddArc(l, t, d, d, 180, 90);
            p.AddArc(r - d, t, d, d, 270, 90);
            p.AddArc(r - d, b - d, d, d, 0, 90);
            p.AddArc(l, b - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static GraphicsPath BottomRoundRect(RectangleF r, int rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (d <= 0 || d >= r.Width || d >= r.Height) { p.AddRectangle(r); return p; }
            p.AddLine(r.X, r.Y, r.Right, r.Y);
            p.AddLine(r.Right, r.Y, r.Right, r.Bottom - rad);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ================= keyboard hook =================
        static bool AltDown()
        {
            return (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0
                || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0
                || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        }

        static bool WinDown()
        {
            return (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        }

        static IntPtr KbHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _enabled)
            {
                var s = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool up = (s.flags & LLKHF_UP) != 0;

                if (s.vkCode == VK_TAB && !up && AltDown() && !WinDown())
                {
                    if (!_session) Post(WM_APP_START);
                    else Post((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ? WM_APP_PREV : WM_APP_NEXT);
                    return (IntPtr)1;
                }
                if (_session && s.vkCode == VK_ESCAPE)
                {
                    if (up) Post(WM_APP_CANCEL);
                    return (IntPtr)1;
                }
                if (_session && (s.vkCode == VK_MENU || s.vkCode == VK_LMENU || s.vkCode == VK_RMENU) && up)
                    Post(WM_APP_COMMIT);   // let the Alt release through
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // ================= mouse hook =================
        static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _session && _enabled)
            {
                int msg = wParam.ToInt32();
                var s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if (msg == WM_LBUTTONDOWN)
                {
                    bool insidePanel = s.pt.X >= _panelRect.Left && s.pt.X < _panelRect.Right
                                    && s.pt.Y >= _panelRect.Top && s.pt.Y < _panelRect.Bottom;
                    if (insidePanel)
                    {
                        int slot = SlotAtPhysical(s.pt.X, s.pt.Y);
                        if (slot >= 0)
                        {
                            PostAt(WM_APP_COMMITAT, _pageStart + slot);
                            return (IntPtr)1;
                        }
                        return IntPtr.Zero;   // panel background: ignore
                    }
                    Post(WM_APP_CANCEL);
                    return (IntPtr)1;         // swallow outside clicks
                }
                if (msg == WM_MOUSEWHEEL)
                {
                    short delta = (short)((s.mouseData >> 16) & 0xFFFF);
                    Post(delta > 0 ? WM_APP_PREV : WM_APP_NEXT);
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        static void InstallMouseHook()
        {
            _panelRect = new RECT { Left = _layout.panelX, Top = _layout.panelY, Right = _layout.panelX + _layout.panelW, Bottom = _layout.panelY + _layout.panelH };
            if (_mouseHook == IntPtr.Zero)
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
        }

        static void UninstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        }

        static int SlotAtPhysical(int px, int py)
        {
            int lx = px - _panelRect.Left, ly = py - _panelRect.Top;
            for (int slot = 0; slot < _layout.pageSize; slot++)
            {
                RECT t = TileRect(ref _layout, slot);
                if (lx >= t.Left && lx < t.Right && ly >= t.Top && ly < t.Bottom)
                    return slot;
            }
            return -1;
        }

        // ================= infra =================
        static void SetStartup(bool add)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (add) key.SetValue("AltTabSwitcher", "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue("AltTabSwitcher") != null) key.DeleteValue("AltTabSwitcher");
                }
            }
            catch { }
        }

        static bool StartupEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return key != null && key.GetValue("AltTabSwitcher") != null;
            }
            catch { return false; }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            _mutex = new Mutex(true, "Local\\AltTabSwitcher", out created);
            if (!created) return;

            foreach (string a in args)
                if (a == "--log")
                    _log = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "alttabswitcher.log"), false);

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _msg = new MsgForm();
            IntPtr hMsg = _msg.Handle;
            _panel = new PanelForm();
            _chrome = new ChromeForm();

            try { _vdm = (IVirtualDesktopManager)new VirtualDesktopManagerClass(); }
            catch { _vdm = null; }

            _hookProc = KbHookProc;
            _mouseHookProc = MouseHookProc;
            using (var cur = Process.GetCurrentProcess())
            using (var mod = cur.MainModule)
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(mod.ModuleName), 0);

            var menu = new ContextMenu();
            var miToggle = new MenuItem("Enabled");
            miToggle.Checked = true;
            miToggle.Click += delegate { _enabled = !_enabled; miToggle.Checked = _enabled; };
            var miStartup = new MenuItem("Start with Windows");
            miStartup.Checked = StartupEnabled();
            miStartup.Click += delegate { bool add = !StartupEnabled(); SetStartup(add); miStartup.Checked = add; };
            var miExit = new MenuItem("Exit");
            miExit.Click += delegate { _exitRequested = true; };
            menu.MenuItems.Add(miToggle);
            menu.MenuItems.Add(miStartup);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(miExit);

            var icon = new NotifyIcon();
            icon.Icon = SystemIcons.Application;
            icon.Text = "AltTab Switcher - Hopper-style UI, one entry per app";
            icon.ContextMenu = menu;
            icon.Visible = true;

            var exitTimer = new System.Windows.Forms.Timer { Interval = 200 };
            exitTimer.Tick += delegate
            {
                if (!_exitRequested) return;
                EndSession();
                exitTimer.Stop();
                icon.Visible = false;
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                Application.Exit();
            };
            exitTimer.Start();

            Application.Run();

            EndSession();
            icon.Visible = false;
            if (_log != null) _log.Dispose();
        }
    }
}
