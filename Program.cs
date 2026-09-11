using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ClearTaskbar;

static class Program
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private static IntPtr _hookForeground;
    private static IntPtr _hookShowHide;
    private static IntPtr _hookCloak;
    private static WinEventDelegate? _winEventProc;

    private static System.Windows.Forms.Timer? _pollTimer;
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

        // Хук на смену активного окна
        _hookForeground = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        // Хук на открытие/закрытие окон (Пуск, меню, всплывающие окна)
        _hookShowHide = SetWinEventHook(
            EVENT_OBJECT_SHOW,
            EVENT_OBJECT_HIDE,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        // Хук на UWP-окна (Пуск, Action Center часто cloaked/uncloaked)
        _hookCloak = SetWinEventHook(
            EVENT_OBJECT_CLOAKED,
            EVENT_OBJECT_UNCLOAKED,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        // Легкий таймер на случай пропущенных сообщений WinEvent (150 мс, 0% CPU)
        _pollTimer = new System.Windows.Forms.Timer
        {
            Interval = 150
        };
        _pollTimer.Tick += (s, e) => UpdateTaskbarState();
        _pollTimer.Start();

        SetupTrayIcon();

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
            Text = "Clear Taskbar",
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
        // 1. Проверяем, открыто ли меню «Пуск» или поиск Cortana/Search
        if (IsStartMenuOpen())
        {
            ApplyState(false);
            return;
        }

        // 2. Проверяем активное окно на переднем плане
        IntPtr fg = GetForegroundWindow();
        bool isDesktop = IsDesktopWindow(fg);

        ApplyState(isDesktop);
    }

    private static void ApplyState(bool transparent)
    {
        if (_currentTransparentState != transparent)
        {
            SetTransparency(transparent);
            _currentTransparentState = transparent;
        }
    }

    private static bool IsStartMenuOpen()
    {
        // В Windows 10 Пуск это Windows.UI.Core.CoreWindow в процессе StartMenuExperienceHost
        // Также проверяем класс "Windows.UI.Core.CoreWindow" с заголовком "Start" или "Поиск"
        IntPtr startHwnd = FindWindow("Windows.UI.Core.CoreWindow", "Start");
        if (startHwnd != IntPtr.Zero && IsWindowVisible(startHwnd))
            return true;

        IntPtr searchHwnd = FindWindow("Windows.UI.Core.CoreWindow", "Search");
        if (searchHwnd != IntPtr.Zero && IsWindowVisible(searchHwnd))
            return true;

        IntPtr actionCenterHwnd = FindWindow("Windows.UI.Core.CoreWindow", "Action center");
        if (actionCenterHwnd != IntPtr.Zero && IsWindowVisible(actionCenterHwnd))
            return true;

        return false;
    }

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return true;

        if (hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
            return true;

        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        string className = sb.ToString();

        // Если активна сама панель задач (например кликнули по ней или трею) - считаем это рабочим столом
        if (className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd")
            return true;

        // Рабочий стол Windows 10
        if (className == "Progman" || className == "WorkerW")
            return true;

        return false;
    }

    private static void SetTransparency(bool transparent)
    {
        // 2 = ACCENT_ENABLE_TRANSPARENTGRADIENT, 0 = ACCENT_DISABLED
        int accentState = transparent ? 2 : 0;
        int accentFlags = transparent ? 2 : 0;

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
        _pollTimer?.Stop();
        _pollTimer?.Dispose();

        if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
        if (_hookShowHide != IntPtr.Zero) UnhookWinEvent(_hookShowHide);
        if (_hookCloak != IntPtr.Zero) UnhookWinEvent(_hookCloak);

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        SetTransparency(false);
    }
}
