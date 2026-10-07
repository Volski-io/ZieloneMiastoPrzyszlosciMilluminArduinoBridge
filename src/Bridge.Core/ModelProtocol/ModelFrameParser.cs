namespace Bridge.Core.ModelProtocol;

public sealed class ModelFrameParser
{
    private ParserState _state;
    private byte _data;

    public event Action<ModelFrame>? FrameParsed;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            switch (_state)
            {
                case ParserState.WaitingForStart:
                    if (value == ModelProtocolConstants.StartByte)
                    {
                        _state = ParserState.WaitingForData;
                    }
                    break;

                case ParserState.WaitingForData:
                    if (value == ModelProtocolConstants.StartByte)
                    {
                        // DATA=0x29 is reserved. A repeated marker restarts framing.
                        break;
                    }

                    _data = value;
                    _state = ParserState.WaitingForChecksum;
                    break;

                case ParserState.WaitingForChecksum:
                    var frame = new ModelFrame(_data, value, Crc8.Compute(_data));
                    _state = ParserState.WaitingForStart;
                    FrameParsed?.Invoke(frame);
                    break;
            }
        }
    }

    public void Reset()
    {
        _state = ParserState.WaitingForStart;
        _data = 0;
    }

    private enum ParserState
    {
        WaitingForStart,
        WaitingForData,
        WaitingForChecksum
    }
}
