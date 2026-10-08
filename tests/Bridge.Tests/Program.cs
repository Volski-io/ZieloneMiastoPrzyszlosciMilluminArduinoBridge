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
        ("Command catalog covers every byte", TestCommandCatalogAsync),
        ("OSC codec round-trips supported values", TestOscRoundTripAsync),
        ("Readable OSC command address sends a UART frame", TestOscModelCommandAsync),
        ("Parameterized actuator OSC queues speed then start", TestOscSpeedSequencesAsync),
        ("Parameterized fan and balloon ranges queue every element", TestOscNumberedRangesAsync),
        ("Parameterized sector OSC covers every sector-based device and range", TestOscSectorSequencesAsync),
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

    private static Task TestCommandCatalogAsync()
    {
        Equal(256, ModelCommandCatalog.All.Count);
        Equal("29 54 AB", ModelCommandCatalog.Get(0x54).FrameHex);
        Equal("/makieta/sektory/4/przelacz", ModelCommandCatalog.Get(0x54).OscAddress);
        Equal("Wyczyść wybór sektorów", ModelCommandCatalog.Get(0x59).Name);
        Equal("/makieta/sektory/wyczysc", ModelCommandCatalog.Get(0x59).OscAddress);
        Equal("Biurowiec 1 off", ModelCommandCatalog.Get(0x3C).Name);
        Equal("/makieta/obiekty/elektrownia-zlotniki-off", ModelCommandCatalog.Get(0x76).OscAddress);
        Equal("/makieta/obiekty/elektrownia-zlotniki-on", ModelCommandCatalog.Get(0x77).OscAddress);
        True(ModelCommandCatalog.All
            .Where(command => command.IsSendable && !command.IsReserved)
            .All(command => command.OscAddress.All(character => character <= 0x7F)));
        True(!ModelCommandCatalog.Get(0x29).IsSendable);
        Equal("ACK 0x78: Wciśnięto scenariusz 1", ModelCommandCatalog.Get(0xF8).Name);
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

    private static async Task TestOscModelCommandAsync()
    {
        var serial = new FakeSerialTransport();
        var osc = new FakeOscTransport();
        var coordinator = new BridgeCoordinator(serial, osc, new MemoryStateStore(), new TestLog(), FastOptions());
        using var cancellation = new CancellationTokenSource();
        var runTask = coordinator.RunAsync(cancellation.Token);

        osc.Emit(new OscMessage("/makieta/sektory/4/przelacz"));
        await WaitUntilAsync(() => serial.Sent.Any(frame => frame.SequenceEqual(ModelFrameCodec.Encode(0x54))));

        cancellation.Cancel();
        await runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async Task TestOscSpeedSequencesAsync()
    {
        await AssertCommandSequenceAsync("/makieta/balon/2/predkosc/70", [0x16, 0x1D]);
        await AssertCommandSequenceAsync("/makieta/wiatrak/5/predkosc/70", [0x16, 0x0D]);
        await AssertCommandSequenceAsync("/makieta/obiekty/chmura-pompka/predkosc/10", [0x10, 0x4F]);
        await AssertCommandSequenceAsync("/makieta/obiekty/chmura-pompka/predkosc/40", [0x13, 0x4F]);
        await AssertCommandSequenceAsync("/makieta/obiekty/chmura-pompka/predkosc/100", [0x19, 0x4F]);
        await AssertCommandSequenceAsync("/makieta/transport/pociag/predkosc/10", [0x10, 0x4B]);
        await AssertCommandSequenceAsync("/makieta/transport/pociag/predkosc/40", [0x13, 0x4B]);
        await AssertCommandSequenceAsync("/makieta/transport/pociag/predkosc/100", [0x19, 0x4B]);

        var validator = new BridgeCoordinator(
            new FakeSerialTransport(),
            new FakeOscTransport(),
            new MemoryStateStore(),
            new TestLog(),
            FastOptions());
        True(!validator.TryQueueOscModelCommand("/makieta/obiekty/chmura-pompka/predkosc/45"));
        True(!validator.TryQueueOscModelCommand("/makieta/transport/pociag/predkosc/45"));
    }

    private static async Task TestOscNumberedRangesAsync()
    {
        var validator = new BridgeCoordinator(
            new FakeSerialTransport(),
            new FakeOscTransport(),
            new MemoryStateStore(),
            new TestLog(),
            FastOptions());
        True(!validator.TryQueueOscModelCommand("/makieta/wiatraki/1-6/off"));
        True(!validator.TryQueueOscModelCommand("/makieta/wiatraki/3-3/off"));
        True(!validator.TryQueueOscModelCommand("/makieta/balony/3-1/predkosc/70"));
        True(!validator.TryQueueOscModelCommand("/makieta/balony/1-3/predkosc/75"));

        await AssertCommandSequenceAsync("/makieta/wiatraki/1-5/predkosc/70", [0x16, 0x05, 0x07, 0x09, 0x0B, 0x0D]);
        await AssertCommandSequenceAsync("/makieta/wiatraki/2-4/off", [0x06, 0x08, 0x0A]);
        await AssertCommandSequenceAsync("/makieta/balony/1-3/predkosc/40", [0x13, 0x1B, 0x1D, 0x1F]);
        await AssertCommandSequenceAsync("/makieta/balony/1-3/off", [0x1A, 0x1C, 0x1E]);
    }

    private static async Task TestOscSectorSequencesAsync()
    {
        var validator = new BridgeCoordinator(
            new FakeSerialTransport(),
            new FakeOscTransport(),
            new MemoryStateStore(),
            new TestLog(),
            FastOptions());
        True(!validator.TryQueueOscModelCommand("/makieta/magazyn-energii/rgb/sektor/3/on"));
        True(!validator.TryQueueOscModelCommand("/makieta/budynek/1/sektor/7/on"));
        True(!validator.TryQueueOscModelCommand("/makieta/slupy/sektor/7/on"));
        True(!validator.TryQueueOscModelCommand("/makieta/slupy/sektory/4-4/on"));
        True(!validator.TryQueueOscModelCommand("/makieta/slupy/sektory/6-2/on"));

        (string Address, int MaxSector, byte OnCommand)[] routes =
        [
            ("/makieta/slupy/sektor/6/on", 6, 0x21),
            ("/makieta/balony/led/sektor/3/on", 3, 0x23),
            ("/makieta/zabudowa-mieszkaniowa/0/sektor/3/on", 3, 0x5F),
            ("/makieta/budynek/1/sektor/6/on", 6, 0x61),
            ("/makieta/budynek/2/sektor/7/on", 7, 0x63),
            ("/makieta/budynek/3/sektor/8/on", 8, 0x65),
            ("/makieta/hotel/1/sektor/3/on", 3, 0x67),
            ("/makieta/zabudowa-mieszkaniowa/1/sektor/3/on", 3, 0x6B),
            ("/makieta/farma-fotowoltaiczna/1/rgb/sektor/3/on", 3, 0x6F),
            ("/makieta/magazyn-energii/rgb/sektor/2/on", 2, 0x73)
        ];

        foreach (var route in routes)
        {
            await AssertSectorSequenceAsync(route.Address, route.MaxSector, route.OnCommand);

            var rangeAddress = route.Address
                .Replace("/sektor/", "/sektory/", StringComparison.Ordinal)
                .Replace($"/{route.MaxSector}/on", $"/1-{route.MaxSector}/on", StringComparison.Ordinal);
            var expected = new List<byte> { 0x59 };
            expected.AddRange(Enumerable.Range(1, route.MaxSector).Select(sector => (byte)(0x50 + sector)));
            expected.Add(route.OnCommand);
            await AssertCommandSequenceAsync(rangeAddress, expected);
        }
    }

    private static async Task AssertSectorSequenceAsync(string address, int sector, byte onCommand)
    {
        var selectionCommand = (byte)(0x50 + sector);
        await AssertCommandSequenceAsync(address, [0x59, selectionCommand, onCommand]);
    }

    private static async Task AssertCommandSequenceAsync(string address, IReadOnlyList<byte> expectedCommands)
    {
        var serial = new FakeSerialTransport();
        var osc = new FakeOscTransport();
        var coordinator = new BridgeCoordinator(serial, osc, new MemoryStateStore(), new TestLog(), FastOptions());
        using var cancellation = new CancellationTokenSource();
        var runTask = coordinator.RunAsync(cancellation.Token);

        osc.Emit(new OscMessage(address));
        foreach (var command in expectedCommands)
        {
            await WaitUntilAsync(() => ContainsFrame(serial, command));
            True(ModelProtocolConstants.TryCreateAcknowledgement(command, out var acknowledgement));
            serial.Emit(ModelFrameCodec.Encode(acknowledgement));
        }

        var sent = serial.Sent.ToArray();
        for (var index = 1; index < expectedCommands.Count; index++)
        {
            True(IndexOfFrame(sent, expectedCommands[index - 1]) < IndexOfFrame(sent, expectedCommands[index]));
        }

        cancellation.Cancel();
        await runTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static bool ContainsFrame(FakeSerialTransport serial, byte data) =>
        serial.Sent.Any(frame => frame.SequenceEqual(ModelFrameCodec.Encode(data)));

    private static int IndexOfFrame(IReadOnlyList<byte[]> frames, byte data)
    {
        var expected = ModelFrameCodec.Encode(data);
        for (var index = 0; index < frames.Count; index++)
        {
            if (frames[index].SequenceEqual(expected))
            {
                return index;
            }
        }

        return -1;
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
