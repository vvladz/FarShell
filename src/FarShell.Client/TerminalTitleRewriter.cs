namespace FarShell.Client;

internal sealed class TerminalTitleRewriter
{
    internal const int MaximumTitleBytes = 4096;
    private static readonly byte[] Marker = "🛜 "u8.ToArray();
    private readonly List<byte> _pending = [];
    private State _state;
    private enum State { Text, Escape, Command, Title, TitleEscape, OtherOsc, OtherOscEscape, OtherString, OtherStringEscape }

    internal byte[] Transform(ReadOnlySpan<byte> bytes)
    {
        using var output = new MemoryStream(bytes.Length + Marker.Length);
        foreach (var value in bytes)
        {
            if (value is 24 or 26)
            {
                FlushRaw(output);
                output.WriteByte(value);
                _state = State.Text;
                continue;
            }

            switch (_state)
            {
                case State.Text:
                    if (value == 27) { _pending.Add(value); _state = State.Escape; }
                    else { output.WriteByte(value); }
                    break;
                case State.Escape:
                    if (value == 27) { output.WriteByte(27); break; }
                    _pending.Add(value);
                    if (value == ']') { _state = State.Command; }
                    else
                    {
                        FlushRaw(output);
                        _state = value is (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_'
                            ? State.OtherString : State.Text;
                    }
                    break;
                case State.Command:
                    _pending.Add(value);
                    if (_pending.Count == 3 && value is (byte)'0' or (byte)'2') { break; }
                    if (_pending.Count == 4 && value == ';') { _state = State.Title; break; }
                    FlushRaw(output);
                    _state = value == 7 ? State.Text : value == 27 ? State.OtherOscEscape : State.OtherOsc;
                    break;
                case State.Title:
                    _pending.Add(value);
                    if (value == 7) { FlushTitle(output, 1); }
                    else if (value == 27) { _state = State.TitleEscape; }
                    else if (_pending.Count > MaximumTitleBytes) { FlushRaw(output); _state = State.OtherOsc; }
                    break;
                case State.TitleEscape:
                    _pending.Add(value);
                    if (value == '\\') { FlushTitle(output, 2); }
                    else
                    {
                        FlushRaw(output);
                        _state = value == 7 ? State.Text : value == 27 ? State.OtherOscEscape : State.OtherOsc;
                    }
                    break;
                case State.OtherOsc:
                case State.OtherString:
                    output.WriteByte(value);
                    if (value == 7 && _state == State.OtherOsc) { _state = State.Text; }
                    else if (value == 27) { _state = _state == State.OtherOsc ? State.OtherOscEscape : State.OtherStringEscape; }
                    break;
                case State.OtherOscEscape:
                case State.OtherStringEscape:
                    output.WriteByte(value);
                    if (value == '\\' || (value == 7 && _state == State.OtherOscEscape)) { _state = State.Text; }
                    else if (value != 27) { _state = _state == State.OtherOscEscape ? State.OtherOsc : State.OtherString; }
                    break;
            }
        }

        return output.ToArray();
    }

    internal byte[] Complete()
    {
        var bytes = _pending.ToArray();
        _pending.Clear();
        _state = State.Text;
        return bytes;
    }

    private void FlushRaw(Stream output)
    {
        output.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending));
        _pending.Clear();
    }

    private void FlushTitle(Stream output, int terminatorLength)
    {
        var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pending);
        if (_pending.Count - terminatorLength > MaximumTitleBytes) { FlushRaw(output); }
        else
        {
            output.Write(data[..4]);
            output.Write(Marker);
            output.Write(data[4..]);
            _pending.Clear();
        }
        _state = State.Text;
    }
}
