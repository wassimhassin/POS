using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pos.Core.Bridge;
using Pos.Core.Catalog;
using Pos.Core.Data;
using Pos.Core.Hosting;
using Pos.Core.Logging;
using Pos.Core.Orders;

namespace Pos.Desktop;

/// <summary>Composition root: logging, database, bridge dispatcher, main window, global error handling.</summary>
public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private ILogger _log = NullLogger.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Must run before anything can create a WebView2 environment.
        var removedWebViewOverrides = HardenWebView2Environment();
        try
        {
            var paths = AppPaths.Resolve();
            Directory.CreateDirectory(paths.DataDir);
            _loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddProvider(new JsonFileLoggerProvider(paths.LogsDir)));
            _log = _loggerFactory.CreateLogger("Pos.App");
            RegisterGlobalErrorHandlers();

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            _log.LogInformation("Application starting {AppVersion} {ProcessId} {DataDir}", version, Environment.ProcessId, paths.DataDir);
            ReportWebView2Hardening(removedWebViewOverrides);

            var database = new PosDatabase(paths.DatabaseFile, _loggerFactory.CreateLogger<PosDatabase>());
            database.Initialize();

            var dispatcher = new BridgeDispatcher(
                new ProductRepository(database),
                new DraftOrderRepository(database),
                _loggerFactory);

            var window = new MainWindow(paths, dispatcher, UiLocation.Resolve(e.Args), _loggerFactory.CreateLogger<MainWindow>());
            MainWindow = window;
            window.Show();
        }
#pragma warning disable CA1031 // Startup must report any failure to the operator instead of crashing silently.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.LogCritical(ex, "Startup failed");
            MessageBox.Show(
                $"Le POS n'a pas pu démarrer.\n\n{ex.Message}",
                "POS — erreur de démarrage",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log.LogInformation("Application exiting {ExitCode}", e.ApplicationExitCode);
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }

    private static IReadOnlyList<string> HardenWebView2Environment()
    {
#if DEBUG
        // Debug builds keep WEBVIEW2_* variables: the E2E tests use them to attach over CDP.
        return [];
#else
        return WebView2EnvironmentGuard.RemoveProcessOverrides();
#endif
    }

    private void ReportWebView2Hardening(IReadOnlyList<string> removed)
    {
#if DEBUG
        var debugArgs = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
        if (WebView2EnvironmentGuard.ContainsDebuggingSwitch(debugArgs))
        {
            _log.LogWarning("Debug build: WebView2 remote debugging enabled by environment {BrowserArguments}", debugArgs);
        }
#else
        foreach (var name in removed)
        {
            _log.LogWarning("Removed WebView2 environment override {VariableName}", name);
        }

        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "Pos.Desktop.exe";
        if (WebView2Policy.ReadAdditionalBrowserArguments(exeName).Any(WebView2EnvironmentGuard.ContainsDebuggingSwitch))
        {
            throw new InvalidOperationException(
                "Une stratégie Windows (WebView2 AdditionalBrowserArguments) active le débogage distant. " +
                "Démarrage refusé pour des raisons de sécurité ; contactez l'administrateur du poste.");
        }
#endif
        _ = removed;
    }

    private void RegisterGlobalErrorHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.LogCritical(args.ExceptionObject as Exception, "Unhandled exception {IsTerminating}", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the POS running: persisted state lives in SQLite, and the error is logged for support.
        _log.LogError(e.Exception, "Unhandled UI thread exception");
        e.Handled = true;
    }
}
