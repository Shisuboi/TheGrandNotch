using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TheGrandNotch.Services;

/// <summary>
/// Icône de zone de notification (system tray) 100% native via Shell_NotifyIcon,
/// sans dépendance WinForms. Clic gauche = ouvrir les paramètres,
/// clic droit = menu contextuel (Paramètres / Quitter).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // Messages Windows
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;

    // Shell_NotifyIcon
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    // LoadImage
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    // Menu
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint CMD_SETTINGS = 1;
    private const uint CMD_QUIT = 2;

    private readonly uint _uid = 1;
    private readonly HwndSource _source;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMsg;
    private IntPtr _hIcon;
    private string _tooltip;
    private bool _added;
    private bool _disposed;

    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public TrayIcon(string iconPath, string tooltip)
    {
        _tooltip = tooltip;

        var parameters = new HwndSourceParameters("TheGrandNotchTrayHost")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _hwnd = _source.Handle;

        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        AddOrUpdate(NIM_ADD);
    }

    private void AddOrUpdate(int message)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = _uid,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = _tooltip
        };
        if (Shell_NotifyIcon(message, ref data))
            _added = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMsg)
        {
            // L'explorateur a redémarré : on réinjecte l'icône
            _added = false;
            AddOrUpdate(NIM_ADD);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32() & 0xFFFF;
            if (mouseMsg == WM_LBUTTONUP)
            {
                SettingsRequested?.Invoke();
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(menu, MF_STRING, CMD_QUIT, "Quitter");

            GetCursorPos(out POINT pt);
            // Indispensable pour que le menu se ferme correctement au clic ailleurs
            SetForegroundWindow(_hwnd);

            uint cmd = (uint)TrackPopupMenuEx(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y,
                _hwnd,
                IntPtr.Zero);

            switch (cmd)
            {
                case CMD_SETTINGS:
                    SettingsRequested?.Invoke();
                    break;
                case CMD_QUIT:
                    QuitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_added)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = _uid
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    // ----- P/Invoke -----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
