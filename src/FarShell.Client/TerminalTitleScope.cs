namespace FarShell.Client;

internal sealed class TerminalTitleScope : IDisposable
{
    private readonly string? _original;

    internal TerminalTitleScope(string host)
    {
        try
        {
            _original = Console.Title;
            Console.Title = $"🛜 FarShell — {host}";
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        if (_original is null) { return; }
        try { Console.Title = _original; }
        catch (IOException) { }
    }
}
