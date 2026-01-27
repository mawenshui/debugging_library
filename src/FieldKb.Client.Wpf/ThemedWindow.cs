using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FieldKb.Client.Wpf;

public class ThemedWindow : Window
{
    private static readonly Uri ThemeSource = new("pack://application:,,,/FieldKb.Client.Wpf;component/Themes/LightTheme.xaml", UriKind.Absolute);
    private static ResourceDictionary? _themeDictionary;

    public ThemedWindow()
    {
        EnsureThemeLoaded();

        if (_themeDictionary is not null
            && ReadLocalValue(StyleProperty) == DependencyProperty.UnsetValue
            && _themeDictionary.Contains(typeof(Window)))
        {
            Style = _themeDictionary[typeof(Window)] as Style;
        }

        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, OnMinimizeExecuted, OnMinimizeCanExecute));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, OnMaximizeExecuted, OnMaximizeCanExecute));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, OnRestoreExecuted, OnRestoreCanExecute));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, OnCloseExecuted));

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            if (HwndSource.FromHwnd(hwnd) is { } source)
            {
                source.AddHook(WndProc);
            }
        };
    }

    private void OnMinimizeExecuted(object? sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMinimizeCanExecute(object? sender, CanExecuteRoutedEventArgs e) => e.CanExecute = ResizeMode != ResizeMode.NoResize;

    private void OnMaximizeExecuted(object? sender, ExecutedRoutedEventArgs e) => SystemCommands.MaximizeWindow(this);

    private void OnMaximizeCanExecute(object? sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = ResizeMode == ResizeMode.CanResize || ResizeMode == ResizeMode.CanResizeWithGrip;

    private void OnRestoreExecuted(object? sender, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);

    private void OnRestoreCanExecute(object? sender, CanExecuteRoutedEventArgs e) => e.CanExecute = WindowState == WindowState.Maximized;

    private void OnCloseExecuted(object? sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var rcWork = monitorInfo.rcWork;
                var rcMonitor = monitorInfo.rcMonitor;

                mmi.ptMaxPosition.x = rcWork.left - rcMonitor.left;
                mmi.ptMaxPosition.y = rcWork.top - rcMonitor.top;
                mmi.ptMaxSize.x = rcWork.right - rcWork.left;
                mmi.ptMaxSize.y = rcWork.bottom - rcWork.top;
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private static void EnsureThemeLoaded()
    {
        if (_themeDictionary is not null)
        {
            return;
        }

        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        var existing = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source is not null && Uri.Compare(d.Source, ThemeSource, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0);

        if (existing is not null)
        {
            _themeDictionary = existing;
            return;
        }

        var dict = new ResourceDictionary { Source = ThemeSource };
        app.Resources.MergedDictionaries.Add(dict);
        _themeDictionary = dict;
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
