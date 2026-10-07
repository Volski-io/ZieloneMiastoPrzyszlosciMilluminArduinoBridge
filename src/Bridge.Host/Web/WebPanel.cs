using System.Globalization;
using System.IO.Ports;
using Bridge.Core.Bridge;
using Bridge.Core.ModelProtocol;
using Bridge.Core.Osc;
using Bridge.Host.Configuration;
using Bridge.Host.Osc;
using Bridge.Host.Serial;
using Bridge.Host.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Bridge.Host.Web;

public static class WebPanel
{
    public static async Task RunAsync(
        AppOptions options,
        SerialPortTransport serial,
        UdpOscTransport osc,
        BridgeCoordinator coordinator,
        TrafficJournal traffic,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(options.Web.ListenUrl);
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.WriteIndented = false);

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/status", () =>
        {
            var lastOscReceivedAt = traffic.LastOscReceivedAt;
            var oscOnline = lastOscReceivedAt.HasValue
                && DateTimeOffset.UtcNow - lastOscReceivedAt.Value
                    <= TimeSpan.FromMilliseconds(options.Reliability.MilluminOfflineAfterMs);

            return Results.Ok(new
            {
                serialConnected = traffic.SerialConnected,
                serialPort = traffic.SerialPort,
                availablePorts = SerialPort.GetPortNames().OrderBy(port => port).ToArray(),
                baudRate = options.Serial.BaudRate,
                oscTarget = $"{options.Millumin.Host}:{options.Millumin.InputPort}",
                oscFeedbackPort = options.Millumin.FeedbackPort,
                oscOnline,
                lastOscReceivedAt,
                webUrl = options.Web.ListenUrl
            });
        });

        app.MapPost("/api/osc/ping", async (CancellationToken requestCancellation) =>
        {
            await osc.SendAsync(new OscMessage("/ping"), requestCancellation);
            traffic.RecordSystem(
                "Ręczny test OSC /ping",
                $"Wysłano do {options.Millumin.Host}:{options.Millumin.InputPort}; oczekiwanie na feedback UDP {options.Millumin.FeedbackPort}.");
            return Results.Accepted(value: new { sent = true });
        });

        app.MapPost("/api/osc/model", (OscModelCommandRequest request) =>
        {
            if (!serial.IsConnected)
            {
                return Results.Conflict(new { error = "Port szeregowy nie jest połączony." });
            }

            if (!coordinator.TryQueueOscModelCommand(request.Address))
            {
                return Results.BadRequest(new { error = "Nieprawidłowa lub nieobsługiwana ścieżka OSC makiety." });
            }

            return Results.Accepted(value: new { address = request.Address });
        });

        app.MapGet("/api/commands", () => Results.Ok(ModelCommandCatalog.All.Select(command => new
        {
            data = command.Data,
            dataHex = command.DataHex,
            command.Name,
            command.Category,
            command.Wire,
            command.Note,
            command.IsReserved,
            command.IsSendable,
            command.FrameHex,
            expectedAck = command.IsSendable && command.ExpectedAcknowledgement is byte acknowledgement
                ? $"0x{acknowledgement:X2}"
                : null,
            oscAddress = command.OscAddress,
            oscRelation = DescribeOscRelation(command, options)
        })));

        app.MapGet("/api/logs", (long? after) => Results.Ok(traffic.GetAfter(after ?? 0)));

        app.MapPost("/api/logs/clear", () =>
        {
            traffic.Clear();
            return Results.NoContent();
        });

        app.MapPost("/api/commands/{value}/send", (string value) =>
        {
            value = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
            if (!byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var data))
            {
                return Results.BadRequest(new { error = "Podaj jeden bajt w formacie hex, np. 54." });
            }

            var command = ModelCommandCatalog.Get(data);
            if (!command.IsSendable)
            {
                return Results.BadRequest(new { error = command.Note ?? "Tej pozycji nie można wysłać jako DATA." });
            }

            if (!serial.IsConnected)
            {
                return Results.Conflict(new { error = "Port szeregowy nie jest połączony." });
            }

            if (!coordinator.TryQueueModelCommand(data))
            {
                return Results.Conflict(new { error = "Bridge nie przyjął polecenia." });
            }

            return Results.Accepted(value: new
            {
                command = command.DataHex,
                command.Name,
                command.FrameHex
            });
        });

        traffic.RecordSystem("Panel WWW uruchomiony", options.Web.ListenUrl);
        await app.StartAsync(cancellationToken);
        try
        {
            await app.WaitForShutdownAsync(cancellationToken);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static string DescribeOscRelation(ModelCommandDefinition command, AppOptions options)
    {
        if (ModelProtocolConstants.TryGetScenario(command.Data, out var scenario))
        {
            var target = options.Scenarios.First(item => item.Scenario == scenario).Column;
            return $"UART → OSC: /action/launchColumn [{target.Name ?? target.Index?.ToString() ?? "?"}]";
        }

        return command.Data switch
        {
            ModelProtocolConstants.NoScenario => "Feedback Millumin stoppedColumn → UART 0x02",
            ModelProtocolConstants.ScenarioRunning => "Feedback Millumin launchedColumn → UART 0x03",
            >= 0x82 => $"Automatyczny ACK dla DATA 0x{command.Data & 0x7F:X2}",
            _ => $"OSC → UART: {command.OscAddress} (bez argumentów)"
        };
    }

    private sealed record OscModelCommandRequest(string Address);
}
