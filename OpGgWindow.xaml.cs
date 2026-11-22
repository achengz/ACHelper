using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace LolMatchAssistant;

public partial class OpGgWindow : Window
{
    private CoreWebView2? _web;
    private Uri? _requested;
    private bool _closed;
    private bool _starting;
    private readonly string _profile;

    public OpGgWindow() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LolMatchAssistant", "OPGG")) { }

    internal OpGgWindow(string profile)
    {
        _profile = profile;
        InitializeComponent();
        Closed += (_, _) =>
        {
            _closed = true;
            Browser.Dispose();
        };
    }

    internal void PlaceBeside(Window main)
    {
        var area = WorkArea(main);
        var placement = SidePlacement(new Rect(main.Left, main.Top, main.ActualWidth, main.ActualHeight), area);
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
    }

    internal static Rect SidePlacement(Rect owner, Rect area)
    {
        const int gap = 8;
        const int minimum = 430;
        const int preferred = 650;
        var right = area.Right - owner.Right - gap;
        var left = owner.Left - area.Left - gap;
        var available = right >= minimum ? right : left;
        var width = Math.Min(available >= minimum ? Math.Min(preferred, available) :
            Math.Clamp(right + 180, minimum, preferred), area.Width);
        var x = right >= minimum ? owner.Right + gap : left >= minimum ? owner.Left - gap - width : area.Right - width;
        var height = Math.Min(area.Height, Math.Max(380, owner.Height));
        return new(Math.Clamp(x, area.Left, area.Right - width), Math.Clamp(owner.Top, area.Top, area.Bottom - height), width, height);
    }

    internal async Task OpenPage(Uri page, string label)
    {
        PageLabel.Text = label;
        if (_requested == page && _web is not null) return;
        _requested = page;
        if (_web is not null)
        {
            _web.Navigate(page.AbsoluteUri);
            return;
        }
        if (_starting || _closed) return;
        _starting = true;
        PageStatus.Text = "正在加载 OP.GG…";
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
            if (_closed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            _web = Browser.CoreWebView2;
            _web.Settings.IsWebMessageEnabled = false;
            _web.NavigationStarting += (_, e) =>
            {
                if (!IsOpGgAddress(e.Uri)) { e.Cancel = true; return; }
                PageStatus.Text = "正在加载 OP.GG…";
            };
            _web.NavigationCompleted += (_, e) => PageStatus.Text = e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled
                ? "" : "OP.GG 暂时加载失败，请检查网络后点「刷新」。";
            _web.HistoryChanged += (_, _) =>
            {
                BackButton.IsEnabled = _web.CanGoBack;
                ForwardButton.IsEnabled = _web.CanGoForward;
            };
            _web.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsOpGgAddress(e.Uri)) _web.Navigate(e.Uri);
            };
            ReloadButton.IsEnabled = true;
            _web.Navigate(_requested.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!_closed) PageStatus.Text = "需要安装 Microsoft Edge WebView2 Runtime 才能在这里查看 OP.GG。";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (!_closed) PageStatus.Text = "无法启动 OP.GG 侧窗。请关闭侧窗后重试。";
        }
        finally { _starting = false; }
    }

    internal static bool IsOpGgAddress(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && (uri.Host == "op.gg" || uri.Host.EndsWith(".op.gg", StringComparison.Ordinal));

    private void Reload(object sender, RoutedEventArgs e)
    {
        if (_web is null || _requested is null) return;
        if (PageStatus.Text.Length > 0 && PageStatus.Text != "正在加载 OP.GG…")
            _web.Navigate(_requested.AbsoluteUri);
        else _web.Reload();
    }

    private void Back(object sender, RoutedEventArgs e) { if (_web?.CanGoBack == true) _web.GoBack(); }
    private void Forward(object sender, RoutedEventArgs e) { if (_web?.CanGoForward == true) _web.GoForward(); }

    private static Rect WorkArea(Window owner)
    {
        var handle = new WindowInteropHelper(owner).Handle;
        var monitor = MonitorFromWindow(handle, 2);
        var area = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref area) ||
            PresentationSource.FromVisual(owner)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;
        var transform = target.TransformFromDevice;
        return new(transform.Transform(new Point(area.Work.Left, area.Work.Top)),
            transform.Transform(new Point(area.Work.Right, area.Work.Bottom)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
