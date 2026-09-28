using FarShell.Client;
using FarShell.Security;
using FarShell.Protocol;

try
{
    var options = ClientOptions.Parse(args, Environment.GetEnvironmentVariable("FARSHELL_SERVER"),
        Environment.GetEnvironmentVariable("FARSHELL_OUTPUT_BATCH_MS"));
    if (options.Operation == ClientOperation.Help) { Console.WriteLine(ClientUsage.Text); return 0; }
    if (options.Operation == ClientOperation.Version)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
        Console.WriteLine($"FarShell.Client {version} (protocol {ProtocolPayloads.CurrentVersion})");
        return 0;
    }
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
    {
        throw new PlatformNotSupportedException("FarShell requires Windows 10 version 1809 or later.");
    }

    var client = new RemoteTerminalClient(
        options.Endpoint.Host,
        options.Endpoint.Port,
        options.OutputBatchDelay, options.ProfileName, options.StateDirectory);
    if (options.Operation == ClientOperation.Pair) { return await client.PairAsync(); }
    if (options.Operation == ClientOperation.Trust)
    {
        await ClientConnection.TrustAsync(options.Endpoint.Host, options.Endpoint.Port,
            options.StateDirectory ?? PrivateStorage.DefaultDirectory, options.Fingerprint);
        Console.WriteLine("Broker certificate trusted.");
        return 0;
    }
    if (!options.IsInteractive)
    {
        return options.Operation switch
        {
            ClientOperation.Upload => await client.UploadAsync(
                options.LocalPath!,
                options.RemotePath!),
            ClientOperation.Download => await client.DownloadAsync(
                options.RemotePath!,
                options.LocalPath!),
            _ => throw new InvalidOperationException("Unsupported non-interactive operation."),
        };
    }

    using var consoleMode = ConsoleModeScope.EnableVirtualTerminalOutputMode();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        // ENABLE_PROCESSED_INPUT is disabled in the interactive case, so Ctrl+C
        // is normally read as 0x03. This is only a final guard against termination.
        eventArgs.Cancel = true;
    };

    return await client.CreateAsync();
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    Console.Error.WriteLine("Run FarShell.Client.exe --help for usage.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}
