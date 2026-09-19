using FarShell.Broker;

const int defaultPort = 8022;

try
{
    var port = args.Length switch
    {
        0 => defaultPort,
        1 when int.TryParse(args[0], out var value) && value is > 0 and <= 65535 => value,
        _ => throw new ArgumentException("Usage: [port]"),
    };

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    var server = new BrokerServer(port);
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
