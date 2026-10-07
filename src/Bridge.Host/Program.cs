using Bridge.Core.Bridge;
using Bridge.Host.Configuration;
using Bridge.Host.Logging;
using Bridge.Host.Osc;
using Bridge.Host.Persistence;
using Bridge.Host.Serial;
using Bridge.Host.Telemetry;
using Bridge.Host.Web;

namespace Bridge.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            name: "MilluminArduinoBridge.SingleInstance",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            Console.Error.WriteLine(
                "Bridge jest już uruchomiony. Zatrzymaj poprzednią instancję (Ctrl+C) przed ponownym startem.");
            return 2;
        }

        var configPath = GetArgument(args, "--config")
            ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var validateOnly = args.Contains("--validate-config", StringComparer.OrdinalIgnoreCase);

        try
        {
            var options = await ConfigurationLoader.LoadAsync(configPath, CancellationToken.None);
            if (validateOnly)
            {
                Console.WriteLine($"Configuration is valid: {Path.GetFullPath(configPath)}");
                return 0;
            }

            var log = new ConsoleBridgeLog(options.Logging.Debug);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                TryCancel(cancellation);
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryCancel(cancellation);

            var traffic = new TrafficJournal();
            await using var serial = new SerialPortTransport(options.Serial, log, traffic);
            await using var osc = new UdpOscTransport(options.Millumin, log, traffic);
            var stateStore = new JsonStateStore(options.State.FilePath, log);
            var coordinator = new BridgeCoordinator(serial, osc, stateStore, log, options.ToCoordinatorOptions());

            log.Info($"Starting bridge with configuration '{Path.GetFullPath(configPath)}'.");
            var tasks = new List<Task>
            {
                serial.RunAsync(cancellation.Token),
                osc.RunAsync(cancellation.Token),
                coordinator.RunAsync(cancellation.Token)
            };

            if (options.Web.Enabled)
            {
                tasks.Add(WebPanel.RunAsync(options, serial, osc, coordinator, traffic, cancellation.Token));
                log.Info($"Test panel: {options.Web.ListenUrl}");
            }

            var completed = await Task.WhenAny(tasks);
            if (!cancellation.IsCancellationRequested)
            {
                await completed;
                throw new InvalidOperationException("A bridge service stopped unexpectedly.");
            }

            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Bridge startup failed: {exception}");
            return 1;
        }
    }

    private static string? GetArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
