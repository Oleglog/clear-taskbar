using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClearTaskbar;

public static unsafe class Program
{
    // ========================= Win32 Константы =========================

    private const uint EVENT_SYSTEM_FOREGROUND     = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZEEND    = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART  = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND    = 0x0017;
    private const uint EVENT_OBJECT_STATECHANGE    = 0x800A;
    private const uint EVENT_OBJECT_CLOAKED        = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED      = 0x8018;

    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const int  GWL_STYLE        = -16;
    private const int  GWL_EXSTYLE      = -20;
    private const long WS_MAXIMIZE      = 0x01000000L;
    private const long WS_VISIBLE       = 0x10000000L;
    private const long WS_MINIMIZE      = 0x20000000L;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private const uint DWMWA_CLOAKED    = 14;
    private const int  OBJID_WINDOW     = 0;
    private const int  SM_CMONITORS     = 80;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
    private const int ACCENT_POLICY_SIZE = 16;

    private const uint WM_USER            = 0x0400;
    private const uint WM_TRAYICON        = WM_USER + 1;
    private const uint WM_TIMER           = 0x0113;
    private const uint WM_COMMAND         = 0x0111;
    private const uint WM_DESTROY         = 0x0002;
    private const uint WM_SETTINGCHANGE   = 0x001A;
    private const uint WM_DISPLAYCHANGE   = 0x007E;
    private const uint WM_POWERBROADCAST  = 0x0218;
    private const uint WM_THEMECHANGED    = 0x031A;
    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    private const uint WM_RBUTTONUP       = 0x0205;

    private const nuint TIMER_DEBOUNCE_ID = 1;
    private const uint  DEBOUNCE_MS       = 25;

    private const uint IDM_STARTUP = 2001;
    private const uint IDM_EXIT    = 2002;

    private const uint MF_STRING    = 0x00000000;
    private const uint MF_CHECKED   = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTALIGN  = 0x0008;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON    = 0x00000002;
    private const uint NIF_TIP     = 0x00000004;
    private const uint NIM_ADD     = 0x00000000;
    private const uint NIM_DELETE  = 0x00000002;

    private const string TaskbarClass          = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const string CoreWindowClass       = "Windows.UI.Core.CoreWindow";
    private const string WindowClassName       = "ClearTaskbar_Msg_Class";
    private const string RunRegKey             = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName               = "ClearTaskbar";

    // ========================= Состояние =========================

    private static IntPtr _hWndMsg;
    private static IntPtr _hMutex;
    private static uint   _msgTaskbarCreated;

    private static IntPtr _hookForeground;
    private static IntPtr _hookMoveSize;
    private static IntPtr _hookMinimize;
    private static IntPtr _hookState;
    private static IntPtr _hookCloak;

    private static readonly IntPtr _accentBuffer = Marshal.AllocHGlobal(ACCENT_POLICY_SIZE);

    // Мониторы, на которых есть развернутые окна в текущей оценке
    private static readonly IntPtr[] _opaqueMonitors = new IntPtr[16];
    private static int _opaqueMonitorsCount = 0;
    private static int _systemMonitorCount  = 1;

    // Кэш панелей задач: чтобы повторно не долбить DWM при том же состоянии
    private struct TaskbarCache
    {
        public IntPtr Hwnd;
        public bool   IsTransparent;
    }
    private static readonly TaskbarCache[] _taskbars = new TaskbarCache[8];
    private static int _taskbarsCount = 0;

    // Буферы для быстрого GetClassName / QueryFullProcessImageName без аллокаций
    private static readonly char[] _classBuffer = new char[128];
    private static readonly char[] _pathBuffer  = new char[1024];

    // Кэш PID процессов меню пуск / поиска
    private struct PidCacheEntry
    {
        public uint Pid;
        public bool IsTarget;
    }
    private static readonly PidCacheEntry[] _pidCache = new PidCacheEntry[8];
    private static int _pidCacheCount = 0;

    // ========================= P/Invoke =========================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int    cbSize;
        public int    style;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char*  lpszMenuName;
        public char*  lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int    cbSize;
        public IntPtr hWnd;
        public uint   uID;
        public uint   uFlags;
        public uint   uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint   dwState;
        public uint   dwStateMask;
        public fixed char szInfo[256];
        public uint   uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint   dwInfoFlags;
        public Guid   guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public POINT  pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int    Attribute;
        public IntPtr Data;
        public int    SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, void* lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(MSG* lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(in MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, void* lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void> lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SendNotifyMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attr, void* pvAttribute, int cbAttribute);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, void* lpTPM);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateMutexW(void* lpMutexAttributes, bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll")]
    private static extern uint GetLastError();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint flags, char* lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint min, nint max);

    private static nint GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    // ========================= Точка входа =========================

    [STAThread]
    public static int Main()
    {
        // 1. Атомарная проверка единственной копии программы
        _hMutex = CreateMutexW(null, true, @"Local\ClearTaskbar.SingleInstance.Mutex");
        if (_hMutex == IntPtr.Zero || GetLastError() == 183 /* ERROR_ALREADY_EXISTS */)
            return 0;

        // 2. Инициализация AccentPolicy структуры (1 раз в unmanaged памяти)
        int* pPolicy = (int*)_accentBuffer.ToPointer();
        pPolicy[0] = ACCENT_ENABLE_TRANSPARENTGRADIENT; // AccentState
        pPolicy[1] = 2;                                 // AccentFlags
        pPolicy[2] = 0;                                 // GradientColor
        pPolicy[3] = 0;                                 // AnimationId

        IntPtr hInstance = GetModuleHandleW(null);
        _msgTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        // 3. Регистрация собственного легковесного Win32 класса окон
        fixed (char* pClassName = WindowClassName)
        {
            WNDCLASSEXW wx = new()
            {
                cbSize        = sizeof(WNDCLASSEXW),
                style         = 0,
                lpfnWndProc   = &WndProcCallback,
                cbClsExtra    = 0,
                cbWndExtra    = 0,
                hInstance     = hInstance,
                hIcon         = IntPtr.Zero,
                hCursor       = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName  = null,
                lpszClassName = pClassName,
                hIconSm       = IntPtr.Zero
            };
            RegisterClassExW(wx);
        }

        // 4. Создание невидимого top-level окна (получает broadcast системные сообщения)
        _hWndMsg = CreateWindowExW(
            WS_EX_TOOLWINDOW,
            WindowClassName,
            "ClearTaskbar_Host",
            0x80000000 /* WS_POPUP */,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, null);

        if (_hWndMsg == IntPtr.Zero)
            return 1;

        // 5. Установка иконки в системный трей
        AddTrayIcon(_hWndMsg);

        // 6. Установка глобальных низкоуровневых WinEvent-хуков с фильтрацией на уровне ОС
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;

        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, &WinEventCallback, 0, 0, flags);

        _hookMoveSize = SetWinEventHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, &WinEventCallback, 0, 0, flags);

        _hookMinimize = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, &WinEventCallback, 0, 0, flags);

        _hookState = SetWinEventHook(EVENT_OBJECT_STATECHANGE, EVENT_OBJECT_STATECHANGE,
            IntPtr.Zero, &WinEventCallback, 0, 0, flags);

        _hookCloak = SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED,
            IntPtr.Zero, &WinEventCallback, 0, 0, flags);

        // Применяем состояние прямо на старте
        UpdateTaskbarState();

        // Сжимаем рабочий набор (Working Set) после полной загрузки
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

        // 7. Чистый Win32 цикл сообщений (0% CPU в простое)
        MSG msg;
        while (GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }

        // 8. Очистка ресурсов при завершении
        Cleanup();
        return 0;
    }

    // ========================= Обработчик событий ОС =========================

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Мгновенный отсев: нас интересуют ТОЛЬКО окна, а не кнопки, каретки или элементы внутри них
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero)
            return;

        // Схлопываем пачки событий таймером в 25 мс
        RequestDebouncedUpdate();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RequestDebouncedUpdate()
    {
        if (_hWndMsg != IntPtr.Zero)
            SetTimer(_hWndMsg, TIMER_DEBOUNCE_ID, DEBOUNCE_MS, null);
    }

    // ========================= Логика переключения =========================

    private static void UpdateTaskbarState()
    {
        _systemMonitorCount = Math.Max(1, GetSystemMetrics(SM_CMONITORS));
        _opaqueMonitorsCount = 0;

        // Проверяем Пуск / Поиск напрямую (в обход EnumWindows, так как они могут лежать на отдельных рабочих столах)
        CheckStartOrSearchDirect();

        // Перечисляем окна: callback вернет 0 и прервет обход, как только все мониторы будут покрыты
        EnumWindows(&EnumWindowCallback, IntPtr.Zero);

        // Проверяем главную панель
        IntPtr mainTb = FindWindowW(TaskbarClass, null);
        if (mainTb != IntPtr.Zero)
            ApplyTaskbarState(mainTb, !IsMonitorOpaque(mainTb));

        // Проверяем второстепенные панели (мультимониторные конфигурации)
        IntPtr secTb = IntPtr.Zero;
        while ((secTb = FindWindowExW(IntPtr.Zero, secTb, SecondaryTaskbarClass, null)) != IntPtr.Zero)
        {
            ApplyTaskbarState(secTb, !IsMonitorOpaque(secTb));
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        long style = GetWindowLong(hWnd, GWL_STYLE);

        // Окно невидимо или свернуто -> пропускаем сразу
        if ((style & WS_VISIBLE) == 0 || (style & WS_MINIMIZE) != 0)
            return 1;

        if ((style & WS_MAXIMIZE) != 0)
        {
            if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
                return 1;

            if (IsClassIgnored(hWnd))
                return 1;

            if (IsWindowCloaked(hWnd))
                return 1;

            AddOpaqueMonitorFromHwnd(hWnd);
        }
        else if (IsClassCoreWindow(hWnd))
        {
            // Проверка Пуска / Поиска Windows 10/11
            if (!IsWindowCloaked(hWnd) && IsStartOrSearchProcess(hWnd))
            {
                AddOpaqueMonitorFromHwnd(hWnd);
            }
        }

        // Если все физические мониторы уже перекрыты максимизированными окнами — прерываем перечисление!
        return _opaqueMonitorsCount < _systemMonitorCount ? 1 : 0;
    }

    private static void CheckStartOrSearchDirect()
    {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowExW(IntPtr.Zero, h, CoreWindowClass, null)) != IntPtr.Zero)
        {
            if (IsWindowVisible(h) && !IsWindowCloaked(h) && IsStartOrSearchProcess(h))
            {
                AddOpaqueMonitorFromHwnd(h);
            }
        }
    }

    private static void AddOpaqueMonitorFromHwnd(IntPtr hWnd)
    {
        IntPtr hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return;

        for (int i = 0; i < _opaqueMonitorsCount; i++)
        {
            if (_opaqueMonitors[i] == hMonitor)
                return;
        }

        if (_opaqueMonitorsCount < _opaqueMonitors.Length)
            _opaqueMonitors[_opaqueMonitorsCount++] = hMonitor;
    }

    private static bool IsMonitorOpaque(IntPtr hTaskbar)
    {
        IntPtr hMonitor = MonitorFromWindow(hTaskbar, MONITOR_DEFAULTTONEAREST);
        for (int i = 0; i < _opaqueMonitorsCount; i++)
        {
            if (_opaqueMonitors[i] == hMonitor)
                return true;
        }
        return false;
    }

    private static void ApplyTaskbarState(IntPtr hTaskbar, bool makeTransparent)
    {
        int index = -1;
        for (int i = 0; i < _taskbarsCount; i++)
        {
            if (_taskbars[i].Hwnd == hTaskbar)
            {
                index = i;
                break;
            }
        }

        // КЭШ: Если панель уже в целевом состоянии, НИКАКИХ вызовов в DWM/Explorer не делаем!
        if (index != -1 && _taskbars[index].IsTransparent == makeTransparent)
            return;

        if (index == -1 && _taskbarsCount < _taskbars.Length)
        {
            index = _taskbarsCount++;
            _taskbars[index].Hwnd = hTaskbar;
        }

        if (index != -1)
            _taskbars[index].IsTransparent = makeTransparent;

        if (makeTransparent)
        {
            WINCOMPATTRDATA data = new()
            {
                Attribute  = WCA_ACCENT_POLICY,
                Data       = _accentBuffer,
                SizeOfData = ACCENT_POLICY_SIZE
            };
            SetWindowCompositionAttribute(hTaskbar, ref data);
        }
        else
        {
            // Асинхронно уведомляем Explorer восстановить родной вид без блокировки потока
            SendNotifyMessageW(hTaskbar, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);
        }
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        int cloaked = 0;
        return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static bool IsClassIgnored(IntPtr hWnd)
    {
        fixed (char* pBuf = _classBuffer)
        {
            int len = GetClassNameW(hWnd, pBuf, _classBuffer.Length);
            if (len <= 0) return false;
            ReadOnlySpan<char> cls = new(pBuf, len);

            return cls.SequenceEqual("Progman") ||
                   cls.SequenceEqual("WorkerW") ||
                   cls.SequenceEqual(TaskbarClass) ||
                   cls.SequenceEqual(SecondaryTaskbarClass);
        }
    }

    private static bool IsClassCoreWindow(IntPtr hWnd)
    {
        fixed (char* pBuf = _classBuffer)
        {
            int len = GetClassNameW(hWnd, pBuf, _classBuffer.Length);
            return len > 0 && new ReadOnlySpan<char>(pBuf, len).SequenceEqual(CoreWindowClass);
        }
    }

    private static bool IsStartOrSearchProcess(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return false;

        // Поиск в кэше PID
        for (int i = 0; i < _pidCacheCount; i++)
        {
            if (_pidCache[i].Pid == pid)
                return _pidCache[i].IsTarget;
        }

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return false;

        bool isTarget = false;
        try
        {
            fixed (char* pPath = _pathBuffer)
            {
                int size = _pathBuffer.Length;
                if (QueryFullProcessImageNameW(hProcess, 0, pPath, ref size))
                {
                    ReadOnlySpan<char> fullPath = new(pPath, size);
                    int slash = fullPath.LastIndexOf('\\');
                    ReadOnlySpan<char> exeName = slash >= 0 ? fullPath[(slash + 1)..] : fullPath;

                    isTarget = exeName.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase) ||
                               exeName.Equals("SearchHost.exe",              StringComparison.OrdinalIgnoreCase) ||
                               exeName.Equals("SearchApp.exe",               StringComparison.OrdinalIgnoreCase) ||
                               exeName.Equals("SearchUI.exe",                StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        if (_pidCacheCount < _pidCache.Length)
        {
            _pidCache[_pidCacheCount++] = new PidCacheEntry { Pid = pid, IsTarget = isTarget };
        }
        else
        {
            _pidCache[0] = new PidCacheEntry { Pid = pid, IsTarget = isTarget };
        }

        return isTarget;
    }

    // ========================= Оконная процедура =========================

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProcCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == _msgTaskbarCreated)
        {
            AddTrayIcon(hWnd);
            _taskbarsCount = 0; // Сброс кэша
            RequestDebouncedUpdate();
            return IntPtr.Zero;
        }

        switch (uMsg)
        {
            case WM_TIMER:
                if (wParam == (IntPtr)TIMER_DEBOUNCE_ID)
                {
                    KillTimer(hWnd, TIMER_DEBOUNCE_ID);
                    UpdateTaskbarState();
                }
                return IntPtr.Zero;

            case WM_TRAYICON:
                if ((uint)lParam == WM_RBUTTONUP)
                    ShowContextMenu(hWnd);
                return IntPtr.Zero;

            case WM_COMMAND:
                uint cmdId = (uint)(wParam.ToInt64() & 0xFFFF);
                if (cmdId == IDM_STARTUP)
                {
                    SetRunAtStartup(!IsRunAtStartup());
                }
                else if (cmdId == IDM_EXIT)
                {
                    DestroyWindow(hWnd);
                }
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
            case WM_THEMECHANGED:
            case WM_DWMCOMPOSITIONCHANGED:
            case WM_DWMCOLORIZATIONCOLORCHANGED:
            case WM_SETTINGCHANGE:
                _taskbarsCount = 0; // Инвалидируем кэш: explorer мог сбросить accent
                RequestDebouncedUpdate();
                return IntPtr.Zero;

            case WM_POWERBROADCAST:
                RequestDebouncedUpdate();
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, uMsg, wParam, lParam);
    }

    // ========================= Трей и Контекстное меню =========================

    private static void AddTrayIcon(IntPtr hWnd)
    {
        NOTIFYICONDATAW nid = new()
        {
            cbSize           = sizeof(NOTIFYICONDATAW),
            hWnd             = hWnd,
            uID              = 1,
            uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon            = LoadIconW(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */)
        };

        string tip = "Clear Taskbar";
        int i = 0;
        for (; i < tip.Length && i < 127; i++) nid.szTip[i] = tip[i];
        nid.szTip[i] = '\0';

        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    private static void RemoveTrayIcon(IntPtr hWnd)
    {
        NOTIFYICONDATAW nid = new()
        {
            cbSize = sizeof(NOTIFYICONDATAW),
            hWnd   = hWnd,
            uID    = 1
        };
        Shell_NotifyIconW(NIM_DELETE, ref nid);
    }

    private static void ShowContextMenu(IntPtr hWnd)
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        bool isStartup = IsRunAtStartup();
        AppendMenuW(hMenu, MF_STRING | (isStartup ? MF_CHECKED : MF_UNCHECKED), IDM_STARTUP, "Запускать вместе с Windows");
        AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
        AppendMenuW(hMenu, MF_STRING, IDM_EXIT, "Выход");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(hWnd);
        TrackPopupMenuEx(hMenu, TPM_RIGHTALIGN | TPM_BOTTOMALIGN, pt.X, pt.Y, hWnd, null);
        DestroyMenu(hMenu);
    }

    // ========================= Автозагрузка =========================

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
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                    key.SetValue(AppName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }

    // ========================= Завершение =========================

    private static void Cleanup()
    {
        Unhook(ref _hookForeground);
        Unhook(ref _hookMoveSize);
        Unhook(ref _hookMinimize);
        Unhook(ref _hookState);
        Unhook(ref _hookCloak);

        if (_hWndMsg != IntPtr.Zero)
        {
            RemoveTrayIcon(_hWndMsg);
            KillTimer(_hWndMsg, TIMER_DEBOUNCE_ID);
        }

        // Возвращаем панелям непрозрачный вид по умолчанию
        IntPtr mainTb = FindWindowW(TaskbarClass, null);
        if (mainTb != IntPtr.Zero)
            SendNotifyMessageW(mainTb, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);

        IntPtr secTb = IntPtr.Zero;
        while ((secTb = FindWindowExW(IntPtr.Zero, secTb, SecondaryTaskbarClass, null)) != IntPtr.Zero)
            SendNotifyMessageW(secTb, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero);

        Marshal.FreeHGlobal(_accentBuffer);

        if (_hMutex != IntPtr.Zero)
            CloseHandle(_hMutex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Unhook(ref IntPtr hook)
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }
    }
}
