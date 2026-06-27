using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tomur.Config;

namespace Tomur.Cli;

internal sealed class TrayApplication : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WmApp + 17;
    private const int OpenWorkspaceCommand = 1001;
    private const int OpenRuntimeStatusCommand = 1002;
    private const int ExitCommand = 1003;

    private readonly string serviceUrl;
    private readonly Action stopApplication;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly NativeMethods.WindowProcedure windowProcedure;
    private readonly Thread thread;

    private volatile bool started;
    private volatile bool disposed;
    private Exception? startupException;
    private IntPtr windowHandle;
    private IntPtr iconHandle;

    private TrayApplication(string serviceUrl, Action stopApplication)
    {
        this.serviceUrl = serviceUrl.TrimEnd('/');
        this.stopApplication = stopApplication;
        windowProcedure = WindowProcedure;
        thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "Tomur tray"
        };
        thread.SetApartmentState(ApartmentState.STA);
    }

    public static TrayApplication? TryStart(string serviceUrl, Action stopApplication)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var tray = new TrayApplication(serviceUrl, stopApplication);
        tray.thread.Start();
        tray.ready.Wait();

        if (tray.startupException is not null)
        {
            Console.Error.WriteLine($"Windows tray icon could not be initialized: {tray.startupException.Message}");
            tray.Dispose();
            return null;
        }

        return tray.started ? tray : null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (windowHandle != IntPtr.Zero)
        {
            _ = NativeMethods.PostMessage(windowHandle, NativeMethods.WmClose, UIntPtr.Zero, IntPtr.Zero);
        }

        if (thread.IsAlive && Thread.CurrentThread.ManagedThreadId != thread.ManagedThreadId)
        {
            _ = thread.Join(TimeSpan.FromSeconds(2));
        }

        ready.Dispose();
    }

    private void RunMessageLoop()
    {
        try
        {
            windowHandle = CreateHiddenWindow();
            iconHandle = NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IdiApplication);
            AddIcon();
            started = true;
            ready.Set();

            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = NativeMethods.TranslateMessage(ref message);
                _ = NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            startupException = exception;
            ready.Set();
        }
        finally
        {
            RemoveIcon();
            if (windowHandle != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyWindow(windowHandle);
                windowHandle = IntPtr.Zero;
            }
        }
    }

    private IntPtr CreateHiddenWindow()
    {
        var className = $"TomurTrayWindow-{Guid.NewGuid():N}";
        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WindowClass
        {
            lpfnWndProc = windowProcedure,
            hInstance = instance,
            lpszClassName = className
        };

        var atom = NativeMethods.RegisterClass(ref windowClass);
        if (atom == 0)
        {
            throw CreateWin32Exception("RegisterClassW");
        }

        var handle = NativeMethods.CreateWindowEx(
            0,
            className,
            "Tomur Tray",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw CreateWin32Exception("CreateWindowExW");
        }

        return handle;
    }

    private void AddIcon()
    {
        var data = CreateNotifyIconData();
        data.uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = iconHandle;
        data.szTip = $"{Defaults.ProductName} is running";

        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data))
        {
            throw CreateWin32Exception("Shell_NotifyIconW(NIM_ADD)");
        }

        data.uVersion = NativeMethods.NotifyIconVersion4;
        _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimSetVersion, ref data);
    }

    private void RemoveIcon()
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData();
        _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
    }

    private NativeMethods.NotifyIconData CreateNotifyIconData()
    {
        return new NativeMethods.NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            hWnd = windowHandle,
            uID = 1,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr WindowProcedure(IntPtr handle, uint message, UIntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case CallbackMessage:
                HandleTrayCallback(unchecked((uint)lParam.ToInt64()) & 0xFFFF);
                return IntPtr.Zero;
            case NativeMethods.WmCommand:
                HandleMenuCommand((int)(wParam.ToUInt64() & 0xFFFF));
                return IntPtr.Zero;
            case NativeMethods.WmClose:
                RemoveIcon();
                _ = NativeMethods.DestroyWindow(handle);
                return IntPtr.Zero;
            case NativeMethods.WmDestroy:
                windowHandle = IntPtr.Zero;
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProc(handle, message, wParam, lParam);
        }
    }

    private void HandleTrayCallback(uint eventCode)
    {
        if (eventCode is NativeMethods.WmLButtonDoubleClick)
        {
            OpenUrl(serviceUrl);
            return;
        }

        if (eventCode is NativeMethods.NinSelect or NativeMethods.NinKeySelect)
        {
            OpenUrl(serviceUrl);
            return;
        }

        if (eventCode is NativeMethods.WmRButtonUp or NativeMethods.WmContextMenu)
        {
            ShowMenu();
        }
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, new UIntPtr((uint)OpenWorkspaceCommand), "Open workspace");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, new UIntPtr((uint)OpenRuntimeStatusCommand), "Runtime status");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, new UIntPtr((uint)ExitCommand), "Exit Tomur");

            _ = NativeMethods.SetForegroundWindow(windowHandle);
            if (!NativeMethods.GetCursorPos(out var point))
            {
                point = default;
            }

            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturnCommand | NativeMethods.TpmNonotify,
                point.X,
                point.Y,
                windowHandle,
                IntPtr.Zero);
            if (command != 0)
            {
                HandleMenuCommand(command);
            }

            _ = NativeMethods.PostMessage(windowHandle, NativeMethods.WmNull, UIntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    private void HandleMenuCommand(int command)
    {
        switch (command)
        {
            case OpenWorkspaceCommand:
                OpenUrl(serviceUrl);
                break;
            case OpenRuntimeStatusCommand:
                OpenUrl($"{serviceUrl}/api/runtime/status");
                break;
            case ExitCommand:
                stopApplication();
                _ = NativeMethods.PostMessage(windowHandle, NativeMethods.WmClose, UIntPtr.Zero, IntPtr.Zero);
                break;
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not open {url}: {exception.Message}");
        }
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }

    private static class NativeMethods
    {
        public const uint WmNull = 0x0000;
        public const uint WmClose = 0x0010;
        public const uint WmDestroy = 0x0002;
        public const uint WmCommand = 0x0111;
        public const uint WmContextMenu = 0x007B;
        public const uint WmLButtonDoubleClick = 0x0203;
        public const uint WmRButtonUp = 0x0205;
        public const uint WmUser = 0x0400;
        public const uint WmApp = 0x8000;
        public const uint NinSelect = WmUser;
        public const uint NinKeySelect = WmUser + 1;

        public const uint NifMessage = 0x00000001;
        public const uint NifIcon = 0x00000002;
        public const uint NifTip = 0x00000004;
        public const uint NimAdd = 0x00000000;
        public const uint NimDelete = 0x00000002;
        public const uint NimSetVersion = 0x00000004;
        public const uint NotifyIconVersion4 = 4;

        public const uint MfString = 0x00000000;
        public const uint MfSeparator = 0x00000800;
        public const uint TpmRightButton = 0x0002;
        public const uint TpmNonotify = 0x0080;
        public const uint TpmReturnCommand = 0x0100;

        public static readonly IntPtr IdiApplication = new(32512);

        public delegate IntPtr WindowProcedure(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WindowClass
        {
            public uint style;
            public WindowProcedure lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Message
        {
            public IntPtr Hwnd;
            public uint MessageId;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public Point Point;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NotifyIconData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClass(ref WindowClass windowClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetMessage(out Message message, IntPtr hWnd, uint minMessage, uint maxMessage);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr DispatchMessage(ref Message message);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", EntryPoint = "LoadIconW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string? item);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int TrackPopupMenuEx(
            IntPtr menu,
            uint flags,
            int x,
            int y,
            IntPtr owner,
            IntPtr parameters);
    }
}

internal static class ConsoleWindow
{
    public static void HideIfOwnedByCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            WindowsMethods.HideIfOwnedByCurrentProcess();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Console window could not be hidden: {exception.Message}");
        }
    }

    private static class WindowsMethods
    {
        public static void HideIfOwnedByCurrentProcess()
        {
            var console = GetConsoleWindow();
            if (console == IntPtr.Zero)
            {
                return;
            }

            Span<uint> processIds = stackalloc uint[8];
            var processCount = GetConsoleProcessList(ref MemoryMarshal.GetReference(processIds), (uint)processIds.Length);
            if (processCount <= 1)
            {
                _ = FreeConsole();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList(ref uint processList, uint processCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();
    }
}
