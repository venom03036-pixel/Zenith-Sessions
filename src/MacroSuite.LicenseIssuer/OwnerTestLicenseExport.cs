using System.IO;
using System.IO.Compression;
using System.Text;

namespace MacroSuite.LicenseIssuer;

/// <summary>Exports an already-issued test set. A new archive is created without replacing any file.</summary>
public static class OwnerTestLicenseExport
{
    public static async Task SaveAsync(string path, IReadOnlyList<OwnerTestLicense> licenses, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(licenses);
        if (licenses.Count != 3 || licenses.Select(x => x.Number).Order().SequenceEqual(new[] { 1, 2, 3 }) == false ||
            licenses.Any(x => string.IsNullOrWhiteSpace(x.LicenseText) || x.LicenseText.Length > 65_536))
            throw new ArgumentException("Generate a complete set of three test licenses first.", nameof(licenses));
        ct.ThrowIfCancellationRequested();
        // CreateNew is deliberate: selecting an existing archive must never overwrite it.
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        bool completed = false;
        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var license in licenses.OrderBy(x => x.Number))
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry($"Zenith-test-{license.Number}.zlicense", CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await target.WriteAsync(Encoding.UTF8.GetBytes(license.LicenseText), ct).ConfigureAwait(false);
                }
                var readme = archive.CreateEntry("READ-ME.txt", CompressionLevel.Optimal);
                await using var text = readme.Open();
                await text.WriteAsync(Encoding.UTF8.GetBytes("Zenith Macros — three device-bound test licenses\r\n\r\n" +
                    "All three licenses activate only the PC whose activation request was imported.\r\n" +
                    "In the Public app on that PC, open Account, choose Import license file, and select any one .zlicense file.\r\n" +
                    "Importing another replaces the current activation with that test license. A different PC needs its own activation request.\r\n"), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            await stream.FlushAsync(ct).ConfigureAwait(false); stream.Flush(flushToDisk: true); completed = true;
        }
        finally
        {
            if (!completed)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                File.Delete(path);
            }
        }
    }
}
