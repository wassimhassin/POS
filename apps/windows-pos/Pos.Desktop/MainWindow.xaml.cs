using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Pos.Core.Bridge;

namespace Pos.Desktop;

/// <summary>
/// Hosts the React UI in WebView2 and carries bridge messages to <see cref="BridgeDispatcher"/>.
/// Security: navigation and messages are accepted only from the trusted UI origin; no host objects,
/// no new windows, dev tools and browser shortcuts only in Debug builds.
/// </summary>
public partial class MainWindow : Window
{
#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private readonly AppPaths _paths;
    private readonly BridgeDispatcher _dispatcher;
    private readonly Uri _startUri;
    private readonly ILogger _log;
    private readonly HashSet<string> _allowedOrigins;

    internal MainWindow(AppPaths paths, BridgeDispatcher dispatcher, Uri startUri, ILogger<MainWindow> logger)
    {
        _paths = paths;
        _dispatcher = dispatcher;
        _startUri = startUri;
        _log = logger;
        _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BridgeOrigin.GetOrigin(startUri.AbsoluteUri) ?? throw new ArgumentException("Invalid UI URI.", nameof(startUri)),
        };

        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            _log.LogCritical(ex, "WebView2 runtime not installed");
            ShowError("Composant WebView2 manquant", "Installez Microsoft Edge WebView2 Runtime puis relancez le POS.");
        }
#pragma warning disable CA1031 // async void event handler: every failure must be surfaced, not crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogCritical(ex, "WebView2 initialization failed");
            ShowError("Impossible d'afficher l'interface", ex.Message);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (UiLocation.IsPackaged(_startUri) && !File.Exists(Path.Combine(_paths.WebRoot, "index.html")))
        {
            _log.LogCritical("Packaged UI missing at {WebRoot}", _paths.WebRoot);
            ShowError("Interface introuvable", $"Le dossier de l'interface est absent : {_paths.WebRoot}. Exécutez scripts\\build.ps1.");
            return;
        }

        var environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _paths.WebViewDataDir);
        await WebView.EnsureCoreWebView2Async(environment);
        var core = WebView.CoreWebView2;

        ConfigureSettings(core.Settings);
        core.SetVirtualHostNameToFolderMapping(UiLocation.VirtualHost, _paths.WebRoot, CoreWebView2HostResourceAccessKind.Deny);

        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;

        _log.LogInformation("WebView2 ready {BrowserVersion} {StartUri}", environment.BrowserVersionString, _startUri);
        core.Navigate(_startUri.AbsoluteUri);
    }

    private static void ConfigureSettings(CoreWebView2Settings settings)
    {
        settings.IsWebMessageEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.IsScriptEnabled = true;
        settings.AreDevToolsEnabled = IsDebugBuild;
        settings.AreDefaultContextMenusEnabled = IsDebugBuild;
        settings.AreBrowserAcceleratorKeysEnabled = IsDebugBuild;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsBuiltInErrorPageEnabled = true;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!BridgeOrigin.IsAllowed(e.Uri, _allowedOrigins))
        {
            e.Cancel = true;
            _log.LogWarning("Blocked navigation to untrusted URI {Uri}", e.Uri);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _log.LogInformation("UI loaded {NavigationId}", e.NavigationId);
        }
        else
        {
            _log.LogWarning("UI navigation failed {WebErrorStatus} {HttpStatusCode}", e.WebErrorStatus, e.HttpStatusCode);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // never open popups or external windows
        _log.LogWarning("Blocked new window request {Uri}", e.Uri);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!BridgeOrigin.IsAllowed(e.Source, _allowedOrigins))
            {
                _log.LogWarning("Rejected bridge message from untrusted origin {Source}", e.Source);
                return;
            }

            var request = e.WebMessageAsJson;
            var response = await Task.Run(() => _dispatcher.Handle(request));
            WebView.CoreWebView2?.PostWebMessageAsJson(response);
        }
#pragma warning disable CA1031 // async void event handler: log transport failures instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogError(ex, "Bridge transport failure");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.LogError("WebView2 process failed {ProcessFailedKind} {Reason} {ExitCode}", e.ProcessFailedKind, e.Reason, e.ExitCode);
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                // The draft is persisted in SQLite, so reloading restores the cart.
                WebView.CoreWebView2?.Reload();
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                ShowError("Le moteur d'affichage s'est arrêté", "Fermez puis relancez le POS. La commande en cours est conservée.");
                break;
            default:
                break;
        }
    }

    private void ShowError(string title, string body)
    {
        ErrorTitle.Text = title;
        ErrorBody.Text = body;
        WebView.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}
