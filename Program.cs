using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClearTaskbar;

static class Program
{
    // ========================= Константы =========================

    private const uint EVENT_SYSTEM_FOREGROUND     = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART  = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND    = 0x0017;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_CLOAKED        = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED      = 0x8018;

    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;

    private const int  GWL_STYLE   = -16;
    private const int  GWL_EXSTYLE = -20;
    private const long WS_MAXIMIZE       = 0x01000000L;
    private const long WS_VISIBLE        = 0x10000000L;
    private const long WS_MINIMIZE       = 0x20000000L;
    private const long WS_EX_TOOLWINDOW  = 0x00000080L;

    private const uint DWMWA_CLOAKED = 14;
    private const int  OBJID_WINDOW  = 0;
    private const int  SM_CMONITORS  = 80;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
    private const int ACCENT_POLICY_SIZE = 16; // 4 x int

    private const string TaskbarClass          = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const string CoreWindowClass       = "Windows.UI.Core.CoreWindow";

    /// <summary>Схлопывание серии событий в один пересчёт. В простое таймер остановлен.</summary>
    private const int DebounceMs = 40;

    private const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName   = "ClearTaskbar";

    // ========================= Состояние =========================

    private static Mutex?          _singleInstanceMutex;
    private static NotifyIcon?     _trayIcon;
    private static Icon?           _trayIconImage;
    private static BroadcastWindow? _broadcastWindow;

    private static readonly WinEventDelegate _winEventProc = OnWinEvent;
    private static readonly EnumWindowsProc  _enumProc     = EnumWindowCallback;
    private static readonly System.Windows.Forms.Timer _debounceTimer = new() { Interval = DebounceMs };

    private static IntPtr _hookForeground;
    private static IntPtr _hookMinimize;
    private static IntPtr _hookCloak;
    private static IntPtr _hookLocation;     // LOCATIONCHANGE только потока активного окна
    private static uint   _locationHookPid;
    private static uint   _locationHookTid;

    // Кэш активного окна для дешёвого предфильтра LOCATIONCHANGE.
    private static IntPtr _fgHwnd;
    private static bool   _fgMaximized;
    private static IntPtr _fgMonitor;

    // Результат одного прохода EnumWindows: мониторы, на которых панель должна быть непрозрачной.
    private static readonly List<IntPtr> _opaqueMonitors = new(4);
    private static int _monitorCount = 1;

    // Панели, которым уже применено состояние (двойная буферизация, ноль аллокаций).
    private readonly record struct TaskbarEntry(IntPtr Hwnd, bool Transparent);
    private static List<TaskbarEntry> _current  = new(4);
    private static List<TaskbarEntry> _previous = new(4);

    // Переиспользуемые буферы и unmanaged-память для accent policy.
    private static readonly char[] _classBuffer = new char[128];
    private static readonly char[] _pathBuffer  = new char[1024];
    private static readonly IntPtr _accentBuffer = Marshal.AllocHGlobal(ACCENT_POLICY_SIZE);

    // ========================= P/Invoke =========================

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attr, out int value, int cb);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, [Out] char[] buffer, ref int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint min, nint max);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int    Attribute;
        public IntPtr Data;
        public int    SizeOfData;
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
        _broadcastWindow = new BroadcastWindow();
        _debounceTimer.Tick += OnDebounceTick;

        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;

        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, flags);
        _hookMinimize = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0, flags);
        _hookCloak = SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED,
            IntPtr.Zero, _winEventProc, 0, 0, flags);

        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) TrackForegroundWindow(fg);

        UpdateTaskbarState(); // начальное состояние — сразу

        // Однократно после инициализации: JIT/WinForms-мусор уходит, working set сжимается.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

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
                TrackForegroundWindow(hwnd);
                ScheduleUpdate();
                break;

            case EVENT_OBJECT_LOCATIONCHANGE:
                OnForegroundLocationChanged(hwnd);
                break;

            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
                ScheduleUpdate();
                break;

            case EVENT_OBJECT_CLOAKED:
            case EVENT_OBJECT_UNCLOAKED:
                // Пуск/Поиск (CoreWindow) или развёрнутое окно, скрытое/показанное сменой виртуального стола.
                if ((GetWindowLong(hwnd, GWL_STYLE) & WS_MAXIMIZE) != 0 || ClassEquals(hwnd, CoreWindowClass))
                    ScheduleUpdate();
                break;
        }
    }

    /// <summary>Запоминает активное окно и перевешивает LOCATIONCHANGE-хук на его поток.</summary>
    private static void TrackForegroundWindow(IntPtr hwnd)
    {
        _fgHwnd      = hwnd;
        _fgMaximized = (GetWindowLong(hwnd, GWL_STYLE) & WS_MAXIMIZE) != 0;
        _fgMonitor   = _fgMaximized ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;

        uint tid = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _locationHookPid && tid == _locationHookTid)
            return;

        Unhook(ref _hookLocation);
        _locationHookPid = pid;
        _locationHookTid = tid;

        if (pid == 0 || tid == 0)
            return;

        _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventProc, pid, tid, WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Дешёвый предфильтр: пересчёт только если изменился факт «развёрнуто» или монитор.</summary>
    private static void OnForegroundLocationChanged(IntPtr hwnd)
    {
        if (hwnd != _fgHwnd)
            return;

        bool   maximized = (GetWindowLong(hwnd, GWL_STYLE) & WS_MAXIMIZE) != 0;
        IntPtr monitor   = maximized ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;

        if (maximized == _fgMaximized && monitor == _fgMonitor)
            return;

        _fgMaximized = maximized;
        _fgMonitor   = monitor;
        ScheduleUpdate();
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
        _monitorCount = Math.Max(1, GetSystemMetrics(SM_CMONITORS));
        _opaqueMonitors.Clear();
        EnumWindows(_enumProc, IntPtr.Zero);

        _current.Clear();

        IntPtr main = FindWindow(TaskbarClass, null);
        if (main != IntPtr.Zero)
            _current.Add(new TaskbarEntry(main, !IsMonitorOpaque(main)));

        IntPtr sec = IntPtr.Zero;
        while ((sec = FindWindowEx(IntPtr.Zero, sec, SecondaryTaskbarClass, null)) != IntPtr.Zero)
            _current.Add(new TaskbarEntry(sec, !IsMonitorOpaque(sec)));

        for (int i = 0; i < _current.Count; i++)
        {
            TaskbarEntry entry = _current[i];
            if (entry.Transparent)
                ApplyTransparent(entry.Hwnd);      // идемпотентно и дёшево → самовосстановление
            else if (!WasOpaque(entry.Hwnd))
                ApplyOpaque(entry.Hwnd);           // заметная операция → только при смене состояния
        }

        (_previous, _current) = (_current, _previous);
    }

    private static bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        long style = GetWindowLong(hWnd, GWL_STYLE);
        if ((style & WS_VISIBLE) == 0 || (style & WS_MINIMIZE) != 0)
            return true;

        if ((style & WS_MAXIMIZE) != 0)
        {
            if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
                return true;

            ReadOnlySpan<char> cls = GetClass(hWnd);
            if (cls.SequenceEqual("Progman") || cls.SequenceEqual("WorkerW") ||
                cls.SequenceEqual(TaskbarClass) || cls.SequenceEqual(SecondaryTaskbarClass))
                return true;

            if (IsCloaked(hWnd))
                return true;

            AddOpaqueMonitor(hWnd);
        }
        else if (GetClass(hWnd).SequenceEqual(CoreWindowClass))
        {
            // Пуск / Поиск: видимое, не скрытое DWM окно нужного системного процесса.
            if (!IsCloaked(hWnd) && IsStartOrSearchProcess(hWnd))
                AddOpaqueMonitor(hWnd);
        }

        // Все мониторы уже «закрыты» — дальше перечислять нечего.
        return _opaqueMonitors.Count < _monitorCount;
    }

    private static void AddOpaqueMonitor(IntPtr hWnd)
    {
        IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero && !_opaqueMonitors.Contains(monitor))
            _opaqueMonitors.Add(monitor);
    }

    private static bool IsMonitorOpaque(IntPtr taskbar)
        => _opaqueMonitors.Contains(MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST));

    private static bool WasOpaque(IntPtr hwnd)
    {
        for (int i = 0; i < _previous.Count; i++)
            if (_previous[i].Hwnd == hwnd)
                return !_previous[i].Transparent;
        return false;
    }

    private static void ResetAppliedCache() => _previous.Clear();

    private static bool IsStartOrSearchProcess(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return false;

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return false;

        try
        {
            int size = _pathBuffer.Length;
            if (!QueryFullProcessImageName(hProcess, 0, _pathBuffer, ref size))
                return false;

            ReadOnlySpan<char> path = _pathBuffer.AsSpan(0, size);
            int slash = path.LastIndexOf('\\');
            ReadOnlySpan<char> name = slash >= 0 ? path[(slash + 1)..] : path;

            return name.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("SearchHost.exe",              StringComparison.OrdinalIgnoreCase)
                || name.Equals("SearchApp.exe",               StringComparison.OrdinalIgnoreCase)
                || name.Equals("SearchUI.exe",                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static ReadOnlySpan<char> GetClass(IntPtr hWnd)
    {
        int len = GetClassName(hWnd, _classBuffer, _classBuffer.Length);
        return _classBuffer.AsSpan(0, Math.Max(len, 0));
    }

    private static bool ClassEquals(IntPtr hWnd, string className) => GetClass(hWnd).SequenceEqual(className);

    // ========================= Внешний вид taskbar =========================

    private static void ApplyTransparent(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // ACCENT_POLICY { AccentState, AccentFlags, GradientColor, AnimationId }
        Marshal.WriteInt32(_accentBuffer, 0,  ACCENT_ENABLE_TRANSPARENTGRADIENT);
        Marshal.WriteInt32(_accentBuffer, 4,  2);
        Marshal.WriteInt32(_accentBuffer, 8,  0);
        Marshal.WriteInt32(_accentBuffer, 12, 0);

        WINCOMPATTRDATA data = new()
        {
            Attribute  = WCA_ACCENT_POLICY,
            Data       = _accentBuffer,
            SizeOfData = ACCENT_POLICY_SIZE
        };
        SetWindowCompositionAttribute(hwnd, ref data);
    }

    private static void ApplyOpaque(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        // Заставляет explorer заново применить собственный (штатный) accent. Асинхронно, без блокировки.
        SendNotifyMessage(hwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);
    }

    private static void RestoreAllTaskbars()
    {
        ApplyOpaque(FindWindow(TaskbarClass, null));

        IntPtr sec = IntPtr.Zero;
        while ((sec = FindWindowEx(IntPtr.Zero, sec, SecondaryTaskbarClass, null)) != IntPtr.Zero)
            ApplyOpaque(sec);
    }

    // ========================= Broadcast-окно =========================

    private sealed class BroadcastWindow : NativeWindow
    {
        private const int WM_SETTINGCHANGE                = 0x001A;
        private const int WM_DISPLAYCHANGE                = 0x007E;
        private const int WM_POWERBROADCAST               = 0x0218;
        private const int WM_THEMECHANGED                 = 0x031A;
        private const int WM_DWMCOMPOSITIONCHANGED_INT    = 0x031E;
        private const int WM_DWMCOLORIZATIONCOLORCHANGED  = 0x0320;
        private const int PBT_APMRESUMESUSPEND            = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC          = 0x0012;

        private static readonly int TaskbarCreated = unchecked((int)RegisterWindowMessage("TaskbarCreated"));

        public BroadcastWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "ClearTaskbar_Broadcast",
                Style   = unchecked((int)0x80000000),      // WS_POPUP (top-level → получает broadcast)
                ExStyle = 0x00000080 | 0x08000000          // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TaskbarCreated || m.Msg == WM_DISPLAYCHANGE)
            {
                // Новые hwnd панелей / новые мониторы — кэш применённых состояний недействителен.
                ResetAppliedCache();
                ScheduleUpdate();
            }
            else if (m.Msg == WM_SETTINGCHANGE || m.Msg == WM_THEMECHANGED ||
                     m.Msg == WM_DWMCOMPOSITIONCHANGED_INT || m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED)
            {
                ScheduleUpdate(); // explorer мог сбросить accent — прозрачность переприменится
            }
            else if (m.Msg == WM_POWERBROADCAST)
            {
                long ev = m.WParam.ToInt64();
                if (ev == PBT_APMRESUMEAUTOMATIC || ev == PBT_APMRESUMESUSPEND)
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
        startupItem.CheckedChanged += (_, _) => SetRunAtStartup(startupItem.Checked);
        contextMenu.Items.Add(startupItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => Application.Exit()));

        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) _trayIconImage = Icon.ExtractAssociatedIcon(exe);
        }
        catch { /* fallback ниже */ }

        _trayIcon = new NotifyIcon
        {
            Icon = _trayIconImage ?? SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Text = "Clear Taskbar",
            Visible = true
        };
    }

    private static bool IsRunAtStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    private static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegKey, true);
            if (key == null) return;

            if (enable)
                key.SetValue(AppName, $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"");
            else
                key.DeleteValue(AppName, false);
        }
        catch { /* нет прав — молча игнорируем */ }
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

        RestoreAllTaskbars();

        _broadcastWindow?.DestroyHandle();
        _broadcastWindow = null;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayIconImage?.Dispose();

        Marshal.FreeHGlobal(_accentBuffer);

        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
        }
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        UnhookWinEvent(hook);
        hook = IntPtr.Zero;
    }
}
