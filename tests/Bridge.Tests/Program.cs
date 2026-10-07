using Bridge.Core.Abstractions;
using Bridge.Core.Bridge;
using Bridge.Core.ModelProtocol;
using Bridge.Core.Osc;
using System.Collections.Concurrent;

namespace Bridge.Tests;

public static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("CRC8 matches reference vectors", TestCrc8Async),
        ("Frame parser handles noise and split input", TestFrameParserAsync),
        ("Frame parser reports invalid checksum", TestInvalidChecksumAsync),
        ("Scenario and ACK mappings match workbook", TestScenarioMappingsAsync),
        ("OSC codec round-trips supported values", TestOscRoundTripAsync),
        ("Coordinator ACKs and launches a scenario", TestCoordinatorScenarioAsync),
        ("Coordinator restores persisted state after OSC reconnect", TestCoordinatorRestoreAsync)
    ];

    public static async Task<int> Main()
    {
        var failures = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{Tests.Count - failures}/{Tests.Count} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestCrc8Async()
    {
        Equal((byte)0xF2, Crc8.Compute(0x26));
        Equal((byte)0x6F, Crc8.Compute(0x78));
        Equal((byte)0x68, Crc8.Compute(0x79));
        Equal((byte)0x7A, Crc8.Compute(0x7F));
        Equal((byte)0xE6, Crc8.Compute(0xF8));
        return Task.CompletedTask;
    }

    private static Task TestFrameParserAsync()
    {
        var parser = new ModelFrameParser();
        var frames = new List<ModelFrame>();
        parser.FrameParsed += frames.Add;

        parser.Feed([0x00, 0x55, 0x29, 0x29, 0x78]);
        Equal(0, frames.Count);
        parser.Feed([0x6F, 0x29, 0x79, 0x68]);

        Equal(2, frames.Count);
        True(frames.All(frame => frame.IsValid));
        Equal((byte)0x78, frames[0].Data);
        Equal((byte)0x79, frames[1].Data);
        return Task.CompletedTask;
    }

    private static Task TestInvalidChecksumAsync()
    {
        var parser = new ModelFrameParser();
        ModelFrame? parsed = null;
        parser.FrameParsed += frame => parsed = frame;
        parser.Feed([0x29, 0x78, 0x00]);

        var frame = parsed ?? throw new InvalidOperationException("Parser did not produce a frame.");
        True(!frame.IsValid);
        Equal((byte)0x6F, frame.ExpectedChecksum);
        return Task.CompletedTask;
    }

    private static Task TestScenarioMappingsAsync()
    {
        for (var scenario = 1; scenario <= 8; scenario++)
        {
            var data = (byte)(0x77 + scenario);
            True(ModelProtocolConstants.TryGetScenario(data, out var parsedScenario));
            Equal(scenario, parsedScenario);
            True(ModelProtocolConstants.TryCreateAcknowledgement(data, out var acknowledgement));
            Equal((byte)(0xF7 + scenario), acknowledgement);
        }

        return Task.CompletedTask;
    }

    private static Task TestOscRoundTripAsync()
    {
        var source = new OscMessage("/test", 42, "Scena 1", 0.5f, true, false);
        var decoded = OscPacketCodec.Decode(OscPacketCodec.Encode(source));

        Equal(1, decoded.Count);
        Equal(source.Address, decoded[0].Address);
        Equal(5, decoded[0].Arguments.Count);
        Equal(42, decoded[0].Arguments[0]);
        Equal("Scena 1", decoded[0].Arguments[1]);
        Equal(0.5f, decoded[0].Arguments[2]);
        Equal(true, decoded[0].Arguments[3]);
        Equal(false, decoded[0].Arguments[4]);
        return Task.CompletedTask;
    }

    private static async Task TestCoordinatorScenarioAsync()
    {
        var serial = new FakeSerialTransport();
        var osc = new FakeOscTransport();
        var store = new MemoryStateStore();
        var coordinator = new BridgeCoordinator(serial, osc, store, new TestLog(), FastOptions());
        using var cancellation = new CancellationTokenSource();
        var runTask = coordinator.RunAsync(cancellation.Token);

        serial.Emit(ModelFrameCodec.Encode(0x78));
        await WaitUntilAsync(() => serial.Sent.Any(frame => frame.SequenceEqual(ModelFrameCodec.Encode(0xF8))));
        await WaitUntilAsync(() => osc.Sent.Any(message => message.Address == "/action/launchColumn"));

        Equal(1, store.State.DesiredScenario);
        var launch = osc.Sent.Last(message => message.Address == "/action/launchColumn");
        Equal(1, launch.Arguments[0]);

        osc.Emit(new OscMessage("/millumin/board/launchedColumn", 1, "Scena 1"));
        await WaitUntilAsync(() => serial.Sent.Any(frame => frame.SequenceEqual(ModelFrameCodec.Encode(0x03))));
        Equal(1, store.State.ObservedScenario);

        cancellation.Cancel();
        await runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async Task TestCoordinatorRestoreAsync()
    {
        var serial = new FakeSerialTransport();
        var osc = new FakeOscTransport();
        var store = new MemoryStateStore { State = new BridgeState { DesiredScenario = 2 } };
        var coordinator = new BridgeCoordinator(serial, osc, store, new TestLog(), FastOptions());
        using var cancellation = new CancellationTokenSource();
        var runTask = coordinator.RunAsync(cancellation.Token);

        osc.Emit(new OscMessage("/millumin/info", "online"));
        await WaitUntilAsync(() => osc.Sent.Any(message =>
            message.Address == "/action/launchColumn" && Equals(message.Arguments[0], 2)), timeoutMs: 1000);

        cancellation.Cancel();
        await runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static BridgeCoordinatorOptions FastOptions() => new()
    {
        ScenarioColumns = Enumerable.Range(1, 8)
            .ToDictionary(number => number, number => new ColumnTarget { Index = number }),
        DuplicateInputWindow = TimeSpan.FromMilliseconds(20),
        ModelAckTimeout = TimeSpan.FromMilliseconds(30),
        ModelRetryWindow = TimeSpan.FromMilliseconds(80),
        TickInterval = TimeSpan.FromMilliseconds(2),
        MilluminPingInterval = TimeSpan.FromMilliseconds(100),
        MilluminOfflineAfter = TimeSpan.FromMilliseconds(500),
        StartupReconcileDelay = TimeSpan.FromMilliseconds(15),
        RestoreLastScenario = true
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(5);
        }
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected condition to be true.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private sealed class FakeSerialTransport : ISerialTransport
    {
        public bool IsConnected { get; set; } = true;
        public ConcurrentQueue<byte[]> Sent { get; } = new();
        public event Action<ReadOnlyMemory<byte>>? BytesReceived;
        public event Action<bool, string?>? ConnectionChanged;
        public Task RunAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Sent.Enqueue(bytes.ToArray());
            return ValueTask.CompletedTask;
        }

        public void Emit(byte[] bytes) => BytesReceived?.Invoke(bytes);
        public void EmitConnection(bool connected) => ConnectionChanged?.Invoke(connected, "fake");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOscTransport : IOscTransport
    {
        public ConcurrentQueue<OscMessage> Sent { get; } = new();
        public event Action<OscMessage>? MessageReceived;
        public Task RunAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
        public ValueTask SendAsync(OscMessage message, CancellationToken cancellationToken)
        {
            Sent.Enqueue(message);
            return ValueTask.CompletedTask;
        }

        public void Emit(OscMessage message) => MessageReceived?.Invoke(message);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryStateStore : IStateStore
    {
        public BridgeState State { get; set; } = new();
        public ValueTask<BridgeState> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(State);
        public ValueTask SaveAsync(BridgeState state, CancellationToken cancellationToken)
        {
            State = state;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestLog : IBridgeLog
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public void Debug(string message) { }
    }
}
