using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Noted.Services;

internal sealed class TrayIcon : IDisposable
{
    private const int TrayCallbackMessage = 0x8001;
    private const int WmGetIcon = 0x007F;
    private const int WmContextMenu = 0x007B;
    private const int WmRightButtonUp = 0x0205;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const int GclpIcon = -14;
    private const int GclpIconSmall = -34;
    private const int IdiApplication = 32512;

    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource _windowSource;
    private readonly IntPtr _iconHandle;
    private readonly string _tooltip;
    private readonly Action _rightClicked;
    private readonly uint _taskbarCreatedMessage;
    private bool _usesVersion4;
    private bool _isAdded;
    private bool _isDisposed;

    private TrayIcon(
        IntPtr windowHandle,
        HwndSource windowSource,
        IntPtr iconHandle,
        string tooltip,
        Action rightClicked)
    {
        _windowHandle = windowHandle;
        _windowSource = windowSource;
        _iconHandle = iconHandle;
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _rightClicked = rightClicked;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _windowSource.AddHook(WindowMessageReceived);
    }

    internal static TrayIcon? TryCreate(
        IntPtr windowHandle,
        string tooltip,
        Action rightClicked)
    {
        if (windowHandle == IntPtr.Zero || HwndSource.FromHwnd(windowHandle) is not HwndSource windowSource)
            return null;

        var iconHandle = GetWindowIcon(windowHandle);
        if (iconHandle == IntPtr.Zero)
            return null;

        var trayIcon = new TrayIcon(windowHandle, windowSource, iconHandle, tooltip, rightClicked);
        if (trayIcon.AddIcon())
            return trayIcon;

        trayIcon.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        if (_isAdded)
        {
            var data = CreateIconData(0);
            ShellNotifyIcon(NimDelete, ref data);
            _isAdded = false;
        }

        _windowSource.RemoveHook(WindowMessageReceived);
    }

    private bool AddIcon()
    {
        var data = CreateIconData(NifMessage | NifIcon | NifTip | NifShowTip);
        if (!ShellNotifyIcon(NimAdd, ref data))
            return false;

        _isAdded = true;
        data.Version = NotifyIconVersion4;
        _usesVersion4 = false;
        _usesVersion4 = ShellNotifyIcon(NimSetVersion, ref data);
        return true;
    }

    private IntPtr WindowMessageReceived(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && (uint)message == _taskbarCreatedMessage)
        {
            _isAdded = false;
            AddIcon();
            return IntPtr.Zero;
        }

        if (message != TrayCallbackMessage)
            return IntPtr.Zero;

        var notification = _usesVersion4
            ? unchecked((int)(lParam.ToInt64() & 0xFFFF))
            : lParam.ToInt32();
        var isRightClick = _usesVersion4
            ? notification == WmContextMenu
            : notification == WmRightButtonUp;

        if (!isRightClick)
            return IntPtr.Zero;

        handled = true;
        _rightClicked();
        return IntPtr.Zero;
    }

    private NotifyIconData CreateIconData(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = 1,
        Flags = flags,
        CallbackMessage = TrayCallbackMessage,
        IconHandle = _iconHandle,
        Tip = _tooltip,
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private static IntPtr GetWindowIcon(IntPtr windowHandle)
    {
        var iconHandle = SendMessage(windowHandle, WmGetIcon, new IntPtr(IconSmall2), IntPtr.Zero);
        if (iconHandle == IntPtr.Zero)
            iconHandle = SendMessage(windowHandle, WmGetIcon, new IntPtr(IconSmall), IntPtr.Zero);
        if (iconHandle == IntPtr.Zero)
            iconHandle = SendMessage(windowHandle, WmGetIcon, new IntPtr(IconBig), IntPtr.Zero);
        if (iconHandle == IntPtr.Zero)
            iconHandle = GetClassIcon(windowHandle, GclpIconSmall);
        if (iconHandle == IntPtr.Zero)
            iconHandle = GetClassIcon(windowHandle, GclpIcon);
        if (iconHandle == IntPtr.Zero)
            iconHandle = LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication));
        return iconHandle;
    }

    private static IntPtr GetClassIcon(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetClassLongPtr64(windowHandle, index)
            : new IntPtr(unchecked((int)GetClassLong32(windowHandle, index)));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal int Size;
        internal IntPtr WindowHandle;
        internal uint Id;
        internal uint Flags;
        internal int CallbackMessage;
        internal IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Tip;

        internal uint State;
        internal uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Info;

        internal uint Version;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string InfoTitle;

        internal uint InfoFlags;
        internal Guid ItemGuid;
        internal IntPtr BalloonIconHandle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instanceHandle, IntPtr iconName);
}
