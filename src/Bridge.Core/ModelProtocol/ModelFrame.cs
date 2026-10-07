namespace Bridge.Core.ModelProtocol;

public readonly record struct ModelFrame(
    byte Data,
    byte ReceivedChecksum,
    byte ExpectedChecksum)
{
    public bool IsValid => ReceivedChecksum == ExpectedChecksum;
}
