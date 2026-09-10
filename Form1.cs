using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KrunkerWebViewClient;

public sealed class Form1 : Form
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;

    // The hook only observes button state. It deliberately does not reinject
    // mouse_event calls because reinjection causes recursive hooks, duplicate
    // clicks, and input-queue stalls under load.
    private const bool EnableGlobalMouseHook = true;

    // Priority changes are limited to WebView2 child processes.
    // The application and Desktop Window Manager are not reprioritized.
    private const bool EnableWebViewProcessPriority = true;
    private const ProcessPriorityClass WebViewProcessPriority =
        ProcessPriorityClass.High;

    private static readonly LowLevelMouseProc MouseHookProc =
        MouseHookCallback;

    private static int leftButtonIsDown;

    private readonly WebView2 webView = new()
    {
        Dock = DockStyle.Fill,
        TabIndex = 0
    };

    private readonly CancellationTokenSource lifetimeCts = new();

    private IntPtr mouseHookId;
    private Task? processPriorityTask;
    private bool browserInitializationStarted;
    private bool isFullscreen;

    private FormWindowState previousWindowState =
        FormWindowState.Normal;

    private FormBorderStyle previousBorderStyle =
        FormBorderStyle.Sizable;

    public Form1()
    {
        Text = "Krunker WebView Client";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 540);
        ClientSize = new Size(1280, 720);
        KeyPreview = true;

        Controls.Add(webView);

        KeyDown += Form1_KeyDown;
        Shown += Form1_Shown;
    }

    private async void Form1_Shown(object? sender, EventArgs e)
    {
        if (browserInitializationStarted)
        {
            return;
        }

        browserInitializationStarted = true;

        try
        {
            await InitializeBrowserAsync(lifetimeCts.Token);
        }
        catch (OperationCanceledException)
            when (lifetimeCts.IsCancellationRequested)
        {
            // Normal during shutdown.
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"WebView2 could not be initialized.\r\n\r\n" +
                $"{exception.Message}",
                "Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task InitializeBrowserAsync(
        CancellationToken cancellationToken)
    {
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "KrunkerWebViewClient",
            "WebView2");

        Directory.CreateDirectory(userDataFolder);

        var environmentOptions =
            new CoreWebView2EnvironmentOptions(
                BuildBrowserArguments());

        CoreWebView2Environment environment =
            await CoreWebView2Environment.CreateAsync(
                null,
                userDataFolder,
                environmentOptions);

        cancellationToken.ThrowIfCancellationRequested();

        await webView.EnsureCoreWebView2Async(environment);

        cancellationToken.ThrowIfCancellationRequested();

        CoreWebView2 coreWebView = webView.CoreWebView2;

        coreWebView.Settings.IsZoomControlEnabled = false;
        coreWebView.Settings.AreDefaultContextMenusEnabled = false;
        coreWebView.Settings.AreDevToolsEnabled = false;
        coreWebView.Settings.IsStatusBarEnabled = false;

        coreWebView.NewWindowRequested +=
            CoreWebView2_NewWindowRequested;

        coreWebView.NavigationStarting +=
            CoreWebView2_NavigationStarting;

        if (EnableGlobalMouseHook)
        {
            InstallMouseHook();
        }

        if (EnableWebViewProcessPriority)
        {
            processPriorityTask =
                MonitorWebViewProcessesAsync(
                    lifetimeCts.Token);
        }

        coreWebView.Navigate("https://krunker.io/");
    }

    private static string BuildBrowserArguments()
    {
        // These are conservative Chromium switches that reduce background
        // throttling while preserving normal GPU scheduling and compositor
        // behavior.
        //
        // Disabling vsync, IPC flood protection, or the GPU blocklist can
        // cause tearing, hangs, or driver instability, so those switches are
        // intentionally not used.
        string[] arguments =
        [
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--enable-gpu-rasterization",
            "--enable-oop-rasterization",
            "--enable-zero-copy"
        ];

        return string.Join(' ', arguments);
    }

    private void CoreWebView2_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Keep target="_blank" and popup navigations inside this managed
        // window. Only HTTP and HTTPS URLs are accepted.
        if (!Uri.TryCreate(
                e.Uri,
                UriKind.Absolute,
                out Uri? destination) ||
            (destination.Scheme != Uri.UriSchemeHttp &&
             destination.Scheme != Uri.UriSchemeHttps))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        webView.CoreWebView2.Navigate(
            destination.AbsoluteUri);
    }

    private static void CoreWebView2_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        // Prevent web content from launching arbitrary local applications,
        // file URLs, or other unsupported schemes.
        if (!Uri.TryCreate(
                e.Uri,
                UriKind.Absolute,
                out Uri? destination))
        {
            e.Cancel = true;
            return;
        }

        bool allowed =
            destination.Scheme == Uri.UriSchemeHttp ||
            destination.Scheme == Uri.UriSchemeHttps ||
            destination.Scheme == Uri.UriSchemeAbout;

        e.Cancel = !allowed;
    }

    private async Task MonitorWebViewProcessesAsync(
        CancellationToken cancellationToken)
    {
        using var timer =
            new PeriodicTimer(TimeSpan.FromSeconds(3));

        try
        {
            do
            {
                ApplyWebViewProcessPriority();
            }
            while (await timer
                .WaitForNextTickAsync(cancellationToken)
                .ConfigureAwait(false));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the form closes.
        }
    }

    private static void ApplyWebViewProcessPriority()
    {
        foreach (Process process in
                 Process.GetProcessesByName("msedgewebview2"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    if (process.PriorityClass !=
                        WebViewProcessPriority)
                    {
                        process.PriorityClass =
                            WebViewProcessPriority;
                    }
                }
                catch (ArgumentException)
                {
                    // The process exited between enumeration and inspection.
                }
                catch (InvalidOperationException)
                {
                    // The process exited or no longer exposes metadata.
                }
                catch (Win32Exception)
                {
                    // Access may be denied for another security context.
                }
            }
        }
    }

    private void InstallMouseHook()
    {
        if (!OperatingSystem.IsWindows() ||
            mouseHookId != IntPtr.Zero)
        {
            return;
        }

        mouseHookId = SetWindowsHookEx(
            WhMouseLl,
            MouseHookProc,
            GetModuleHandle(null),
            0);
    }

    private void UninstallMouseHook()
    {
        if (mouseHookId == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(mouseHookId);

        mouseHookId = IntPtr.Zero;

        Interlocked.Exchange(
            ref leftButtonIsDown,
            0);
    }

    private static IntPtr MouseHookCallback(
        int code,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (code >= 0)
        {
            int message =
                unchecked((int)wParam.ToInt64());

            if (message == WmLButtonDown)
            {
                Interlocked.Exchange(
                    ref leftButtonIsDown,
                    1);
            }
            else if (message == WmLButtonUp)
            {
                Interlocked.Exchange(
                    ref leftButtonIsDown,
                    0);
            }
        }

        // Do not perform managed UI work, allocation, logging, sleeping,
        // or input injection in this callback. Pass through immediately.
        return CallNextHookEx(
            IntPtr.Zero,
            code,
            wParam,
            lParam);
    }

    private void Form1_KeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F11)
        {
            return;
        }

        ToggleFullscreen();

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void ToggleFullscreen()
    {
        if (isFullscreen)
        {
            FormBorderStyle = previousBorderStyle;
            WindowState = previousWindowState;
            TopMost = false;
            isFullscreen = false;
            return;
        }

        previousBorderStyle = FormBorderStyle;
        previousWindowState = WindowState;

        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        isFullscreen = true;
    }

    protected override void OnFormClosing(
        FormClosingEventArgs e)
    {
        lifetimeCts.Cancel();

        UninstallMouseHook();

        if (webView.CoreWebView2 is not null)
        {
            webView.CoreWebView2.NewWindowRequested -=
                CoreWebView2_NewWindowRequested;

            webView.CoreWebView2.NavigationStarting -=
                CoreWebView2_NavigationStarting;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(
        FormClosedEventArgs e)
    {
        webView.Dispose();
        lifetimeCts.Dispose();

        base.OnFormClosed(e);
    }

    [UnmanagedFunctionPointer(
        CallingConvention.Winapi)]
    private delegate IntPtr LowLevelMouseProc(
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(
        IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr GetModuleHandle(
        string? moduleName);
}
