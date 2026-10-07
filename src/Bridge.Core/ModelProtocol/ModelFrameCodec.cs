namespace Bridge.Core.ModelProtocol;

public static class ModelFrameCodec
{
    public const int FrameLength = 3;

    public static byte[] Encode(byte data) =>
    [
        ModelProtocolConstants.StartByte,
        data,
        Crc8.Compute(data)
    ];
}
