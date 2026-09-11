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
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
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

        _hookShowHide = SetWinEventHook(
            EVENT_OBJECT_SHOW,
            EVENT_OBJECT_HIDE,
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

        _hookCloak = SetWinEventHook(
            EVENT_OBJECT_CLOAKED,
            EVENT_OBJECT_UNCLOAKED,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        _pollTimer = new System.Windows.Forms.Timer
        {
            Interval = 100
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
        // 1. Меню «Пуск», Поиск или Центр действий открыты -> возвращаем стандартный вид
        if (IsStartOrFlyoutOpen())
        {
            ApplyState(false);
            return;
        }

        // 2. Проверяем активное окно на переднем плане
        IntPtr fg = GetForegroundWindow();

        // Если активен рабочий стол или сама панель задач -> прозрачная
        if (IsDesktopOrTaskbar(fg))
        {
            ApplyState(true);
            return;
        }

        // 3. Если активно приложение:
        // Панель должна становиться непрозрачной ТОЛЬКО если приложение развернуто на весь экран (IsZoomed)
        // Если окно обычного размера (не на весь экран) -> панель остаётся прозрачной!
        if (IsNormalUserWindow(fg))
        {
            bool isMaximized = IsZoomed(fg);
            ApplyState(!isMaximized);
            return;
        }

        // По умолчанию для неизвестных системных слоев
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

    private static bool IsStartOrFlyoutOpen()
    {
        // UWP-окна Windows 10: Пуск, Поиск, Центр действий
        string[] titles = { "Start", "Search", "Action center" };
        foreach (var title in titles)
        {
            IntPtr hwnd = FindWindow("Windows.UI.Core.CoreWindow", title);
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsCloaked(hwnd))
            {
                return true;
            }
        }

        // Всплывающие меню системных значков в трее (громкость, сеть, дата/время)
        IntPtr flyout = FindWindow("Windows.UI.Core.CoreWindow", null);
        if (flyout != IntPtr.Zero && IsWindowVisible(flyout) && !IsCloaked(flyout))
        {
            var sb = new StringBuilder(256);
            GetClassName(flyout, sb, sb.Capacity);
            if (sb.ToString() == "Windows.UI.Core.CoreWindow")
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == flyout)
                    return true;
            }
        }

        return false;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
        {
            return cloaked != 0;
        }
        return false;
    }

    private static bool IsDesktopOrTaskbar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
            return true;

        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        string className = sb.ToString();

        return className == "Progman" ||
               className == "WorkerW" ||
               className == "Shell_TrayWnd" ||
               className == "Shell_SecondaryTrayWnd";
    }

    private static bool IsNormalUserWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
            return false;

        long exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            return false;

        return true;
    }

    private static void SetTaskbarAppearance(bool transparent)
    {
        IntPtr mainHwnd = FindWindow("Shell_TrayWnd", null);

        if (transparent)
        {
            // ACCENT_ENABLE_TRANSPARENTGRADIENT: 100% прозрачно
            ApplyAccentPolicy(mainHwnd, 2, 2, 0);

            IntPtr secHwnd = IntPtr.Zero;
            while ((secHwnd = FindWindowEx(IntPtr.Zero, secHwnd, "Shell_SecondaryTrayWnd", IntPtr.Zero)) != IntPtr.Zero)
            {
                ApplyAccentPolicy(secHwnd, 2, 2, 0);
            }
        }
        else
        {
            // Чтобы вернуть РОДНОЙ стиль Windows (без черной заливки),
            // отправляем панели сообщение WM_DWMCOMPOSITIONCHANGED
            // (так же, как делает TranslucentTB).
            // Это заставляет Проводник перерисовать панель родными темами Windows 10.
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
        if (hwnd == IntPtr.Zero)
            return;

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
            Attribute = 19, // WCA_ACCENT_POLICY
            Data = pData,
            SizeOfData = size
        };

        SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(pData);
    }

    private static void Cleanup()
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();

        if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
        if (_hookShowHide != IntPtr.Zero) UnhookWinEvent(_hookShowHide);
        if (_hookLocation != IntPtr.Zero) UnhookWinEvent(_hookLocation);
        if (_hookCloak != IntPtr.Zero) UnhookWinEvent(_hookCloak);

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        SetTaskbarAppearance(false);
    }
}
