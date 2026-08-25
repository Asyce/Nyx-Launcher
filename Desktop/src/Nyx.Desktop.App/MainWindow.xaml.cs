using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Nyx_Desktop_App;

public sealed partial class MainWindow : Window
{
    private static readonly SizeInt32 LauncherSize = new(1280, 720);
    private const string PauseAnimationText = "Pause background animation";
    private const string ResumeAnimationText = "Resume background animation";
    private const uint DesignDpi = 96;
    private const uint WindowMessageNonClientLeftButtonDown = 0x00A1;
    private const int HitTestCaption = 2;
    private const int WindowLongStyle = -16;
    private const int WindowStyleCaption = 0x00C00000;
    private const int WindowStyleThickFrame = 0x00040000;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(nint window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(nint window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    public MainWindow()
    {
        App.SetLaunchStage("main-window-xaml");
        InitializeComponent();
        App.SetLaunchStage("main-window-icon");
        AppWindow.SetIcon("Assets/AppIcon.ico");
        App.SetLaunchStage("main-window-size");
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        ConfigureFixedClientSize();

        App.SetLaunchStage("main-window-titlebar");
        ConfigureTitleBar();
        RemoveSystemFrame();
        ConfigureFixedClientSize();
        Activated += (_, _) => RemoveSystemFrame();
        App.SetLaunchStage("main-page-navigation");
        RootFrame.Navigate(typeof(MainPage));
        App.SetLaunchStage("main-window-ready");
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
    }

    private void RemoveSystemFrame()
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var style = GetWindowLongW(windowHandle, WindowLongStyle);
        var borderlessStyle = style & ~WindowStyleCaption & ~WindowStyleThickFrame;
        if (borderlessStyle != style)
            _ = SetWindowLongW(windowHandle, WindowLongStyle, borderlessStyle);
        _ = SetWindowPos(
            windowHandle,
            0,
            0,
            0,
            0,
            0,
            SetWindowPositionNoSize
            | SetWindowPositionNoMove
            | SetWindowPositionNoZOrder
            | SetWindowPositionNoActivate
            | SetWindowPositionFrameChanged);
    }

    private void ConfigureFixedClientSize()
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(windowHandle);
        var target = CalculateClientSizeForDpi(LauncherSize, dpi);
        AppWindow.Resize(target);
        var actual = AppWindow.ClientSize;
        if (actual.Width != target.Width || actual.Height != target.Height)
        {
            // Resize accepts outer pixels on older runtimes. Correct once by
            // the observed client delta, then leave the fixed presenter alone.
            AppWindow.Resize(new SizeInt32(
                target.Width + (target.Width - actual.Width),
                target.Height + (target.Height - actual.Height)));
        }
    }

    internal static SizeInt32 CalculateClientSizeForDpi(SizeInt32 logicalSize, uint dpi) =>
        new(
            ScaleLogicalPixels(logicalSize.Width, dpi),
            ScaleLogicalPixels(logicalSize.Height, dpi));

    private static int ScaleLogicalPixels(int logicalPixels, uint dpi) =>
        (int)Math.Round(logicalPixels * (dpi == 0 ? DesignDpi : dpi) / (double)DesignDpi);

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is MainPage page)
        {
            await page.ShowSettingsAsync();
        }
    }

    private void AnimationButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is not MainPage page) return;
        UpdateAnimationButton(page.ToggleLauncherAnimation());
    }

    private void UpdateAnimationButton(bool paused)
    {
        var text = paused ? ResumeAnimationText : PauseAnimationText;
        AnimationIcon.Glyph = paused ? "\uE768" : "\uE769";
        AutomationProperties.SetName(AnimationIcon, $"{text} icon");
        AutomationProperties.SetName(AnimationButton, text);
        ToolTipService.SetToolTip(AnimationButton, new ToolTip { Content = text });
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Minimize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    internal void Minimize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    internal Task ShutDownAsync() =>
        RootFrame.Content is MainPage page
            ? page.ShutDownAsync()
            : Task.CompletedTask;

    internal void BeginDrag()
    {
        ReleaseCapture();
        _ = SendMessage(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            WindowMessageNonClientLeftButtonDown,
            HitTestCaption,
            0);
    }
}
