using FarShell.Client;

try
{
    var endpoint = ClientEndpoint.Parse(args);
    using var consoleMode = ConsoleModeScope.EnableRawVirtualTerminalMode();

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        // ENABLE_PROCESSED_INPUT is disabled in the interactive case, so Ctrl+C
        // is normally read as 0x03. This is only a final guard against termination.
        eventArgs.Cancel = true;
    };

    var client = new RemoteTerminalClient(endpoint.Host, endpoint.Port);
    return await client.RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}
