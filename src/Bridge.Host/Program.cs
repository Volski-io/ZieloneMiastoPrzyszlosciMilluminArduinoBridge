using Bridge.Core.Bridge;
using Bridge.Host.Configuration;
using Bridge.Host.Logging;
using Bridge.Host.Osc;
using Bridge.Host.Persistence;
using Bridge.Host.Serial;

namespace Bridge.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
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
                cancellation.Cancel();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellation.Cancel();

            await using var serial = new SerialPortTransport(options.Serial, log);
            await using var osc = new UdpOscTransport(options.Millumin, log);
            var stateStore = new JsonStateStore(options.State.FilePath, log);
            var coordinator = new BridgeCoordinator(serial, osc, stateStore, log, options.ToCoordinatorOptions());

            log.Info($"Starting bridge with configuration '{Path.GetFullPath(configPath)}'.");
            var tasks = new[]
            {
                serial.RunAsync(cancellation.Token),
                osc.RunAsync(cancellation.Token),
                coordinator.RunAsync(cancellation.Token)
            };

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
}
