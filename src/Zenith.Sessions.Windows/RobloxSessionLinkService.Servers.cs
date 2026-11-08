using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxSessionLinkService : IRobloxSignedInServerDirectory
{
    public Task<SignedInServerResult> BrowseSignedInAsync(RobloxAccountContext context, SignedInServerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validated();
        return RunServerRequestAsync(context, (credential, token) => signedServers!.BrowseAsync(query, context.UserId, credential, token), false, ct);
    }
    public Task<SignedInServerResult> RenamePrivateAsync(RobloxAccountContext context, OwnedPrivateServer server, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(server);
        if (server.Id <= 0 || server.PlaceId <= 0 || server.OwnerUserId != context.UserId || !OwnedPrivateServer.IsNewName(name))
            throw new ArgumentException("Review one owned private server and a short plain name.");
        return RunServerRequestAsync(context, (credential, token) => signedServers!.RenameAsync(server, name, credential, token), true, ct);
    }
    private async Task<SignedInServerResult> RunServerRequestAsync(RobloxAccountContext context,
        Func<RobloxSessionCredential, CancellationToken, Task<SignedInServerResponse>> request, bool mutation, CancellationToken ct)
    {
        SignedInServerPage? page = null; TimeSpan? retryAfter = null;
        var result = await RunAccountRequestAsync(context, RobloxAccountRead.Presence, null, ct, signedServers is null ? null : async (credential, token) =>
        {
            using var response = await request(credential, token).ConfigureAwait(false);
            page = response.Page;
            retryAfter = response.RetryAfter;
            var rotation = response.Rotation is { } changed ? new RobloxSessionCredential(changed.CopyValue(), changed.ExpiresUtc) : null;
            return new RobloxAccountResponse(response.Outcome, rotatedCredential: rotation);
        }, mutation).ConfigureAwait(false);
        return new(result.Outcome, result.Context, result.Outcome == RobloxAccountOutcome.Success ? page : null, retryAfter);
    }
}
