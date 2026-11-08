using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Zenith.Sessions.Windows;

public sealed record StagedUpdate(string Directory, string Version);
public sealed record VerifiedUpdateInventory(string Version, string ManifestSha256);
public interface IUpdatePublisherVerifier
{
    void Verify(FileStream lockedExecutable, string certificateSha256, CancellationToken cancellationToken);
}
public sealed class WindowsUpdatePublisherVerifier : IUpdatePublisherVerifier
{
    public void Verify(FileStream executable, string certificateSha256, CancellationToken cancellationToken)
    {
        var evidence = new WindowsAuthenticodePolicy().Verify(executable.SafeFileHandle, executable.Name, DateTimeOffset.UtcNow, cancellationToken);
        if (evidence.Outcome != NativeClientTrustOutcome.VerifiedCached || evidence.Signer?.CertificateSha256 != certificateSha256.ToUpperInvariant())
            throw new CryptographicException("The release executable does not have the configured trusted publisher signature.");
    }
}

/// <summary>Stages only. Never closes the app, executes a package, changes installed files, or touches account data.</summary>
public sealed class SignedUpdateStager(IUpdateFeed feed, SignedUpdateVerifier verifier, IUpdatePublisherVerifier publisher)
{
    public async Task<VerifiedUpdateInventory> VerifyStageAsync(string stage, Version currentVersion, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); stop.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = stop.Token; stage = Path.GetFullPath(stage); AssertNoRedirects(stage);
        byte[] envelope;
        using (var input = new FileStream(Path.Combine(stage, "signed-update.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var buffer = new MemoryStream()) { await CopyBoundedAsync(input, buffer, SignedUpdateVerifier.MaximumEnvelopeBytes, ct).ConfigureAwait(false); envelope = buffer.ToArray(); }
        var release = verifier.Verify(envelope, currentVersion);
        using var archiveFile = new FileStream(Path.Combine(stage, "package.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (archiveFile.Length != release.Manifest.PackageBytes || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(await SHA256.HashDataAsync(archiveFile, ct).ConfigureAwait(false)), release.Manifest.PackageSha256))
            throw new CryptographicException("Staged ZIP differs from its signed manifest.");
        archiveFile.Position = 0; ValidateZipDirectoryBudget(archiveFile); using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > 2048) throw new InvalidDataException("Invalid update file count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; string? inventoryHash = null;
        string content = Path.Combine(stage, "content");
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested(); string name = ValidateEntryName(entry, names);
            total = checked(total + entry.Length);
            if (entry.Length is < 0 or > 256L * 1024 * 1024 || total > SignedUpdateVerifier.MaximumPackageBytes) throw new InvalidDataException("Expanded update exceeds its bound.");
            string path = Path.GetFullPath(Path.Combine(content, name));
            if (!path.StartsWith(content + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update escaped staging.");
            using var actual = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (actual.Length != entry.Length) throw new CryptographicException("Staged file length differs from the signed ZIP.");
            using var expected = entry.Open();
            string expectedHash = await HashBoundedAsync(expected, entry.Length, ct).ConfigureAwait(false);
            if (Convert.ToHexString(await SHA256.HashDataAsync(actual, ct).ConfigureAwait(false)) != expectedHash) throw new CryptographicException("Staged content differs from the signed ZIP.");
            if (name == "release-manifest.json") inventoryHash = expectedHash;
        }
        foreach (string path in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories))
            if (!names.Contains(Path.GetRelativePath(content, path).Replace('\\', '/'))) throw new CryptographicException("Staged content contains an unsigned extra file.");
        using (var executable = new FileStream(Path.Combine(content, "Zenith.Sessions.App.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            publisher.Verify(executable, release.Key.PublisherCertificateSha256, ct);
        return new(release.Manifest.Version, inventoryHash ?? throw new InvalidDataException("Signed package has no release inventory."));
    }
    public async Task<StagedUpdate> StageAsync(Uri feedUri, Version currentVersion, string stagingRoot, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        byte[] envelope;
        using (var input = await feed.OpenAsync(feedUri, ct).ConfigureAwait(false))
        using (var memory = new MemoryStream())
        { await CopyBoundedAsync(input, memory, SignedUpdateVerifier.MaximumEnvelopeBytes, ct).ConfigureAwait(false); envelope = memory.ToArray(); }
        var release = verifier.Verify(envelope, currentVersion);
        var packageUri = new Uri(release.Manifest.PackageUri);
        // A signed manifest cannot redirect this installation to another host or local/remote transport.
        if (packageUri.Scheme != feedUri.Scheme || !StringComparer.OrdinalIgnoreCase.Equals(packageUri.Authority, feedUri.Authority))
            throw new InvalidDataException("Package and feed must have the same HTTPS origin or local-file transport.");
        string root = Path.GetFullPath(stagingRoot);
        AssertNoRedirects(root);
        Directory.CreateDirectory(root);
        long retainedBytes = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested(); retainedBytes = checked(retainedBytes + new FileInfo(file).Length);
            if (retainedBytes + release.Manifest.PackageBytes + SignedUpdateVerifier.MaximumPackageBytes > 4 * SignedUpdateVerifier.MaximumPackageBytes)
                throw new IOException("The two-GiB update staging budget is full. Review retained update files before trying again.");
        }
        string stage = Path.Combine(root, "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string zipPath = Path.Combine(stage, "package.zip"), content = Path.Combine(stage, "content");
            using var zipFile = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.Asynchronous);
            using (var input = await feed.OpenAsync(packageUri, ct).ConfigureAwait(false))
            {
                long copied = await CopyBoundedAsync(input, zipFile, release.Manifest.PackageBytes, ct).ConfigureAwait(false);
                if (copied != release.Manifest.PackageBytes) throw new InvalidDataException("Incomplete update download.");
            }
            await zipFile.FlushAsync(ct).ConfigureAwait(false); zipFile.Position = 0;
            if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(await SHA256.HashDataAsync(zipFile, ct).ConfigureAwait(false)), release.Manifest.PackageSha256))
                throw new CryptographicException("Update package hash does not match its signed manifest.");
            zipFile.Position = 0;
            Directory.CreateDirectory(content);
            await ExtractAsync(zipFile, content, ct).ConfigureAwait(false);
            string executable = Path.Combine(content, "Zenith.Sessions.App.exe");
            using (var locked = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
                publisher.Verify(locked, release.Key.PublisherCertificateSha256, ct);
            // The install manifest is also inside the signed ZIP; a preview cannot opt itself into installation.
            if (new FileInfo(Path.Combine(content, "release-manifest.json")).Length > 1048576) throw new InvalidDataException("Release inventory is too large.");
            using (var manifestFile = File.OpenRead(Path.Combine(content, "release-manifest.json")))
            using (var manifest = await JsonDocument.ParseAsync(manifestFile, cancellationToken: ct).ConfigureAwait(false))
                if (!manifest.RootElement.GetProperty("ReleaseReady").GetBoolean()
                    || manifest.RootElement.GetProperty("Version").GetString() != release.Manifest.Version)
                    throw new InvalidDataException("The release package is not approved for this version.");
            await File.WriteAllBytesAsync(Path.Combine(stage, "signed-update.json"), envelope, ct).ConfigureAwait(false);
            return new(stage, release.Manifest.Version);
        }
        catch
        {
            // Only this fresh GUID child is ours. Refuse deletion if another actor redirected any part.
            AssertNoRedirects(stage);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            throw;
        }
    }
    internal static async Task<long> CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[65536]; long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return total;
            total = checked(total + read);
            if (total > limit) throw new InvalidDataException("Update content exceeds its declared bound.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
    private static async Task ExtractAsync(Stream input, string destination, CancellationToken ct)
    {
        ValidateZipDirectoryBudget(input);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is 0 or > 2048) throw new InvalidDataException("Invalid update file count.");
        long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = ValidateEntryName(entry, names);
            total = checked(total + entry.Length);
            if (entry.Length is < 0 or > 256L * 1024 * 1024 || total > SignedUpdateVerifier.MaximumPackageBytes)
                throw new InvalidDataException("Expanded update exceeds its bound.");
            string path = Path.GetFullPath(Path.Combine(destination, name));
            if (!path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update escaped staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            if (await CopyBoundedAsync(source, target, entry.Length, ct).ConfigureAwait(false) != entry.Length) throw new InvalidDataException("Incomplete archive entry.");
        }
    }
    internal static void AssertNoRedirects(string path, bool inspectChildren = true)
    {
        for (string? item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Distribution paths cannot contain redirected folders.");
        if (!inspectChildren || !Directory.Exists(path)) return;
        int count = 0;
        var pending = new Stack<string>(); pending.Push(path);
        while (pending.TryPop(out string? directory))
            foreach (string item in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 10000) throw new IOException("Distribution file inventory exceeds its bound.");
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Distribution contains redirected files.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
            }
    }
    private static bool IsDeviceName(string part)
    {
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9');
    }
    private static string ValidateEntryName(ZipArchiveEntry entry, HashSet<string> names)
    {
        string name = entry.FullName.Replace('\\', '/');
        if (name.Length > 512 || name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part)) || Path.IsPathRooted(name)
            || !names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Unsafe or duplicate update file path.");
        return name;
    }
    private static async Task<string> HashBoundedAsync(Stream input, long bound, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[65536]; long total = 0;
        while (true) { int read = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (read == 0) break; total += read; if (total > bound) throw new InvalidDataException("ZIP entry exceeds its bound."); hash.AppendData(buffer, 0, read); }
        if (total != bound) throw new InvalidDataException("Incomplete ZIP entry.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void ValidateZipDirectoryBudget(Stream stream)
    {
        // Bound the central directory before ZipArchive materializes entries; checking Entries.Count afterward alone is too late.
        long prior = stream.Position; int length = (int)Math.Min(stream.Length, 65557);
        if (length < 22) throw new InvalidDataException("Incomplete ZIP directory.");
        byte[] tail = new byte[length]; stream.Position = stream.Length - length; stream.ReadExactly(tail); stream.Position = prior;
        for (int i = length - 22; i >= 0; i--)
        {
            var span = tail.AsSpan(i);
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x06054b50) continue;
            int comment = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[20..]);
            if (i + 22 + comment != length) continue;
            int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[12..]), offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            if (count is 0 or > 2048 || size > 4 * 1024 * 1024 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) != 0
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span[8..]) != count || (long)offset + size != stream.Length - length + i)
                throw new InvalidDataException("ZIP directory exceeds the supported bounded format.");
            return;
        }
        throw new InvalidDataException("ZIP directory is missing.");
    }
}
