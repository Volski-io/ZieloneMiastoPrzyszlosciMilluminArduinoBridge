namespace Bridge.Core.ModelProtocol;

public static class ModelProtocolConstants
{
    public const byte StartByte = 0x29;
    public const byte AckFailed = 0x01;
    public const byte NoScenario = 0x02;
    public const byte ScenarioRunning = 0x03;
    public const byte Scenario1 = 0x78;
    public const byte Scenario8 = 0x7F;
    public const byte ChecksumFailed = 0x80;

    public static bool TryGetScenario(byte data, out int scenario)
    {
        if (data is >= Scenario1 and <= Scenario8)
        {
            scenario = data - Scenario1 + 1;
            return true;
        }

        scenario = 0;
        return false;
    }

    public static bool TryCreateAcknowledgement(byte data, out byte acknowledgement)
    {
        if (data is >= 0x02 and <= 0x7F)
        {
            acknowledgement = (byte)(data | 0x80);
            return true;
        }

        acknowledgement = 0;
        return false;
    }

    public static bool TryGetAcknowledgedData(byte acknowledgement, out byte data)
    {
        if (acknowledgement is >= 0x82)
        {
            data = (byte)(acknowledgement & 0x7F);
            return true;
        }

        data = 0;
        return false;
    }
}
