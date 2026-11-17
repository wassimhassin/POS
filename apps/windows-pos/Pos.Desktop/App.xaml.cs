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
using Pos.Core.Printing;

namespace Pos.Desktop;

/// <summary>Composition root: logging, database, bridge dispatcher, main window, global error handling.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Application lifetime: disposed in OnExit.")]
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private ILoggerFactory? _loggerFactory;
    private ILogger _log = NullLogger.Instance;
    private PrintWorker? _printWorker;
    private Task? _printWorkerTask;

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

            var drafts = new DraftOrderRepository(database);
            var printSettings = new PrintSettingsRepository(database);
            var printQueue = new PrintQueue(database, _loggerFactory.CreateLogger<PrintQueue>());
            var recovered = printQueue.RecoverInterrupted();
            if (recovered > 0)
            {
                _log.LogWarning("{JobCount} print jobs were interrupted by the previous shutdown and need operator review", recovered);
            }

            var printerGateway = new WindowsSpoolerPrinter();
            _printWorker = new PrintWorker(printQueue, printSettings, printerGateway, _loggerFactory.CreateLogger<PrintWorker>());
            var printing = new PrintService(database, drafts, printSettings, printQueue, printerGateway, _printWorker.Signal);
            _printWorkerTask = Task.Run(() => _printWorker.RunAsync(_shutdown.Token));

            var dispatcher = new BridgeDispatcher(
                new ProductRepository(database),
                drafts,
                printing,
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
        _shutdown.Cancel();
        // Let an in-flight spooler call finish so its outcome is recorded (otherwise it becomes 'uncertain' on restart).
        if (_printWorkerTask is not null && !_printWorkerTask.Wait(TimeSpan.FromSeconds(5)))
        {
            _log.LogWarning("Print worker did not stop within 5 s");
        }

        _printWorker?.Dispose();
        _shutdown.Dispose();
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
