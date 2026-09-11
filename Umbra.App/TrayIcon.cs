using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Umbra.App;

// Icône systray implémentée en P/Invoke Win32 direct (Shell_NotifyIcon)
// plutôt que via System.Windows.Forms.NotifyIcon : activer UseWindowsForms
// dans le csproj injecte des `global using System.Windows.Forms` qui
// entrent en collision dans tout le projet avec les types WPF de même nom
// (Button, UserControl, Brush, KeyEventArgs...) - conflit connu du SDK
// .NET quand UseWPF et UseWindowsForms sont activés ensemble. Cette classe
// évite complètement la dépendance WinForms, dans le même esprit que
// DwmBackdrop.cs (API Win32 stable, pas de nouvelle surface de risque).
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint MF_STRING = 0x0;
    private const uint MF_GRAYED = 0x1;
    private const uint MF_SEPARATOR = 0x800;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIdNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    // Quand l'Explorateur Windows redémarre (plantage, ou redémarrage manuel
    // depuis le Gestionnaire des tâches), la zone de notification est
    // recréée vide et diffuse ce message enregistré à toutes les fenêtres de
    // premier niveau. Sans le traiter, l'icône d'Umbra disparaissait pour de
    // bon : l'app continuait de tourner (et le watchdog de bloquer) sans
    // aucun moyen visible d'ouvrir le tableau de bord ou d'arrêter la
    // session, puisque la croix masque vers le systray.
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly System.Drawing.Icon _icon;
    private readonly string _tooltip;
    private readonly List<(uint Id, string Label, Action? OnClick)> _menuItems = new();
    private HwndSource? _source;
    private bool _disposed;

    public TrayIcon(Window window, string tooltip)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _icon = LoadIcon();
        _hIcon = _icon.Handle;
        _tooltip = tooltip;

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        AddIcon();
    }

    private void AddIcon()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _hIcon,
            szTip = _tooltip,
        };
        Shell_NotifyIconW(0 /* NIM_ADD */, ref data);
    }

    // icon.ico est marqué <Resource> (pour Window.Icon en XAML), pas
    // <Content> - il n'existe donc pas comme fichier à côté de l'exe, il
    // faut le lire depuis le flux de ressources embarqué de l'assembly.
    private static System.Drawing.Icon LoadIcon()
    {
        var uri = new Uri("icon.ico", UriKind.Relative);
        var streamInfo = System.Windows.Application.GetResourceStream(uri) ?? throw new InvalidOperationException("icon.ico manquant des ressources embarquées");
        return new System.Drawing.Icon(streamInfo.Stream);
    }

    // Clic gauche ou double-clic sur l'icône : action principale (afficher le
    // tableau de bord), comme n'importe quelle app à icône systray.
    public event Action? Activated;
    public event Action? MenuOpening;

    public void SetMenu(IEnumerable<(string Label, Action? OnClick)> items)
    {
        _menuItems.Clear();
        uint id = 100;
        foreach (var (label, onClick) in items)
        {
            _menuItems.Add((id, label, onClick));
            id++;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (TaskbarCreatedMessage != 0 && msg == (int)TaskbarCreatedMessage)
        {
            if (!_disposed) AddIcon();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage) return IntPtr.Zero;

        var evt = lParam.ToInt32();
        // Le clic gauche ouvrait le menu contextuel, ce qui rendait le
        // double-clic inatteignable : TrackPopupMenuEx est modal, il
        // s'ouvrait dès le premier WM_LBUTTONUP et avalait le second clic,
        // donc DoubleClicked ne se déclenchait jamais. Convention Windows :
        // gauche = action principale, droite = menu.
        if (evt is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
        {
            Activated?.Invoke();
            handled = true;
        }
        else if (evt == WM_RBUTTONUP)
        {
            ShowMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        MenuOpening?.Invoke();
        if (_menuItems.Count == 0) return;
        var hMenu = CreatePopupMenu();
        try
        {
            foreach (var (id, label, onClick) in _menuItems)
            {
                if (label == "-") AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
                else AppendMenuW(hMenu, MF_STRING | (onClick is null ? MF_GRAYED : 0), id, label);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var selected = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            if (selected != 0)
            {
                var item = _menuItems.FirstOrDefault(m => m.Id == (uint)selected);
                item.OnClick?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1 };
        Shell_NotifyIconW(2 /* NIM_DELETE */, ref data);
        _source?.RemoveHook(WndProc);
        _icon.Dispose();
    }
}
