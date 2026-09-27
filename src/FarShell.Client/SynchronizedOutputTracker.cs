namespace FarShell.Client;

// Recognize only the two exact mode-2026 markers outside OSC/DCS strings.
internal sealed class SynchronizedOutputTracker
{
    private static ReadOnlySpan<byte> Prefix => "\u001b[?2026"u8;
    private int _matched;
    private bool _escape, _inString, _osc, _stringEscape;
    internal bool IsActive { get; private set; }
    internal bool IsInsideString => _inString;

    internal bool Observe(byte value)
    {
        if (value is 24 or 26) // CAN / SUB cancel an unfinished control string.
        {
            _inString = _stringEscape = _escape = false;
            _matched = 0;
            return false;
        }

        if (_inString)
        {
            if ((_osc && value == 7) || (_stringEscape && value == '\\'))
            {
                _inString = false;
                _stringEscape = false;
            }
            else { _stringEscape = value == 27; }
            return false;
        }

        if (_escape && value is (byte)']' or (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_')
        {
            _inString = true;
            _osc = value == ']';
            _escape = false;
            _matched = 0;
            return false;
        }
        _escape = value == 27;

        if (_matched == Prefix.Length && value is (byte)'h' or (byte)'l')
        {
            IsActive = value == 'h';
            _matched = 0;
            return true;
        }

        _matched = _matched < Prefix.Length && value == Prefix[_matched] ? _matched + 1 : value == 27 ? 1 : 0;
        return false;
    }
}
