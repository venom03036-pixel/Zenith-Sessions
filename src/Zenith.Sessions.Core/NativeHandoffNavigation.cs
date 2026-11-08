namespace Zenith.Sessions.Core;

/// <summary>Candidate-browser policy only; the external-scheme event must always cancel first.</summary>
public static class NativeHandoffNavigation
{
    public static bool IsAllowedPage(NativeHandoffContext? context, string? address)
        => context is not null && address is not null
        && (RobloxHandoffProbePolicy.IsAllowedGamePage(new(context.ProfileId, context.ExpectedUserId,
                context.PendingAuthorizationGeneration, context.Destination.PlaceId), address)
            || (context.Destination.ServerKind == RobloxServerKind.Private
                ? context.PrivateLease?.MatchesPage(address) == true
                : string.Equals(address, context.Destination.ToWebStartUri().AbsoluteUri, StringComparison.Ordinal)));

    public static string InitialPage(NativeHandoffContext context)
        => context.Destination.ServerKind == RobloxServerKind.Private ? context.PrivateLease!.InitialPage() : context.Destination.JobId is null
            ? RobloxHandoffProbePolicy.CreateGamePageUri(new(context.ProfileId, context.ExpectedUserId,
                context.PendingAuthorizationGeneration, context.Destination.PlaceId))
            : context.Destination.ToWebStartUri().AbsoluteUri;

    public static RobloxHandoffProbeNavigation Classify(NativeHandoffContext context, string? address, bool cancellingGuardInstalled)
    {
        if (IsAllowedPage(context, address)) return RobloxHandoffProbeNavigation.AllowGamePage;
        return cancellingGuardInstalled && address is { Length: > 0 and <= NativeRobloxHandoff.MaximumCharacters }
            && address.StartsWith("roblox-player:", StringComparison.Ordinal)
            && address.All(value => value is >= '!' and <= '~')
                ? RobloxHandoffProbeNavigation.AllowCancelledRobloxPlayer : RobloxHandoffProbeNavigation.Block;
    }
}
