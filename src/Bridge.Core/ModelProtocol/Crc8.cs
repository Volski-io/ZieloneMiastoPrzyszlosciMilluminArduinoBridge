namespace Bridge.Core.ModelProtocol;

public static class Crc8
{
    public const byte Polynomial = 0x07;
    public const byte InitialValue = 0x00;

    public static byte Compute(byte data)
    {
        var crc = (byte)(InitialValue ^ data);

        for (var bit = 0; bit < 8; bit++)
        {
            crc = (byte)((crc & 0x80) != 0
                ? (crc << 1) ^ Polynomial
                : crc << 1);
        }

        return crc;
    }

    public static bool IsValid(byte data, byte crc) => crc == Compute(data);
}
