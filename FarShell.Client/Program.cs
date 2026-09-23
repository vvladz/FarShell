using FarShell.Client;

try
{
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
    {
        throw new PlatformNotSupportedException("FarShell requires Windows 10 version 1809 or later.");
    }

    var options = ClientOptions.Parse(
        args,
        Environment.GetEnvironmentVariable("FARSHELL_SERVER"),
        Environment.GetEnvironmentVariable("FARSHELL_OUTPUT_BATCH_MS"));
    var client = new RemoteTerminalClient(
        options.Endpoint.Host,
        options.Endpoint.Port,
        options.OutputBatchDelay);
    if (!options.IsInteractive)
    {
        return options.Operation switch
        {
            ClientOperation.List => await client.ListAsync(),
            ClientOperation.Terminate => await client.TerminateAsync(options.SessionId!.Value),
            _ => throw new InvalidOperationException("Unsupported non-interactive operation."),
        };
    }

    using var consoleMode = ConsoleModeScope.EnableRawVirtualTerminalMode();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        // ENABLE_PROCESSED_INPUT is disabled in the interactive case, so Ctrl+C
        // is normally read as 0x03. This is only a final guard against termination.
        eventArgs.Cancel = true;
    };

    return options.Operation == ClientOperation.Create
        ? await client.CreateAsync()
        : await client.AttachAsync(options.SessionId!.Value);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}
