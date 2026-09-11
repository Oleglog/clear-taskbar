using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClearTaskbar;

static class Program
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const uint DWMWA_CLOAKED = 14;

    private static IntPtr _hookForeground;
    private static IntPtr _hookShowHide;
    private static IntPtr _hookLocation;
    private static IntPtr _hookCloak;
    private static WinEventDelegate? _winEventProc;

    private static bool? _currentTransparentState = null;
    private static NotifyIcon? _trayIcon;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, IntPtr windowTitle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    private static long GetWindowLong(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8)
            return GetWindowLongPtr64(hWnd, nIndex).ToInt64();
        return GetWindowLong32(hWnd, nIndex);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        SetupTrayIcon();

        _winEventProc = new WinEventDelegate(OnWinEvent);

        // Системные события Windows (0% CPU, процесс спит в ожидании событий):
        _hookForeground = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        _hookLocation = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE,
            EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        _hookShowHide = SetWinEventHook(
            EVENT_OBJECT_SHOW,
            EVENT_OBJECT_HIDE,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        _hookCloak = SetWinEventHook(
            EVENT_OBJECT_CLOAKED,
            EVENT_OBJECT_UNCLOAKED,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        // Первичная проверка при старте
        UpdateTaskbarState();

        Application.Run();

        Cleanup();
    }

    private static void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // Проверяем только оконные события (idObject == 0 означает OBJID_WINDOW)
        if (idObject == 0)
        {
            UpdateTaskbarState();
        }
    }

    private const string RUN_REG_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string APP_NAME = "ClearTaskbar";

    private static void SetupTrayIcon()
    {
        var contextMenu = new ContextMenuStrip();

        var startupItem = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            CheckOnClick = true,
            Checked = IsRunAtStartup()
        };
        startupItem.CheckedChanged += (s, e) =>
        {
            SetRunAtStartup(startupItem.Checked);
        };
        contextMenu.Items.Add(startupItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Выход", null, (s, e) => Application.Exit());
        contextMenu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Text = "Clear Taskbar",
            Visible = true
        };
    }

    private static bool IsRunAtStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RUN_REG_KEY, false);
            return key?.GetValue(APP_NAME) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RUN_REG_KEY, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Application.ExecutablePath;
                key.SetValue(APP_NAME, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(APP_NAME, false);
            }
        }
        catch { }
    }

    private static void UpdateTaskbarState()
    {
        if (HasAnyMaximizedWindow())
        {
            ApplyState(false);
            return;
        }

        if (IsStartMenuVisible())
        {
            ApplyState(false);
            return;
        }

        ApplyState(true);
    }

    private static void ApplyState(bool transparent)
    {
        if (_currentTransparentState != transparent)
        {
            SetTaskbarAppearance(transparent);
            _currentTransparentState = transparent;
        }
    }

    private static bool IsStartMenuVisible()
    {
        IntPtr startHwnd = FindWindow("Windows.UI.Core.CoreWindow", "Поиск");
        if (startHwnd == IntPtr.Zero)
            startHwnd = FindWindow("Windows.UI.Core.CoreWindow", "Start");
        if (startHwnd == IntPtr.Zero)
            startHwnd = FindWindow("Windows.UI.Core.CoreWindow", "Search");

        if (startHwnd != IntPtr.Zero && IsWindowVisible(startHwnd) && !IsCloaked(startHwnd))
        {
            return true;
        }

        return false;
    }

    private static bool HasAnyMaximizedWindow()
    {
        bool foundMaximized = false;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd) || IsCloaked(hWnd))
                return true;

            long exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return true;

            var sb = new StringBuilder(64);
            GetClassName(hWnd, sb, 64);
            string cls = sb.ToString();

            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" ||
                cls == "Windows.UI.Core.CoreWindow")
            {
                return true;
            }

            if (IsZoomed(hWnd))
            {
                foundMaximized = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return foundMaximized;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
        {
            return cloaked != 0;
        }
        return false;
    }

    private static void SetTaskbarAppearance(bool transparent)
    {
        IntPtr mainHwnd = FindWindow("Shell_TrayWnd", null);

        if (transparent)
        {
            ApplyAccentPolicy(mainHwnd, 2, 2, 0);

            IntPtr secHwnd = IntPtr.Zero;
            while ((secHwnd = FindWindowEx(IntPtr.Zero, secHwnd, "Shell_SecondaryTrayWnd", IntPtr.Zero)) != IntPtr.Zero)
            {
                ApplyAccentPolicy(secHwnd, 2, 2, 0);
            }
        }
        else
        {
            if (mainHwnd != IntPtr.Zero)
            {
                SendMessage(mainHwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);
            }

            IntPtr secHwnd = IntPtr.Zero;
            while ((secHwnd = FindWindowEx(IntPtr.Zero, secHwnd, "Shell_SecondaryTrayWnd", IntPtr.Zero)) != IntPtr.Zero)
            {
                SendMessage(secHwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);
            }
        }
    }

    private static void ApplyAccentPolicy(IntPtr hwnd, int accentState, int accentFlags, int color)
    {
        if (hwnd == IntPtr.Zero) return;

        ACCENT_POLICY policy = new ACCENT_POLICY
        {
            AccentState = accentState,
            AccentFlags = accentFlags,
            GradientColor = color,
            AnimationId = 0
        };

        int size = Marshal.SizeOf(policy);
        IntPtr pData = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(policy, pData, false);

        WINCOMPATTRDATA data = new WINCOMPATTRDATA
        {
            Attribute = 19,
            Data = pData,
            SizeOfData = size
        };

        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(pData);
    }

    private static void Cleanup()
    {
        if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
        if (_hookLocation != IntPtr.Zero) UnhookWinEvent(_hookLocation);
        if (_hookShowHide != IntPtr.Zero) UnhookWinEvent(_hookShowHide);
        if (_hookCloak != IntPtr.Zero) UnhookWinEvent(_hookCloak);

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        SetTaskbarAppearance(false);
    }
}
