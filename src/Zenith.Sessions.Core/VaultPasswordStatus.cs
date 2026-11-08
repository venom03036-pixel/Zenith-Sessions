namespace Zenith.Sessions.Core;

public enum VaultPasswordMode { Disabled, Locked, Unlocked, MigrationRequired }
public sealed record VaultPasswordStatus(VaultPasswordMode Mode);
public sealed class VaultPasswordRequiredException : InvalidOperationException
{
    public VaultPasswordRequiredException() : base("Unlock the master-password vault before using saved sign-ins.") { }
}
public sealed class VaultPasswordMigrationException : InvalidOperationException
{
    public VaultPasswordMigrationException() : base("Saved-sign-in encryption is incomplete. Explicitly resume it with the same master password; original data is preserved.") { }
}
