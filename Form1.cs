using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KrunkerWebViewClient;

public sealed class Form1 : Form
{
    private const int WmHotKey = 0x0312;
    private const int F11HotKeyId = 0x4B11;

    private const uint ModNoRepeat = 0x4000;

    // Keep this disabled. Raising WebView2 to High priority can cause
    // network and compositor starvation during heavy rendering.
    private const bool EnableWebViewProcessPriority = false;

    private const ProcessPriorityClass WebViewProcessPriority =
        ProcessPriorityClass.AboveNormal;

    private readonly WebView2 webView = new()
    {
        Dock = DockStyle.Fill,
        TabIndex = 0
    };

    private readonly CancellationTokenSource lifetimeCts = new();

    private Task? processPriorityTask;
    private bool browserInitializationStarted;
    private bool isFullscreen;
    private bool f11HotKeyRegistered;
    private F11MessageFilter? f11FallbackFilter;

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

        Controls.Add(webView);

        Shown += Form1_Shown;
    }

    protected override void OnHandleCreated(
        EventArgs e)
    {
        base.OnHandleCreated(e);

        // Register F11 directly against the form window. This still works
        // while the WebView2 child window owns keyboard focus.
        f11HotKeyRegistered = RegisterHotKey(
            Handle,
            F11HotKeyId,
            ModNoRepeat,
            (uint)Keys.F11);

        // Fallback for environments where RegisterHotKey is unavailable.
        if (!f11HotKeyRegistered)
        {
            f11FallbackFilter =
                new F11MessageFilter(this);

            Application.AddMessageFilter(
                f11FallbackFilter);
        }
    }

    protected override void OnHandleDestroyed(
        EventArgs e)
    {
        if (f11HotKeyRegistered)
        {
            UnregisterHotKey(
                Handle,
                F11HotKeyId);

            f11HotKeyRegistered = false;
        }

        if (f11FallbackFilter is not null)
        {
            Application.RemoveMessageFilter(
                f11FallbackFilter);

            f11FallbackFilter = null;
        }

        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(
        ref Message message)
    {
        if (message.Msg == WmHotKey &&
            message.WParam.ToInt64() == F11HotKeyId)
        {
            ToggleFullscreen();
            return;
        }

        base.WndProc(ref message);
    }

    private async void Form1_Shown(
        object? sender,
        EventArgs e)
    {
        if (browserInitializationStarted)
        {
            return;
        }

        browserInitializationStarted = true;

        try
        {
            await InitializeBrowserAsync(
                lifetimeCts.Token);
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

        await webView.EnsureCoreWebView2Async(
            environment);

        cancellationToken.ThrowIfCancellationRequested();

        CoreWebView2 coreWebView =
            webView.CoreWebView2;

        coreWebView.Settings.IsZoomControlEnabled = false;
        coreWebView.Settings.AreDefaultContextMenusEnabled = false;
        coreWebView.Settings.AreDevToolsEnabled = false;
        coreWebView.Settings.IsStatusBarEnabled = false;

        coreWebView.NewWindowRequested +=
            CoreWebView2_NewWindowRequested;

        coreWebView.NavigationStarting +=
            CoreWebView2_NavigationStarting;

        // This is intentionally disabled by default. If enabled, use only
        // AboveNormal rather than High to reduce scheduling interference.
        if (EnableWebViewProcessPriority)
        {
            processPriorityTask =
                MonitorWebViewProcessesAsync(
                    lifetimeCts.Token);
        }

        coreWebView.Navigate(
            "https://krunker.io/");
    }

private static string BuildBrowserArguments()
{
    // Stable rendering settings. Do not disable GPU vsync or Chromium's
    // frame-rate limiter while diagnosing click-time latency; those flags can
    // starve rendering/input/network scheduling when shooting effects appear.
    string[] arguments =
    [
        "--disable-background-timer-throttling",
        "--disable-renderer-backgrounding",
        "--disable-backgrounding-occluded-windows",
        "--enable-gpu-rasterization",
        "--enable-oop-rasterization",
        "--enable-zero-copy"
    ];

    return string.Join(
        ' ',
        arguments);
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
        // file URLs, or unsupported schemes.
        if (!Uri.TryCreate(
                e.Uri,
                UriKind.Absolute,
                out Uri? destination))
        {
            e.Cancel = true;
            return;
        }

        bool isHttp =
            destination.Scheme == Uri.UriSchemeHttp;

        bool isHttps =
            destination.Scheme == Uri.UriSchemeHttps;

        bool isAbout =
            string.Equals(
                destination.Scheme,
                "about",
                StringComparison.OrdinalIgnoreCase);

        e.Cancel = !(isHttp || isHttps || isAbout);
    }

    private async Task MonitorWebViewProcessesAsync(
        CancellationToken cancellationToken)
    {
        using var timer =
            new PeriodicTimer(
                TimeSpan.FromSeconds(3));

        try
        {
            do
            {
                ApplyWebViewProcessPriority();
            }
            while (await timer
                .WaitForNextTickAsync(
                    cancellationToken)
                .ConfigureAwait(false));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    private static void ApplyWebViewProcessPriority()
    {
        foreach (Process process in
                 Process.GetProcessesByName(
                     "msedgewebview2"))
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
                    // The process exited between enumeration
                    // and inspection.
                }
                catch (InvalidOperationException)
                {
                    // The process exited or metadata is unavailable.
                }
                catch (Win32Exception)
                {
                    // Access may be denied for another security context.
                }
            }
        }
    }

    private void ToggleFullscreen()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

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

        if (f11FallbackFilter is not null)
        {
            Application.RemoveMessageFilter(
                f11FallbackFilter);

            f11FallbackFilter = null;
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

    private sealed class F11MessageFilter : IMessageFilter
    {
        private readonly Form1 owner;

        public F11MessageFilter(Form1 owner)
        {
            this.owner = owner;
        }

        public bool PreFilterMessage(
            ref Message message)
        {
            const int WmKeyDown = 0x0100;
            const int WmSysKeyDown = 0x0104;

            bool isKeyDown =
                message.Msg == WmKeyDown ||
                message.Msg == WmSysKeyDown;

            int virtualKey =
                unchecked((int)message.WParam.ToInt64());

            if (!isKeyDown ||
                virtualKey != (int)Keys.F11)
            {
                return false;
            }

            long lParam =
                message.LParam.ToInt64();

            // Bit 30 indicates an auto-repeat keydown.
            bool wasAlreadyDown =
                (lParam & (1L << 30)) != 0;

            if (!wasAlreadyDown &&
                !owner.IsDisposed &&
                owner.IsHandleCreated)
            {
                owner.ToggleFullscreen();
            }

            // Prevent the browser from handling F11 as its own command.
            return true;
        }
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);
}
