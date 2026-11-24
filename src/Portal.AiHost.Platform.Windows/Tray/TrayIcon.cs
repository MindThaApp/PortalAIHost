using System.Runtime.InteropServices;

namespace Portal.AiHost.Platform.Windows.Tray;

/// <summary>
/// Plain <c>Shell_NotifyIcon</c> tray icon with Open and Quit, on its own message-loop thread. Its hidden
/// top-level window also receives <c>WM_POWERBROADCAST</c> (resume from sleep) and <c>WM_QUERYENDSESSION</c> /
/// <c>WM_ENDSESSION</c> (shutdown, logoff, update), so the host can stop cleanly.
/// Events are raised on the tray thread; handlers should hand work off quickly.
/// </summary>
public sealed unsafe partial class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmTrayCallback = WmApp + 1;
    private const uint WmCloseTray = WmApp + 2;
    private const uint WmDestroy = 0x0002;
    private const uint WmCommand = 0x0111;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const nint EndSessionCloseApp = 0x1;
    private const uint RestartNoCrash = 1, RestartNoHang = 2, RestartNoReboot = 8;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const nint PbtApmResumeAutomatic = 0x12;
    private const nint PbtApmResumeSuspend = 0x7;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifInfo = 0x10, NiifInfo = 1, NiifRespectQuietTime = 0x80;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint MfString = 0, MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2;
    private const int CmdOpen = 1, CmdQuit = 2;

    private static readonly Dictionary<nint, TrayIcon> Windows = new();
    private static readonly Lock WindowsGate = new();

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly string _tooltip;
    private readonly string _openText;
    private readonly string _quitText;
    private readonly bool _showIcon;
    private uint _taskbarCreated;
    private nint _hwnd;
    private Exception? _startError;

    private TrayIcon(string tooltip, string openText, string quitText, bool showIcon)
    {
        _showIcon = showIcon;
        _tooltip = tooltip;
        _openText = openText;
        _quitText = quitText;
        _thread = new Thread(Run) { IsBackground = true, Name = "Tray" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public event EventHandler? OpenRequested;

    /// <summary>The user clicked a notification from <see cref="ShowBalloon"/>; the value is the tag it was shown with.</summary>
    public event EventHandler<string>? BalloonClicked;

    /// <summary>Left click on the icon: the small status pop-up. The point is the cursor in screen pixels.</summary>
    public event EventHandler<(int X, int Y)>? PopupRequested;

    public event EventHandler? QuitRequested;

    public event EventHandler? Resumed;

    /// <summary>
    /// Windows is ending the session (shutdown, restart, logoff) or closing the app for an update (argument true).
    /// Raised synchronously on the tray thread: the handler must finish its clean stop before it returns, because
    /// the process can be ended as soon as it does.
    /// </summary>
    public event EventHandler<bool>? SessionEnding;

    /// <summary>The hidden window that receives tray, power and session messages.</summary>
    public nint WindowHandle => _hwnd;

    /// <summary>
    /// Starts the hidden window and, when <paramref name="showIcon"/> is true, the tray icon. Without the icon the
    /// window still receives resume and session-end messages (test mode, or a host with no tray).
    /// </summary>
    public static TrayIcon Start(string tooltip = "Portal AI Host", string openText = "Open", string quitText = "Quit",
        bool showIcon = true)
    {
        var tray = new TrayIcon(tooltip, openText, quitText, showIcon);
        tray._thread.Start();
        tray._ready.Wait(TimeSpan.FromSeconds(10));
        if (tray._startError is { } error)
        {
            throw new InvalidOperationException("The tray icon could not be created.", error);
        }

        return tray;
    }

    private void Run()
    {
        try
        {
            var instance = GetModuleHandleW(null);
            var className = "PortalAIHost.Tray." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fixed (char* cls = className)
            {
                var wc = new WndClassExW
                {
                    CbSize = (uint)sizeof(WndClassExW),
                    LpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                    HInstance = instance,
                    LpszClassName = cls,
                };
                if (RegisterClassExW(&wc) == 0)
                {
                    throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastPInvokeError());
                }

                // A hidden top-level window (not message-only) so broadcast power messages reach it.
                _hwnd = CreateWindowExW(0, cls, cls, 0, -32000, -32000, 0, 0, 0, 0, instance, 0);
            }

            if (_hwnd == 0)
            {
                throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastPInvokeError());
            }

            lock (WindowsGate)
            {
                Windows[_hwnd] = this;
            }

            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            AddIcon();
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();
        Msg msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    private void AddIcon()
    {
        if (_showIcon)
        {
            Notify(NimAdd, NifMessage | NifIcon | NifTip);
        }
    }

    /// <summary>
    /// Asks Windows to start the host again after an update or Restart Manager closed it (not after a crash, a
    /// hang or a reboot). Returns false when Windows refused.
    /// </summary>
    public static bool RegisterForRestartAfterUpdate() =>
        RegisterApplicationRestart(null, RestartNoCrash | RestartNoHang | RestartNoReboot) == 0;

    private string? _balloonTag;

    /// <summary>
    /// Shows a Windows notification from the tray icon. Windows decides how it looks and keeps it quiet during
    /// Focus Assist (the quiet-time flag). Returns false when there is no icon to show it from. A click raises
    /// <see cref="BalloonClicked"/> with <paramref name="tag"/>.
    /// </summary>
    public bool ShowBalloon(string title, string text, string tag)
    {
        if (_hwnd == 0 || !_showIcon)
        {
            return false;
        }

        _balloonTag = tag;
        var data = new NotifyIconDataW
        {
            CbSize = (uint)sizeof(NotifyIconDataW),
            HWnd = _hwnd,
            UId = 1,
            UFlags = NifInfo,
            DwInfoFlags = NiifInfo | NiifRespectQuietTime,
        };
        var body = text.Length > 255 ? text[..255] : text;
        var head = title.Length > 63 ? title[..63] : title;
        body.AsSpan().CopyTo(new Span<char>(data.SzInfo, 256));
        head.AsSpan().CopyTo(new Span<char>(data.SzInfoTitle, 64));
        return Shell_NotifyIconW(NimModify, &data);
    }

    private void Notify(uint message, uint flags)
    {
        var data = new NotifyIconDataW
        {
            CbSize = (uint)sizeof(NotifyIconDataW),
            HWnd = _hwnd,
            UId = 1,
            UFlags = flags,
            UCallbackMessage = WmTrayCallback,
            HIcon = LoadIconW(0, 32512), // IDI_APPLICATION
        };
        var tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip;
        tip.AsSpan().CopyTo(new Span<char>(data.SzTip, 128));
        Shell_NotifyIconW(message, &data);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        TrayIcon? tray;
        lock (WindowsGate)
        {
            Windows.TryGetValue(hwnd, out tray);
        }

        if (tray is not null)
        {
            try
            {
                if (tray.Handle(msg, wParam, lParam) is { } result)
                {
                    return result;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never let an exception cross the native boundary.
            }
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint? Handle(uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            AddIcon(); // Explorer restarted.
            return 0;
        }

        switch (msg)
        {
            case WmTrayCallback:
                var mouse = (uint)(lParam & 0xFFFF);
                if (mouse == NinBalloonUserClick)
                {
                    if (_balloonTag is { } tag)
                    {
                        BalloonClicked?.Invoke(this, tag);
                    }

                    return 0;
                }

                if (mouse == WmLButtonDblClk)
                {
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (mouse == WmLButtonUp)
                {
                    GetCursorPos(out var click);
                    PopupRequested?.Invoke(this, (click.X, click.Y));
                }
                else if (mouse is WmRButtonUp or WmContextMenu)
                {
                    ShowMenu();
                }

                return 0;
            case WmQueryEndSession:
                return 1; // Never block shutdown; the clean stop happens on WM_ENDSESSION.
            case WmEndSession:
                if (wParam != 0)
                {
                    SessionEnding?.Invoke(this, (lParam & EndSessionCloseApp) != 0);
                }

                return 0;
            case WmPowerBroadcast:
                if (wParam is PbtApmResumeAutomatic or PbtApmResumeSuspend)
                {
                    Resumed?.Invoke(this, EventArgs.Empty);
                }

                return 1;
            case WmCloseTray:
                if (_showIcon)
                {
                    Notify(NimDelete, 0);
                }

                DestroyWindow(_hwnd);
                return 0;
            case WmDestroy:
                lock (WindowsGate)
                {
                    Windows.Remove(_hwnd);
                }

                PostQuitMessage(0);
                return 0;
            case WmCommand:
                return 0;
        }

        return null;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MfString, CmdOpen, _openText);
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, CmdQuit, _quitText);
            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, pt.X, pt.Y, _hwnd, 0);
            if (cmd == CmdOpen)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (cmd == CmdQuit)
            {
                QuitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            PostMessageW(_hwnd, WmCloseTray, 0, 0);
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join(TimeSpan.FromSeconds(5));
            }

            _hwnd = 0;
        }

        _ready.Dispose();
    }

    // ---- interop --------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassExW
    {
        public uint CbSize;
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public char* LpszMenuName;
        public char* LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconDataW
    {
        public uint CbSize;
        public nint HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public nint HIcon;
        public fixed char SzTip[128];
        public uint DwState;
        public uint DwStateMask;
        public fixed char SzInfo[256];
        public uint UVersion;
        public fixed char SzInfoTitle[64];
        public uint DwInfoFlags;
        public Guid GuidItem;
        public nint HBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint HWnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public Point Pt;
        public uint LPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string? commandLine, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WndClassExW* wc);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    private static partial nint LoadIconW(nint instance, nint name);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, NotifyIconDataW* data);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
