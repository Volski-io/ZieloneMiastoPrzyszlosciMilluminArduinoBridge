namespace Bridge.Core.ModelProtocol;

public sealed record ModelCommandDefinition(
    byte Data,
    string Name,
    string Category,
    string? Wire = null,
    string? Note = null,
    bool IsReserved = false,
    bool IsSendable = true,
    string? OscPath = null)
{
    public string DataHex => $"0x{Data:X2}";
    public byte[] Frame => ModelFrameCodec.Encode(Data);
    public string FrameHex => string.Join(' ', Frame.Select(value => value.ToString("X2")));
    public byte? ExpectedAcknowledgement =>
        ModelProtocolConstants.TryCreateAcknowledgement(Data, out var acknowledgement)
            ? acknowledgement
            : null;
    public string OscAddress => OscPath ?? $"/makieta/rezerwa/{Data:X2}";
    public string LegacyOscAddress => $"/bridge/model/command/{Data:X2}";
}
