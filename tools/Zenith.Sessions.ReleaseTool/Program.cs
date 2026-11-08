using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Windows;

using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    if (args.Length == 3 && args[0] == "keygen")
    {
        string privatePath = Path.GetFullPath(args[1]), publicPath = Path.GetFullPath(args[2]);
        if (StringComparer.OrdinalIgnoreCase.Equals(privatePath, publicPath)) throw new ArgumentException("Private and public output paths must differ.");
        for (string? folder = Path.GetDirectoryName(privatePath); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private-key output cannot use redirected directories.");
            if (Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"))) throw new InvalidOperationException("Keep private signing keys outside every repository.");
        }
        if (File.Exists(privatePath) || File.Exists(publicPath)) throw new IOException("Key output already exists.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // Encryption requires an operator-supplied passphrase; it is read only from the console, never arguments or logs.
        Console.Write("Private-key passphrase (at least 16 characters): ");
        string password = ReadPassword();
        if (password.Length < 16) throw new ArgumentException("Use at least 16 characters.");
        string pem = key.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 300000));
        using (var file = new FileStream(privatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file)) writer.Write(pem);
        using (var file = new FileStream(publicPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file)) await writer.WriteAsync(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()).AsMemory(), stop.Token);
    }
    else if (args.Length == 5 && args[0] == "sign")
    {
        byte[] manifest = await File.ReadAllBytesAsync(args[2], stop.Token);
        if (manifest.Length is 0 or > 32768) throw new InvalidDataException("Manifest exceeds its bound.");
        var document = JsonSerializer.Deserialize<UpdateManifest>(manifest) ?? throw new InvalidDataException("Invalid manifest.");
        if (!document.ReleaseReady || document.Product != SignedUpdateVerifier.Product) throw new InvalidDataException("Preview manifests cannot be signed as releases.");
        Console.Write("Private-key passphrase: ");
        using var key = ECDsa.Create();
        key.ImportFromEncryptedPem(await File.ReadAllTextAsync(args[1], stop.Token), ReadPassword());
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException("Use P-256.");
        byte[] signature = key.SignData(SignedUpdateVerifier.SigningBytes(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = new SignedUpdateEnvelope(args[3], Convert.ToBase64String(manifest), Convert.ToBase64String(signature));
        using var output = new FileStream(args[4], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, envelope, cancellationToken: stop.Token);
    }
    else if ((args.Length == 4 && args[0] == "stage") || (args.Length == 3 && args[0] == "verify-stage"))
    {
        using var trust = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zenith.Sessions.ReleaseTool.trusted-update-keys.json")!;
        var keys = await JsonSerializer.DeserializeAsync<UpdateSigningKey[]>(trust, cancellationToken: stop.Token) ?? [];
        if (keys.Length == 0) throw new InvalidOperationException("Updates are disabled: this build contains no approved release signing keys.");
        if (args[0] == "verify-stage")
        {
            var verified = await new SignedUpdateStager(new LocalUpdateFeed(), new(keys), new WindowsUpdatePublisherVerifier())
                .VerifyStageAsync(args[1], Version.Parse(args[2]), stop.Token);
            Console.WriteLine(JsonSerializer.Serialize(verified)); return 0;
        }
        var uri = new Uri(args[1]);
        using var network = new HttpUpdateFeed();
        IUpdateFeed feed = uri.IsFile ? new LocalUpdateFeed() : network;
        var staged = await new SignedUpdateStager(feed, new(keys), new WindowsUpdatePublisherVerifier())
            .StageAsync(uri, Version.Parse(args[2]), args[3], stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(staged));
    }
    else throw new ArgumentException("Usage: keygen <private.pem outside repository> <public.txt>; sign <private.pem> <manifest.json> <key-id> <envelope.json>; stage <feed-uri> <current-version> <staging-root>.");
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine($"Release operation failed ({error.GetType().Name}): {error.Message}");
    return 2;
}

static string ReadPassword()
{
    if (Console.IsInputRedirected) throw new InvalidOperationException("A signing passphrase must be entered in an attended console.");
    var value = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
        else if (!char.IsControl(key.KeyChar) && value.Length < 1024) value.Append(key.KeyChar);
    }
}
