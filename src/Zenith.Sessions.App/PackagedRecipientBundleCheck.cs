using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

internal static class PackagedRecipientBundleCheck
{
    public static async Task VerifyAsync(string root, Guid fixtureId, Guid sessionFixtureId, CancellationToken ct)
    {
        using var bundles = new RecipientBundleStore(root);
        var prior = await bundles.LoadAsync(ct);
        if (prior.Bundles.Count == 1 && IsOwnLeftover(prior.Bundles[0], fixtureId, sessionFixtureId))
        {
            // A run stopped by the deadline, the runner's kill or a failed assertion leaves this stage's own bundle committed.
            var leftover = prior.Bundles[0];
            await bundles.DeleteAsync(leftover.Id, leftover.Revision, leftover.ChangeId, ct);
            prior = await bundles.LoadAsync(ct);
        }
        // Anything else (foreign IDs, extra bundles, foreign members) stays on disk for review.
        if (prior.Bundles.Count != 0) throw new InvalidDataException("The owned self-check bundle store contains other records.");
        var bundle = await bundles.SaveAsync(new(fixtureId, "Synthetic account bundle", [fixtureId, sessionFixtureId]), ct);
        var renamed = await bundles.RenameAsync(bundle.Id, bundle.Revision, bundle.ChangeId, "Synthetic renamed bundle", ct);
        var restored = (await bundles.LoadAsync(ct)).Bundles.Single();
        var missing = restored.ResolveRecipients(new HashSet<Guid> { fixtureId });
        if (restored.Id != fixtureId || !restored.ProfileIds.SequenceEqual(new[] { fixtureId, sessionFixtureId })
            || !missing.RequiresReview || missing.AuthorizesLaunch || missing.MissingProfileIds.Single() != sessionFixtureId)
            throw new InvalidDataException("Bundle metadata acquired authority or lost recipient references.");
        await bundles.DeleteAsync(renamed.Id, renamed.Revision, renamed.ChangeId, ct);
        if ((await bundles.LoadAsync(ct)).Bundles.Count != 0) throw new InvalidDataException("Bundle deletion failed.");
    }

    private static bool IsOwnLeftover(RecipientBundle bundle, Guid fixtureId, Guid sessionFixtureId) =>
        bundle.Id == fixtureId && bundle.ProfileIds.All(id => id == fixtureId || id == sessionFixtureId);
}
