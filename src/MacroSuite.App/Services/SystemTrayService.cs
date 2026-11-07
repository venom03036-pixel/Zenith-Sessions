using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using MacroSuite.App.ViewModels;

namespace MacroSuite.App.Services;

/// <summary>
/// Native Win32 Shell_NotifyIcon service providing system tray integration without WinForms dependencies.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int WM_TRAYICON = 0x8000 + 100;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

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
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    private readonly IntPtr _hWnd;
    private readonly Window _window;
    private readonly HwndSource _source;
    private NOTIFYICONDATA _nid;
    private bool _isAdded;
    private bool _disposed;
    private readonly uint _taskbarCreatedMessage;
    private ContextMenu? _menu;
    private MacroSuite.Infrastructure.Windows.ApplicationIconHandle? _applicationIcon;
    public bool IsAvailable => _isAdded && !_disposed;
    public event Action<bool>? AvailabilityChanged;

    public SystemTrayService(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _hWnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hWnd) ?? throw new InvalidOperationException("Cannot obtain HwndSource.");
        _source.AddHook(HwndHook);

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        if (Environment.ProcessPath is { } executablePath)
            _applicationIcon = MacroSuite.Infrastructure.Windows.ApplicationIconHandle.FromExecutable(executablePath);
        IntPtr appIcon = _applicationIcon is { IsInvalid: false }
            ? _applicationIcon.DangerousGetHandle()
            : LoadIcon(IntPtr.Zero, (IntPtr)32512);

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = appIcon,
            szTip = "Zenith Macros"
        };

        AddTrayIcon();
    }

    private void AddTrayIcon()
    {
        bool wasAvailable = IsAvailable;
        _isAdded = Shell_NotifyIcon(NIM_ADD, ref _nid);
        if (wasAvailable != IsAvailable) AvailabilityChanged?.Invoke(IsAvailable);
    }

    /// <summary>
    /// Minimizes window to tray.
    /// </summary>
    public void MinimizeToTray()
    {
        _window.Hide();
    }

    /// <summary>The caller retains ownership of the handle until it is replaced.</summary>
    public void UpdateIcon(IntPtr icon)
    {
        if (_disposed || !_isAdded || icon == IntPtr.Zero) return;
        _nid.hIcon = icon;
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }

    public void RestoreDefaultIcon()
    {
        if (_applicationIcon is { IsInvalid: false }) UpdateIcon(_applicationIcon.DangerousGetHandle());
    }

    /// <summary>
    /// Restores window from tray.
    /// </summary>
    public void Restore()
    {
        if (_window is MainWindow studio)
        {
            studio.RestoreFromMiniMode();
            return;
        }
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OpenTrayMenu()
    {
        if (_menu is null)
        {
            _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            var restore = new MenuItem { Header = "Restore Zenith Macros" };
            restore.Click += (_, _) => Restore();
            _menu.Items.Add(restore);
            if (_window is MainWindow studio && _window.DataContext is MainViewModel vm)
            {
                var mini = new MenuItem { Header = "Show mini toolbar" };
                mini.Click += (_, _) => studio.SwitchToMiniMode();
                _menu.Items.Add(mini);
                _menu.Items.Add(new Separator());
                AddSetting("Mini toolbar click-through", nameof(SettingsViewModel.MiniToolbarClickThrough));
                AddSetting("Hide mini toolbar from screenshots", nameof(SettingsViewModel.MiniToolbarHideFromCapture));
                var settings = new MenuItem { Header = "Mini toolbar settings…" };
                settings.Click += (_, _) => { vm.CurrentViewModel = vm.SettingsVM; Restore(); };
                _menu.Items.Add(settings);
                _menu.Items.Add(new Separator());
                var panic = new MenuItem { Command = vm.PanicCommand };
                panic.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding(nameof(MainViewModel.PanicButtonText)) { Source = vm });
                _menu.Items.Add(panic);

                void AddSetting(string title, string property)
                {
                    var item = new MenuItem { Header = title, IsCheckable = true };
                    item.SetBinding(MenuItem.IsCheckedProperty, new Binding(property) { Source = vm.SettingsVM, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                    if (property == nameof(SettingsViewModel.MiniToolbarClickThrough))
                        item.SetBinding(MenuItem.InputGestureTextProperty, new Binding(nameof(SettingsViewModel.MiniToolbarClickThroughHotkey)) { Source = vm.SettingsVM });
                    _menu.Items.Add(item);
                }
            }
        }
        SetForegroundWindow(_hWnd);
        _menu.IsOpen = true;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            // Explorer owns notification icons; it asks applications to recreate them after restart.
            AddTrayIcon();
        }
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32();
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                Restore();
                handled = true;
            }
            else if (mouseMsg is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                OpenTrayMenu();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_menu is not null) { _menu.IsOpen = false; _menu.Items.Clear(); _menu = null; }

        if (_isAdded)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _isAdded = false;
        }

        _source.RemoveHook(HwndHook);
        _applicationIcon?.Dispose();
    }
}
