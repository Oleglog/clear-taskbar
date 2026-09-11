using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ClearTaskbar;

static class Program
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private static IntPtr _hookForeground;
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, IntPtr windowTitle);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

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

        _winEventProc = new WinEventDelegate(OnWinEvent);

        _hookForeground = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        SetupTrayIcon();

        // Проверяем начальное состояние
        UpdateTaskbarState();

        Application.Run();

        Cleanup();
    }

    private static void SetupTrayIcon()
    {
        var contextMenu = new ContextMenuStrip();
        var exitItem = new ToolStripMenuItem("Выход", null, (s, e) => Application.Exit());
        contextMenu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Text = "Clear Taskbar (фоновый режим)",
            Visible = true
        };
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
        UpdateTaskbarState();
    }

    private static void UpdateTaskbarState()
    {
        IntPtr fg = GetForegroundWindow();
        bool isDesktop = IsDesktopWindow(fg);

        if (_currentTransparentState != isDesktop)
        {
            SetTransparency(isDesktop);
            _currentTransparentState = isDesktop;
        }
    }

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return true;

        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        string className = sb.ToString();

        // Рабочий стол:
        // Progman (десктоп)
        // WorkerW (фон при активных обоях)
        return className == "Progman" || className == "WorkerW";
    }

    private static void SetTransparency(bool transparent)
    {
        // 2 = ACCENT_ENABLE_TRANSPARENTGRADIENT (полная прозрачность)
        // 0 = ACCENT_DISABLED (возврат дефолта)
        int accentState = transparent ? 2 : 0;
        int accentFlags = transparent ? 2 : 0;

        ApplyToTaskbars(accentState, accentFlags);
    }

    private static void ApplyToTaskbars(int accentState, int accentFlags)
    {
        ACCENT_POLICY policy = new ACCENT_POLICY
        {
            AccentState = accentState,
            AccentFlags = accentFlags,
            GradientColor = 0,
            AnimationId = 0
        };

        int size = Marshal.SizeOf(policy);
        IntPtr pData = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(policy, pData, false);

        WINCOMPATTRDATA data = new WINCOMPATTRDATA
        {
            Attribute = 19, // WCA_ACCENT_POLICY
            Data = pData,
            SizeOfData = size
        };

        IntPtr mainHwnd = FindWindow("Shell_TrayWnd", null);
        if (mainHwnd != IntPtr.Zero)
        {
            SetWindowCompositionAttribute(mainHwnd, ref data);
        }

        IntPtr secHwnd = IntPtr.Zero;
        while ((secHwnd = FindWindowEx(IntPtr.Zero, secHwnd, "Shell_SecondaryTrayWnd", IntPtr.Zero)) != IntPtr.Zero)
        {
            SetWindowCompositionAttribute(secHwnd, ref data);
        }

        Marshal.FreeHGlobal(pData);
    }

    private static void Cleanup()
    {
        if (_hookForeground != IntPtr.Zero)
            UnhookWinEvent(_hookForeground);

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        SetTransparency(false);
    }
}
