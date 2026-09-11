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
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_MINIMIZE = 0x20000000L;
    private const long WS_MAXIMIZE = 0x01000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    private const uint DWMWA_CLOAKED = 14;

    /// <summary>idObject == 0 (OBJID_WINDOW) — событие относится к окну целиком, а не к его элементам.</summary>
    private const int OBJID_WINDOW = 0;

    private const string TaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// Пауза «схлопывания» серии событий (Alt+Tab, открытие Пуска и т.п.).
    /// Таймер одноразовый: пока событий нет — он остановлен, пробуждений потока ноль.
    /// </summary>
    private const int DebounceMs = 40;

    private const string RUN_REG_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string APP_NAME = "ClearTaskbar";

    // ========================= Состояние =========================

    private static Mutex? _singleInstanceMutex;
    private static NotifyIcon? _trayIcon;

    // Делегаты создаются один раз — на события не тратится ни байта.
    private static readonly WinEventDelegate _winEventProc = OnWinEvent;
    private static readonly EnumWindowsProc _maximizedEnumProc = HasMaximizedCallback;

    // WinForms-таймер: WM_TIMER, без дополнительных потоков и без смены системного разрешения таймера.
    private static readonly System.Windows.Forms.Timer _debounceTimer = new() { Interval = DebounceMs };

    private static IntPtr _hookForeground;
    private static IntPtr _hookLocation;
    private static IntPtr _hookCloak;

    private static bool? _currentTransparentState; // кэш: taskbar не трогаем, пока ничего не изменилось
    private static IntPtr _lastTaskbarHwnd;        // переживает перезапуск explorer
    private static bool _foundMaximized;           // результат EnumWindows без замыканий

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

    // Unicode-варианты: без ANSI-конвертаций, корректно для нелатинских заголовков («Поиск»).
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute,
        out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
        // Второй экземпляр лишь дублировал бы работу и «спорил» бы с первым за вид taskbar.
        _singleInstanceMutex = new Mutex(true, @"Local\ClearTaskbar.SingleInstance", out bool firstInstance);
        if (!firstInstance) return;

        ApplicationConfiguration.Initialize();

        SetupTrayIcon();

        _debounceTimer.Tick += OnDebounceTick;

        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        _hookCloak = SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        UpdateTaskbarState(); // начальное состояние — сразу, без таймера

        try
        {
            // Чистый цикл сообщений: без окон и постоянно работающих таймеров.
            // Поток спит в GetMessage → в простое 0% CPU и почти нет context switch'ей.
            Application.Run();
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
        // Только события самого окна, не его дочерних объектов/скроллбаров/каретки.
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero)
            return;

        // CLOAKED/UNCLOAKED сыплются почти от всех UWP-окон системы (Store-приложения,
        // тосты, поиск, виджеты). Нам важны только Пуск/поиск — дешёвая проверка класса
        // отсекает подавляющее большинство событий ещё до EnumWindows и вызовов DWM.
        if ((eventType == EVENT_OBJECT_CLOAKED || eventType == EVENT_OBJECT_UNCLOAKED)
            && !ClassEquals(hwnd, CoreWindowClass))
        {
            return;
        }

        // Серия событий схлопывается в один проход: если таймер уже тикает — ничего не делаем.
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

        // Выходим, если не изменилось ни состояние, ни сама панель задач
        // (второе покрывает перезапуск explorer, чтобы состояние не «залипало»).
        if (_currentTransparentState == transparent && taskbar == _lastTaskbarHwnd)
            return;

        _currentTransparentState = transparent;
        _lastTaskbarHwnd = taskbar;
        SetTaskbarAppearance(transparent, taskbar);
    }

    private static bool HasAnyMaximizedWindow()
    {
        _foundMaximized = false;
        EnumWindows(_maximizedEnumProc, IntPtr.Zero); // делегат закэширован — без аллокаций
        return _foundMaximized;
    }

    private static bool HasMaximizedCallback(IntPtr hWnd, IntPtr lParam)
    {
        // ПОРЯДОК ПРОВЕРОК — главная оптимизация:
        // 1) один дешёвый GetWindowLong(GWL_STYLE) заменяет сразу IsWindowVisible +
        //    IsIconic + IsZoomed (это те же биты стиля) — 1 native-вызов вместо 3;
        long style = GetWindowLong(hWnd, GWL_STYLE);
        if ((style & WS_MAXIMIZE) == 0 || (style & WS_VISIBLE) == 0 || (style & WS_MINIMIZE) != 0)
            return true;

        // 2) дорогой межпроцессный запрос к DWM — только для максимизированных окон
        //    (обычно 0–2 окна, а не все видимые окна системы);
        if (IsCloaked(hWnd))
            return true;

        long exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            return true;

        // 3) GetClassName не посылает окну сообщений (читает кэш win32k) — дёшево;
        //    буфер переиспользуется, строки не создаются (сравнение через Span).
        int len = GetClassName(hWnd, _classBuffer, _classBuffer.Length);
        ReadOnlySpan<char> cls = _classBuffer.AsSpan(0, len);
        if (cls.SequenceEqual("Progman") || cls.SequenceEqual("WorkerW") ||
            cls.SequenceEqual(TaskbarClass) || cls.SequenceEqual(SecondaryTaskbarClass) ||
            cls.SequenceEqual(CoreWindowClass))
            return true;

        _foundMaximized = true;
        return false; // ранний выход из EnumWindows
    }

    private static bool IsStartMenuVisible()
    {
        // FindWindow — быстрый поиск по внутренним таблицам; вызывается редко, по событию.
        IntPtr startHwnd = FindWindow(CoreWindowClass, "Поиск");
        if (startHwnd == IntPtr.Zero) startHwnd = FindWindow(CoreWindowClass, "Start");
        if (startHwnd == IntPtr.Zero) startHwnd = FindWindow(CoreWindowClass, "Search");

        return startHwnd != IntPtr.Zero && IsWindowVisible(startHwnd) && !IsCloaked(startHwnd);
    }

    private static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool ClassEquals(IntPtr hWnd, string className)
    {
        int len = GetClassName(hWnd, _classBuffer, _classBuffer.Length);
        return _classBuffer.AsSpan(0, len).SequenceEqual(className);
    }

    // ========================= Внешний вид taskbar =========================

    private static void SetTaskbarAppearance(bool transparent, IntPtr mainHwnd)
    {
        if (transparent)
        {
            ApplyAccentPolicy(mainHwnd, 2, 2, 0);

            IntPtr secHwnd = IntPtr.Zero;
            while ((secHwnd = FindWindowEx(IntPtr.Zero, secHwnd, SecondaryTaskbarClass, null)) != IntPtr.Zero)
                ApplyAccentPolicy(secHwnd, 2, 2, 0);
        }
        else
        {
            if (mainHwnd != IntPtr.Zero)
                SendMessage(mainHwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);

            IntPtr secHwnd = IntPtr.Zero;
            while ((secHwnd = FindWindowEx(IntPtr.Zero, secHwnd, SecondaryTaskbarClass, null)) != IntPtr.Zero)
                SendMessage(secHwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);
        }
    }

    private static void ApplyAccentPolicy(IntPtr hwnd, int accentState, int accentFlags, int color)
    {
        if (hwnd == IntPtr.Zero) return;

        // Редкий путь (только при смене состояния) — аллокация здесь не критична.
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
        if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
        if (_hookLocation != IntPtr.Zero) UnhookWinEvent(_hookLocation);
        if (_hookCloak != IntPtr.Zero) UnhookWinEvent(_hookCloak);
        _hookForeground = _hookLocation = _hookCloak = IntPtr.Zero;

        _debounceTimer.Stop();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        // Вернуть панели задач стандартный вид.
        SetTaskbarAppearance(false, FindWindow(TaskbarClass, null));

        _singleInstanceMutex?.Dispose();
    }
}
