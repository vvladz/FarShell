using FarShell.Broker;

try
{
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
    {
        throw new PlatformNotSupportedException("FarShell requires Windows 10 version 1809 or later.");
    }

    var options = BrokerOptions.Parse(args);

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    var server = new BrokerServer(options);
    await server.RunAsync(shutdown.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}
