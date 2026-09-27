using FarShell.Broker;
using FarShell.Protocol;
using FarShell.Security;
using System.Security.Cryptography;

try
{
    if (args.Any(a => a is "--help" or "-h")) { Console.WriteLine(BrokerUsage.Text); return 0; }
    if (args is ["--version"])
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
        Console.WriteLine($"FarShell.Broker {version} (protocol {ProtocolPayloads.CurrentVersion})");
        return 0;
    }
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
    {
        throw new PlatformNotSupportedException("FarShell requires Windows 10 version 1809 or later.");
    }

    if (args is ["--send", .. var sendArgs])
    {
        return await SendCommand.RunAsync(sendArgs);
    }

    var options = BrokerOptions.Parse(args);
    var stateDirectory = options.StateDirectory ?? PrivateStorage.DefaultDirectory;
    if (options.Operation == BrokerOperation.Approve)
    {
        await PairingService.ApproveAsync(stateDirectory, options.RequestId!.Value);
        Console.WriteLine("Pairing request approved.");
        return 0;
    }
    if (options.Operation == BrokerOperation.Fingerprint)
    {
        using var saved = BrokerIdentity.Load(stateDirectory);
        Console.WriteLine($"SHA-256: {saved.Fingerprint}");
        return 0;
    }

    using var stateLock = BrokerIdentity.AcquireLock(stateDirectory);
    if (options.Operation is BrokerOperation.RotateKey or BrokerOperation.ReplaceCertificate)
    {
        using var saved = BrokerIdentity.Load(stateDirectory);
        if (options.Operation == BrokerOperation.RotateKey)
        {
            RandomNumberGenerator.Fill(saved.Key);
            saved.Save(stateDirectory);
            Console.WriteLine("API key rotated. All clients must pair again after the broker restarts.");
        }
        else
        {
            using var replacement = BrokerIdentity.Create();
            replacement.Save(stateDirectory);
            Console.WriteLine($"Certificate and API key replaced. SHA-256: {replacement.Fingerprint}\nAll clients must approve this fingerprint and pair again.");
        }
        return 0;
    }

    var profiles = SessionProfiles.Load(options.ConfigPath);
    using var identity = BrokerIdentity.LoadOrCreate(stateDirectory);
    Console.WriteLine($"Broker certificate SHA-256: {identity.Fingerprint}");
    await using var pairing = new PairingService(stateDirectory);
    pairing.Start();

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    var server = new BrokerServer(options, identity, profiles, pairing);
    try { await server.RunAsync(shutdown.Token); }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    return 0;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}\nRun FarShell.Broker.exe --help for usage.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}
