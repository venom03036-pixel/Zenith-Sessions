using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

internal static class PackagedPublicMediaCheck
{
    public static async Task VerifyAsync(CancellationToken ct)
    {
        var directory = new FixtureGallery();
        await using var gallery = new GameGalleryViewModel(directory, () => true);
        if (gallery.PresentationState != GameGalleryPresentationState.NotLoaded || !gallery.ShowPlaceholder)
            throw new InvalidDataException("A new packaged gallery did not report its unloaded state.");
        var game = new PublicRobloxGame(123, 456, 123, "Owned media fixture", 789, "Fixture", PublicRobloxCreatorKind.User, 0, 10, DateTimeOffset.UtcNow);
        await gallery.LoadAsync(game, ct);
        if (gallery.Image is not { IsFrozen: true, PixelWidth: 576, PixelHeight: 324 } || gallery.Position != "1 / 3"
            || gallery.PresentationState != GameGalleryPresentationState.Displayed || gallery.ShowPlaceholder)
            throw new InvalidDataException("Packaged public gallery pixels did not decode.");
        await gallery.MoveAsync(1, ct);
        if (gallery.Image is not null || gallery.Position != "2 / 3" || directory.Fetches != 1
            || gallery.PresentationState != GameGalleryPresentationState.Video || !gallery.ShowPlaceholder)
            throw new InvalidDataException("Unsupported media was fetched or misrepresented.");
        await gallery.MoveAsync(1, ct); await gallery.MoveAsync(1, ct);
        if (directory.Fetches != 2 || gallery.Position != "1 / 3" || gallery.RetainedPixelBytes > 1_492_992)
            throw new InvalidDataException("Packaged gallery did not preserve order or its image budget.");
        if (PublicAccountPresentation.DecodePublicPng(directory.Png, "image/png", ct) is not null)
            throw new InvalidDataException("A gallery image entered the strict avatar decoder.");
        gallery.SetVisible(false);
        if (gallery.Image is not null || gallery.RetainedPixelBytes != 0 || gallery.CanBrowse)
            throw new InvalidDataException("Hidden public media was not cleared.");
        await using var empty = new GameGalleryViewModel(new EmptyGallery(), () => true);
        await empty.LoadAsync(game, ct);
        if (empty.PresentationState != GameGalleryPresentationState.Empty || !empty.ShowPlaceholder
            || empty.HasGallery || empty.CanBrowse || empty.EmptyTitle.Contains("load", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A successful empty packaged gallery was represented as not loaded.");
    }

    private sealed class EmptyGallery : IPublicRobloxGameGallery
    {
        public Task<PublicDirectoryResult<PublicRobloxGameGallery>> GetGameGalleryAsync(long place, long universe, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PublicDirectoryResult<PublicRobloxGameGallery>(PublicDirectoryState.Success,
                new(place, universe, Array.Empty<PublicRobloxGameGalleryImage>(), DateTimeOffset.UtcNow), "Synthetic empty gallery"));
        }
        public Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>> FetchGameGalleryImageAsync(PublicRobloxGameGalleryImage image, CancellationToken ct)
            => throw new InvalidOperationException("An empty gallery must not fetch media.");
    }

    private sealed class FixtureGallery : IPublicRobloxGameGallery
    {
        public readonly byte[] Png = MakePng();
        public int Fetches;
        public Task<PublicDirectoryResult<PublicRobloxGameGallery>> GetGameGalleryAsync(long place, long universe, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var now = DateTimeOffset.UtcNow;
            PublicRobloxGameGalleryImage Image(int position, long id) => new(place, universe, position, id,
                PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Ready,
                new Uri($"https://tr.rbxcdn.com/owned-fixture-{id}.png"), 576, 324, now);
            return Task.FromResult(new PublicDirectoryResult<PublicRobloxGameGallery>(PublicDirectoryState.Success,
                new(place, universe, Array.AsReadOnly(new[] { Image(0, 1), new(place, universe, 1, null,
                    PublicRobloxGameGalleryKind.Video, PublicRobloxGameGalleryState.Unsupported, null, 576, 324, now), Image(2, 2) }), now), "Synthetic packaged gallery"));
        }
        public Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>> FetchGameGalleryImageAsync(PublicRobloxGameGalleryImage image, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Fetches++;
            return Task.FromResult(new PublicDirectoryResult<EncodedPublicGameGalleryImage>(PublicDirectoryState.Success,
                new(Png, "image/png", DateTimeOffset.UtcNow), "Synthetic packaged pixels"));
        }
        private static byte[] MakePng()
        {
            const int width = 576, height = 324;
            byte[] pixels = new byte[width * height * 4];
            for (int index = 0; index < pixels.Length; index += 4)
            { pixels[index] = 160; pixels[index + 1] = 184; pixels[index + 2] = 64; pixels[index + 3] = 255; }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
        }
    }
}
