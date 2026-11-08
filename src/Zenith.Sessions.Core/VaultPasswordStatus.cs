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
public sealed class VaultPasswordLegacyMigrationException : InvalidOperationException
{
    public VaultPasswordLegacyMigrationException() : base("An older sign-in file lacks verified identity and already requires sign-in. Reauthenticate or explicitly remove that saved sign-in before enabling a master password; its original file was preserved.") { }
}
