using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClearTaskbar;

static class Program
{
    // ========================= Константы =========================

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;   // окно начало сворачиваться
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;     // окно восстановлено из свёрнутого
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B; // окно сменило позицию/размер
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_MAXIMIZE = 0x01000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_MINIMIZE = 0x20000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    private const uint DWMWA_CLOAKED = 14;
    private const int OBJID_WINDOW = 0;

    private const string TaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow"; // Пуск/поиск
    private const string AppFrameClass = "ApplicationFrameWindow";       // хост UWP-приложений

    /// <summary>Схлопывание серии событий в один проход. В простое таймер остановлен.</summary>
    private const int DebounceMs = 40;

    /// <summary>
    /// Хук LOCATIONCHANGE для процесса активного окна: ловит «Развернуть»/Win+Up/Down/привязки
    /// ТЕКУЩЕГО окна. Хотите абсолютный минимум пробуждений — выключите: тогда состояние
    /// обновится при следующей смене активного окна (как в исходной версии).
    /// </summary>
    private const bool TrackActiveWindowResize = true;

    private const string RUN_REG_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string APP_NAME = "ClearTaskbar";

    // ========================= Состояние =========================

    private static Mutex? _singleInstanceMutex;
    private static NotifyIcon? _trayIcon;
    private static BroadcastWindow? _broadcastWindow;

    private static readonly WinEventDelegate _winEventProc = OnWinEvent;
    private static readonly EnumWindowsProc _maximizedEnumProc = HasMaximizedCallback;
    private static readonly System.Windows.Forms.Timer _debounceTimer = new() { Interval = DebounceMs };

    private static IntPtr _hookForeground;  // смена активного окна
    private static IntPtr _hookMinimize;    // свернули/восстановили окно
    private static IntPtr _hookCloak;       // cloak UWP/оболочки
    private static IntPtr _hookLocation;   // LOCATIONCHANGE только процесса активного окна
    private static uint _locationHookPid;   // pid, на который сейчас смотрит _hookLocation

    private static bool? _currentTransparentState; // кэш: taskbar не трогаем без изменений
    private static IntPtr _lastTaskbarHwnd;         // переживает перезапуск explorer
    private static bool _foundMaximized;            // результат EnumWindows без замыканий

    // Переиспользуемый буфер имени класса: ноль аллокаций в горячем пути.
    private static readonly char[] _classBuffer = new char[64];

    // ========================= P/Invoke =========================

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter,
        string className, string? windowTitle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute,
        out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int Attribute;   // 19 = WCA_ACCENT_POLICY
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

    private static long GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    // ========================= Точка входа =========================

    [STAThread]
    static void Main()
    {
        _singleInstanceMutex = new Mutex(true, @"Local\ClearTaskbar.SingleInstance", out bool firstInstance);
        if (!firstInstance) return;

        ApplicationConfiguration.Initialize();

        SetupTrayIcon();
        _broadcastWindow = new BroadcastWindow(); // невидимое окно: TaskbarCreated / смена мониторов
        _debounceTimer.Tick += OnDebounceTick;

        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        _hookMinimize = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        _hookCloak = SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) UpdateForegroundLocationHook(fg);

        UpdateTaskbarState(); // начальное состояние — сразу, без таймера

        try
        {
            Application.Run(); // в простое поток спит в GetMessage: 0% CPU
        }
        finally
        {
            Cleanup();
        }
    }

    // ========================= События =========================

    private static void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero)
            return;

        switch (eventType)
        {
            case EVENT_SYSTEM_FOREGROUND:
                UpdateForegroundLocationHook(hwnd);
                ScheduleUpdate();
                break;

            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
                ScheduleUpdate();
                break;

            case EVENT_OBJECT_CLOAKED:
            case EVENT_OBJECT_UNCLOAKED:
                if (ClassEquals(hwnd, CoreWindowClass) || ClassEquals(hwnd, AppFrameClass))
                    ScheduleUpdate();
                break;

            case EVENT_OBJECT_LOCATIONCHANGE:
                ScheduleUpdate();
                break;
        }
    }

    private static void UpdateForegroundLocationHook(IntPtr hwnd)
    {
        if (!TrackActiveWindowResize) return;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _locationHookPid)
            return; // уже слушаем этот процесс

        if (_hookLocation != IntPtr.Zero)
        {
            UnhookWinEvent(_hookLocation);
            _hookLocation = IntPtr.Zero;
        }

        _locationHookPid = pid;

        if (pid == 0 || pid == (uint)Environment.ProcessId)
            return;

        _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
    }

    private static void ScheduleUpdate()
    {
        if (!_debounceTimer.Enabled)
            _debounceTimer.Start();
    }

    private static void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        UpdateTaskbarState();
    }

    // ========================= Логика состояния =========================

    private static void UpdateTaskbarState()
    {
        bool transparent = !HasAnyMaximizedWindow() && !IsStartMenuVisible();
        ApplyState(transparent);
    }

    private static void ApplyState(bool transparent)
    {
        IntPtr taskbar = FindWindow(TaskbarClass, null);

        if (_currentTransparentState == transparent && taskbar == _lastTaskbarHwnd)
            return;

        _currentTransparentState = transparent;
        _lastTaskbarHwnd = taskbar;
        SetTaskbarAppearance(transparent, taskbar);
    }

    private static void ForceReapply()
    {
        _currentTransparentState = null;
        _lastTaskbarHwnd = IntPtr.Zero;
    }

    private static bool HasAnyMaximizedWindow()
    {
        _foundMaximized = false;
        EnumWindows(_maximizedEnumProc, IntPtr.Zero);
        return _foundMaximized;
    }

    private static bool HasMaximizedCallback(IntPtr hWnd, IntPtr lParam)
    {
        long style = GetWindowLong(hWnd, GWL_STYLE);
        if ((style & WS_MAXIMIZE) == 0 || (style & WS_VISIBLE) == 0 || (style & WS_MINIMIZE) != 0)
            return true;

        if (IsCloaked(hWnd))
            return true;

        long exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            return true;

        int len = GetClassName(hWnd, _classBuffer, _classBuffer.Length);
        ReadOnlySpan<char> cls = _classBuffer.AsSpan(0, Math.Max(len, 0));
        if (cls.SequenceEqual("Progman") || cls.SequenceEqual("WorkerW") ||
            cls.SequenceEqual(TaskbarClass) || cls.SequenceEqual(SecondaryTaskbarClass) ||
            cls.SequenceEqual(CoreWindowClass))
            return true;

        if (!IsZoomed(hWnd))
            return true;

        _foundMaximized = true;
        return false;
    }

    private static bool IsStartMenuVisible()
    {
        IntPtr startHwnd = FindWindow(CoreWindowClass, "Поиск");
        if (startHwnd == IntPtr.Zero) startHwnd = FindWindow(CoreWindowClass, "Пуск");
        if (startHwnd == IntPtr.Zero) startHwnd = FindWindow(CoreWindowClass, "Start");
        if (startHwnd == IntPtr.Zero) startHwnd = FindWindow(CoreWindowClass, "Search");

        return startHwnd != IntPtr.Zero && IsWindowVisible(startHwnd) && !IsCloaked(startHwnd);
    }

    private static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool ClassEquals(IntPtr hWnd, string className)
    {
        int len = GetClassName(hWnd, _classBuffer, _classBuffer.Length);
        return _classBuffer.AsSpan(0, Math.Max(len, 0)).SequenceEqual(className);
    }

    // ========================= Внешний вид taskbar =========================

    private static void SetTaskbarAppearance(bool transparent, IntPtr mainHwnd)
    {
        if (transparent)
        {
            ApplyAccentPolicy(mainHwnd, 2, 2, 0);

            IntPtr sec = IntPtr.Zero;
            while ((sec = FindWindowEx(IntPtr.Zero, sec, SecondaryTaskbarClass, null)) != IntPtr.Zero)
                ApplyAccentPolicy(sec, 2, 2, 0);
        }
        else
        {
            if (mainHwnd != IntPtr.Zero)
                SendMessageTimeout(mainHwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 500, out _);

            IntPtr sec = IntPtr.Zero;
            while ((sec = FindWindowEx(IntPtr.Zero, sec, SecondaryTaskbarClass, null)) != IntPtr.Zero)
                SendMessageTimeout(sec, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 500, out _);
        }
    }

    private static void ApplyAccentPolicy(IntPtr hwnd, int accentState, int accentFlags, int color)
    {
        if (hwnd == IntPtr.Zero) return;

        ACCENT_POLICY policy = new()
        {
            AccentState = accentState,
            AccentFlags = accentFlags,
            GradientColor = color,
            AnimationId = 0
        };

        int size = Marshal.SizeOf(policy);
        IntPtr pData = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pData, false);

            WINCOMPATTRDATA data = new()
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                Data = pData,
                SizeOfData = size
            };

            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
        }
    }

    // ========================= Broadcast-окно =========================

    private sealed class BroadcastWindow : NativeWindow
    {
        private const int WM_DISPLAYCHANGE = 0x007E;
        private static readonly int TaskbarCreated =
            unchecked((int)RegisterWindowMessage("TaskbarCreated"));

        public BroadcastWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "ClearTaskbar_Broadcast",
                Style = unchecked((int)0x80000000),         // WS_POPUP
                ExStyle = 0x00000080 | 0x08000000          // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TaskbarCreated || m.Msg == WM_DISPLAYCHANGE)
            {
                ForceReapply();
                ScheduleUpdate();
            }
            base.WndProc(ref m);
        }
    }

    // ========================= Трей и автозапуск =========================

    private static void SetupTrayIcon()
    {
        var contextMenu = new ContextMenuStrip();

        var startupItem = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            CheckOnClick = true,
            Checked = IsRunAtStartup()
        };
        startupItem.CheckedChanged += (s, e) => SetRunAtStartup(startupItem.Checked);
        contextMenu.Items.Add(startupItem);

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Выход", null, (s, e) => Application.Exit()));

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
                key.SetValue(APP_NAME, $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue(APP_NAME, false);
        }
        catch { }
    }

    // ========================= Завершение =========================

    private static void Cleanup()
    {
        Unhook(ref _hookForeground);
        Unhook(ref _hookMinimize);
        Unhook(ref _hookCloak);
        Unhook(ref _hookLocation);

        _debounceTimer.Stop();
        _debounceTimer.Dispose();

        SetTaskbarAppearance(false, FindWindow(TaskbarClass, null));

        _broadcastWindow?.DestroyHandle();
        _broadcastWindow = null;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _singleInstanceMutex?.Dispose();
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        UnhookWinEvent(hook);
        hook = IntPtr.Zero;
    }
}
