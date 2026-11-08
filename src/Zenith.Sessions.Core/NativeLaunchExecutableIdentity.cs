namespace Zenith.Sessions.Core;

/// <summary>Opaque identity of the exact executable used by a recorded trial. No path or credential is persisted.</summary>
public sealed record NativeLaunchExecutableIdentity(string PathSha256, string FileSha256, string FileIdentitySha256)
{
    public NativeLaunchExecutableIdentity Validated()
    {
        if (!Digest(PathSha256) || !Digest(FileSha256) || !Digest(FileIdentitySha256))
            throw new InvalidDataException("A verified native launch has an invalid executable identity.");
        return this;
    }

    private static bool Digest(string? value) => value is { Length: 64 }
        && value.AsSpan().IndexOfAnyExcept("0123456789ABCDEF") < 0;
}
