using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using MacroSuite.App.Themes;
using MacroSuite.App.ViewModels;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Themes;

public sealed class AppearanceTests
{
    [Theory]
    [InlineData("#FFFFFF", "#EEEEEE", "#FFFF00", "#888888", "#FFFFFF")]
    [InlineData("#000000", "#010101", "#000020", "#000000", "#000000")]
    [InlineData("#FF00FF", "#888888", "#777777", "#FFFF00", "#777777")]
    [InlineData("#000000", "#FFFFFF", "#FF0000", "#FFFFFF", "#FFFFFF")]
    public void CustomPalette_ProtectsLightDarkAndLowContrastChoices(string canvas, string panel, string accent, string button, string text)
    {
        var profile = new AppearanceProfile { Canvas = canvas, Panel = panel, Accent = accent, Button = button, Text = text,
            AutomaticText = false, BackgroundKind = AppearanceBackgroundKind.Image, PanelOpacity = .15 };
        var result = AppearancePalette.Resolve(profile); var c = result.Palette.Colors;
        foreach (string background in new[] { "AppBackground", "CardBackground", "SurfaceBackground", "HoverBackground", "ActiveBackground", "SelectionBackground" })
        {
            foreach (string foreground in new[] { "TextPrimary", "TextSecondary", "TextMuted", "AccentDanger", "AccentSuccess", "AccentWarning", "ActionMouse", "ActionKeyboard", "ActionAI" })
                AppearancePalette.Contrast(c[foreground], c[background]).Should().BeGreaterThanOrEqualTo(4.5, $"{foreground} on {background}");
            AppearancePalette.Contrast(c["BorderBrushFocus"], c[background]).Should().BeGreaterThanOrEqualTo(3);
        }
        foreach (string background in new[] { "AccentPrimary", "PrimaryHoverBackground", "PrimaryPressedBackground" })
            AppearancePalette.Contrast(c["PrimaryForeground"], c[background]).Should().BeGreaterThanOrEqualTo(4.5);
        foreach (Color beneath in new[] { Colors.Black, Colors.White })
            foreach (string background in new[] { "AppBackground", "CardBackground" })
                foreach (string foreground in new[] { "TextMuted", "AccentDanger", "AccentSuccess", "AccentWarning" })
                    AppearancePalette.Contrast(c[foreground], AppearancePalette.Mix(beneath, c[background], result.EffectivePanelOpacity)).Should().BeGreaterThanOrEqualTo(4.5);
        result.EffectivePanelOpacity.Should().BeInRange(.15, 1);
        result.Notice.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CustomThemeLibrary_SupportsSaveRenameDuplicateRevertDeleteAndReload()
    {
        string directory = NewDirectory(); var vm = new AppearanceViewModel(directory, "amber-night");
        vm.ThemeName = "My amber"; vm.SelectedColorHex = "#CC8800"; vm.SaveAsThemeCommand.Execute(null);
        string id = vm.SelectedCustomTheme!.Id;
        vm.ThemeName = "Renamed amber"; vm.RenameThemeCommand.Execute(null);
        vm.CustomThemes.Should().ContainSingle(p => p.Id == id && p.Name == "Renamed amber");
        vm.DuplicateThemeCommand.Execute(null); vm.CustomThemes.Should().HaveCount(2);
        vm.SelectedColorHex = "#112233"; vm.IsDirty.Should().BeTrue(); vm.RevertThemeCommand.Execute(null);
        vm.SelectedColorHex.Should().Be("#CC8800"); vm.IsDirty.Should().BeFalse();
        var reloaded = new AppearanceViewModel(directory, ThemeCatalog.DefaultId);
        reloaded.SelectedCustomTheme!.Id.Should().Be(vm.SelectedCustomTheme!.Id);
        reloaded.CustomThemes.Should().HaveCount(2);
        reloaded.DeleteThemeCommand.Execute(null); reloaded.CustomThemes.Should().ContainSingle(p => p.Id == id);
        reloaded.CurrentPalette.Id.Should().Be("amber-night");
        reloaded.UsePreset("ocean-blue");
        new AppearanceViewModel(directory, "ocean-blue").CurrentPalette.Id.Should().Be("ocean-blue");
        await vm.ShutdownAsync(); await reloaded.ShutdownAsync();
    }

    [Fact]
    public async Task ResetToBase_RemovesCustomBackgroundAndColorsOnlyAfterExplicitSave()
    {
        string directory = NewDirectory(), file = Path.Combine(directory, "background.png");
        WritePng(file, 8, 8, Colors.Orange);
        var vm = new AppearanceViewModel(directory, "midnight");
        await vm.ImportFileAsync(file); vm.ThemeName = "Saved custom"; vm.SaveAsThemeCommand.Execute(null); await vm.WaitForBackgroundAsync();
        string id = vm.SelectedCustomTheme!.Id;
        vm.SelectedColorRole = "Panels"; vm.SelectedColorHex = "#FFFFFF"; vm.PanelOpacityPercent = 35;
        vm.ResetThemeCommand.Execute(null);
        vm.CurrentProfile.Should().Be(AppearanceProfile.FromPreset(ThemeCatalog.Resolve("midnight")) with { Id = id, Name = "Saved custom" });
        vm.Asset.Should().BeNull(); vm.IsDirty.Should().BeTrue();
        var unsavedReload = new AppearanceViewModel(directory, "midnight");
        unsavedReload.CurrentProfile.AssetId.Should().NotBeNull("Reset previews changes until Save theme");
        vm.SaveThemeCommand.Execute(null); await vm.WaitForBackgroundAsync();
        var savedReload = new AppearanceViewModel(directory, "midnight");
        savedReload.CurrentProfile.Should().Be(vm.CurrentProfile);
        await vm.ShutdownAsync(); await unsavedReload.ShutdownAsync(); await savedReload.ShutdownAsync();
    }

    [Fact]
    public async Task FailedThemeSave_RetainsTheStoredThemeAndKeepsTheDraftForRetry()
    {
        string directory = NewDirectory(); var vm = new AppearanceViewModel(directory, ThemeCatalog.DefaultId);
        vm.ThemeName = "Original"; vm.SaveAsThemeCommand.Execute(null); await vm.WaitForBackgroundAsync();
        AppearanceProfile original = vm.SelectedCustomTheme!;
        string path = Path.Combine(directory, "Config", "appearance.json"), contents = File.ReadAllText(path);
        vm.ThemeName = "Edited"; vm.SelectedColorHex = "#AABBCC";
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            vm.SaveThemeCommand.Execute(null);
            vm.CustomThemes.Should().ContainSingle().Which.Should().Be(original);
            vm.SelectedCustomTheme.Should().Be(original); vm.CurrentProfile.Name.Should().Be("Edited");
            vm.IsDirty.Should().BeTrue(); vm.Status.Should().StartWith("Could not save appearance:");
        }
        File.ReadAllText(path).Should().Be(contents);
        vm.SaveThemeCommand.Execute(null); await vm.WaitForBackgroundAsync();
        var reloaded = new AppearanceViewModel(directory, ThemeCatalog.DefaultId);
        reloaded.CurrentProfile.Name.Should().Be("Edited"); reloaded.CurrentProfile.Accent.Should().Be("#AABBCC");
        await vm.ShutdownAsync(); await reloaded.ShutdownAsync();
    }

    [Theory]
    [InlineData("#FFF4CC", true)]
    [InlineData("#102030", false)]
    public async Task ImagePalette_UsesTheDominantColorAndChoosesTheMatchingLuminanceFamily(string hex, bool light)
    {
        string directory = NewDirectory(), file = Path.Combine(directory, "background.png");
        WritePng(file, 24, 16, AppearancePalette.Parse(hex));
        var vm = new AppearanceViewModel(directory, ThemeCatalog.DefaultId);
        await vm.ImportFileAsync(file);
        vm.Asset!.Swatches[0].Should().Be(hex); vm.CurrentProfile.ColorsFromImage.Should().BeTrue();
        vm.CurrentProfile.AutomaticText.Should().BeTrue(); vm.CurrentProfile.Button.Should().Be(hex);
        double luminance = AppearancePalette.Luminance(vm.CurrentPalette.Colors["AppBackground"]);
        if (light) luminance.Should().BeGreaterThan(.72); else luminance.Should().BeLessThan(.10);
        AppearancePalette.Contrast(vm.CurrentPalette.Colors["TextPrimary"], vm.CurrentPalette.Colors["CardBackground"]).Should().BeGreaterThanOrEqualTo(4.5);
        AppearanceProfile derived = vm.CurrentProfile;
        vm.SelectedColorHex = "#FF00AA"; vm.CurrentProfile.ColorsFromImage.Should().BeFalse();
        vm.ColorsFromImage = true; vm.CurrentProfile.Should().Be(derived, "reenabling image colors should deterministically restore the same palette");
        await vm.ShutdownAsync();
    }

    [Fact]
    public void AppearanceStore_FallsBackWithoutTouchingGeneralSettingsAndRejectsUnsafeAssetPaths()
    {
        string directory = NewDirectory(); Directory.CreateDirectory(Path.Combine(directory, "Config"));
        string general = Path.Combine(directory, "Config", "settings.json"); File.WriteAllText(general, "{\"panicHotkey\":\"F12\"}");
        var store = new AppearanceStore(directory); string path = Path.Combine(directory, "Config", "appearance.json");
        File.WriteAllText(path, "{\"Version\":99}"); store.Load(out string? message).CustomThemes.Should().BeEmpty(); message.Should().NotBeNull();
        File.ReadAllText(general).Should().Be("{\"panicHotkey\":\"F12\"}");
        Action save = () => store.Save(new() { CustomThemes = [new AppearanceProfile { AssetId = "../../private.png" }] });
        save.Should().Throw<InvalidDataException>();
        File.WriteAllText(path, "{\"Version\":1,\"CustomThemes\":[null,{\"Name\":\"invalid\",\"Accent\":\"red\"}]}");
        store.Load(out _).CustomThemes.Should().BeEmpty();
    }

    [Fact]
    public async Task BackgroundImport_CopiesAndDownscalesWithoutDependingOnOriginalPath()
    {
        string directory = NewDirectory(); string original = Path.Combine(directory, "original.png");
        WritePng(original, 2048, 1152, Colors.Orange);
        var service = new AppearanceAssetService(directory); var asset = await service.ImportAsync(original, CancellationToken.None);
        File.Delete(original);
        var reloaded = await service.LoadAsync(asset.Id, CancellationToken.None);
        reloaded.Frames.Should().ContainSingle(); reloaded.Frames[0].Image.IsFrozen.Should().BeTrue();
        reloaded.Frames[0].Image.PixelWidth.Should().BeLessThanOrEqualTo(1920);
        reloaded.Frames[0].Image.PixelHeight.Should().BeLessThanOrEqualTo(1080);
        reloaded.Swatches[0].Should().Be("#FFA500");
        Func<Task> invalid = async () => await service.LoadAsync("../../outside.png", CancellationToken.None);
        await invalid.Should().ThrowAsync<InvalidDataException>();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Func<Task> load = async () => await service.LoadAsync(asset.Id, cancelled.Token);
        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TransparentPngImport_PreservesAlphaAndLeavesTheOriginalUnchanged()
    {
        string directory = NewDirectory(), file = Path.Combine(directory, "transparent.png");
        byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 0, 0, 255, 0, 128];
        var source = BitmapSource.Create(3, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 12); source.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = File.Create(file)) encoder.Save(stream);
        byte[] original = File.ReadAllBytes(file);
        var service = new AppearanceAssetService(directory);
        var asset = await service.ImportAsync(file, CancellationToken.None);
        File.ReadAllBytes(file).Should().Equal(original);
        var reloaded = await service.LoadAsync(asset.Id, CancellationToken.None);
        foreach (var imported in new[] { asset, reloaded })
        {
            imported.Frames.Should().ContainSingle(); imported.Frames[0].Image.IsFrozen.Should().BeTrue();
            var frame = new FormatConvertedBitmap(imported.Frames[0].Image, PixelFormats.Bgra32, null, 0);
            Pixel(frame, 0).Should().Be(Colors.Red);
            Pixel(frame, 1).A.Should().Be(0, "the underlying theme color must remain visible through a transparent image pixel");
            Pixel(frame, 2).A.Should().Be(128, "partial transparency must not be flattened into a solid background");
        }
    }

    [Fact]
    public async Task GifDecoder_ComposesOffsetsTransparencyDisposalAndOriginalTiming()
    {
        string directory = NewDirectory(); string original = Path.Combine(directory, "fixture.gif");
        File.WriteAllBytes(original, GifFixture());
        var asset = await new AppearanceAssetService(directory).ImportAsync(original, CancellationToken.None);
        asset.Frames.Should().HaveCount(5);
        asset.Frames.Select(f => f.Duration.TotalMilliseconds).Should().Equal(100, 130, 170, 190, 20);
        Pixel(asset.Frames[0].Image, 1).Should().Be(Colors.Red);
        Pixel(asset.Frames[1].Image, 1).Should().Be(Colors.Lime);
        Pixel(asset.Frames[2].Image, 1).A.Should().Be(0, "restore-to-background clears the preceding transparent disposal region");
        Pixel(asset.Frames[2].Image, 2).Should().Be(Colors.Blue);
        Pixel(asset.Frames[3].Image, 2).Should().Be(Colors.Red, "restore-to-previous removes the blue overlay");
        Pixel(asset.Frames[3].Image, 0).Should().Be(Colors.Lime);
        Pixel(asset.Frames[4].Image, 0).Should().Be(Colors.Lime, "a transparent pixel retains the composited background");
        asset.Frames.Should().OnlyContain(f => f.Image.IsFrozen);
    }

    [Fact]
    public async Task AssetCleanup_RemovesOnlyUnreferencedManagedHashFiles()
    {
        string directory = NewDirectory(); string original = Path.Combine(directory, "original.png");
        WritePng(original, 8, 8, Colors.Red); var service = new AppearanceAssetService(directory);
        var first = await service.ImportAsync(original, CancellationToken.None);
        WritePng(original, 8, 8, Colors.Blue); var second = await service.ImportAsync(original, CancellationToken.None);
        string assets = Path.Combine(directory, "Themes", "Assets"), note = Path.Combine(assets, "personal.png");
        File.WriteAllText(note, "unrelated");
        await service.RemoveUnusedAsync([second.Id], CancellationToken.None);
        File.Exists(Path.Combine(assets, first.Id)).Should().BeFalse();
        File.Exists(Path.Combine(assets, second.Id)).Should().BeTrue();
        File.ReadAllText(note).Should().Be("unrelated"); File.Exists(original).Should().BeTrue();
    }

    [Fact]
    public async Task GifRestorePrevious_UsesTheOffsetRectangleWithoutChangingOtherRows()
    {
        string directory = NewDirectory(); string path = Path.Combine(directory, "offset.gif");
        File.WriteAllBytes(path, GifFixture(true));
        var asset = await new AppearanceAssetService(directory).ImportAsync(path, CancellationToken.None);
        Pixel(asset.Frames[2].Image, 2).Should().Be(Colors.Blue);
        Pixel(asset.Frames[3].Image, 2).Should().Be(Colors.Red);
        Pixel(asset.Frames[3].Image, 0, 1).Should().Be(Colors.Lime);
        Pixel(asset.Frames[3].Image, 1, 1).A.Should().Be(0);
        Pixel(asset.Frames[3].Image, 0).Should().Be(Colors.Red);
    }

    [Fact]
    public async Task GifDecoder_RejectsOversizedCanvasBeforeDecoding()
    {
        string directory = NewDirectory(); string path = Path.Combine(directory, "oversized.gif");
        byte[] data = GifFixture(); data[6] = 255; data[7] = 255; data[8] = 255; data[9] = 255; File.WriteAllBytes(path, data);
        Func<Task> action = async () => await new AppearanceAssetService(directory).ImportAsync(path, CancellationToken.None);
        await action.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(Path.Combine(directory, "Themes", "Assets")).Should().BeFalse();
    }

    [Theory]
    [InlineData(300, false)]
    [InlineData(301, true)]
    public async Task GifFrameLimit_KeepsSupportedAnimationAndFallsBackToTheFirstFrameAboveIt(int frameCount, bool fallback)
    {
        string directory = NewDirectory(), file = Path.Combine(directory, "long.gif");
        File.WriteAllBytes(file, GifFixture(additionalFrames: frameCount - 5));
        var service = new AppearanceAssetService(directory);
        AppearanceAsset asset = await service.ImportAsync(file, CancellationToken.None);
        asset.Frames.Should().HaveCount(fallback ? 1 : frameCount);
        if (fallback) asset.Notice.Should().Contain("first frame").And.Contain("still image");
        Pixel(asset.Frames[0].Image, 0).Should().Be(Colors.Red);
        Pixel(asset.Frames[0].Image, 1).Should().Be(Colors.Red);
        asset.Frames[0].Image.IsFrozen.Should().BeTrue();
        File.Delete(file);
        var reloaded = await service.LoadAsync(asset.Id, CancellationToken.None);
        reloaded.Frames.Should().HaveCount(asset.Frames.Count);
        reloaded.Notice.Should().Be(asset.Notice);
    }

    [Fact]
    public async Task GifSourcePixelLimit_UsesAStillFrameEvenBelowTheFrameCountLimit()
    {
        string directory = NewDirectory(), file = Path.Combine(directory, "large-frames.gif");
        const int side = 512;
        byte[] pixels = new byte[side * side * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 1] = 165; pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4); bitmap.Freeze();
        var frame = BitmapFrame.Create(bitmap); frame.Freeze();
        var encoder = new GifBitmapEncoder();
        for (int n = 0; n < 65; n++) encoder.Frames.Add(frame); // 65 * 512² exceeds the 16 Mi-pixel source budget.
        using (var stream = File.Create(file)) encoder.Save(stream);
        var asset = await new AppearanceAssetService(directory).ImportAsync(file, CancellationToken.None);
        asset.Frames.Should().ContainSingle(); asset.Notice.Should().Contain("still image");
        asset.Frames[0].Image.PixelWidth.Should().Be(side);
        Pixel(asset.Frames[0].Image, 0).Should().Be(Colors.Orange);
    }

    [Fact]
    public async Task BackgroundReplacementAndShutdown_DrainOwnedWorkAndNeverResumeEffectsDuringAutomation()
    {
        string directory = NewDirectory(); string file = Path.Combine(directory, "background.png"); WritePng(file, 16, 16, Colors.LightBlue);
        var vm = new AppearanceViewModel(directory, ThemeCatalog.DefaultId);
        Task first = vm.ImportFileAsync(file); Task second = vm.ImportFileAsync(file);
        await Task.WhenAll(first, second); vm.Asset.Should().NotBeNull();
        vm.SetExecutionActive(true); vm.ShouldAnimate.Should().BeFalse(); vm.ImportBackgroundCommand.CanExecute(null).Should().BeFalse();
        vm.SetResourcePressure(true); vm.SetExecutionActive(false); vm.RuntimeReducedEffects.Should().BeFalse(); vm.ShouldAnimate.Should().BeTrue();
        vm.SetResourcePressure(true); vm.ShouldAnimate.Should().BeFalse();
        Task pending = vm.ImportFileAsync(file); await vm.ShutdownAsync(); await pending;
        vm.Asset.Should().BeNull(); vm.ShouldAnimate.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, 5, true, false, true, true)]
    [InlineData(true, 1, true, false, true, true)]
    [InlineData(true, 5, false, false, true, true)]
    [InlineData(true, 5, true, true, true, true)]
    [InlineData(true, 5, true, false, false, true)]
    [InlineData(true, 5, true, false, true, false)]
    public void AnimationGate_PausesForEveryResourceOrWindowRestriction(bool requested, int frames, bool active, bool minimized, bool hardware, bool motion)
        => AppearanceBackdrop.CanAnimate(requested, frames, active, minimized, hardware, motion).Should().BeFalse();

    [Theory]
    [InlineData(0, 1, 1, "#FF0000")]
    [InlineData(120, 1, 1, "#00FF00")]
    [InlineData(240, 1, 1, "#0000FF")]
    [InlineData(50, 0, 1, "#FFFFFF")]
    [InlineData(240, 1, 0, "#000000")]
    public void ColorWheel_HsvMatchesRgbAndHex(double hue, double saturation, double value, string expected)
        => AppearancePalette.Hex(ColorWheel.FromHsv(hue, saturation, value)).Should().Be(expected);

    private static string NewDirectory() { string path = Path.Combine(Path.GetTempPath(), "ZenithAppearanceTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Color Pixel(BitmapSource bitmap, int x, int y = 0) { byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); int i = (y * bitmap.PixelWidth + x) * 4; return Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]); }
    private static void WritePng(string path, int width, int height, Color color)
    {
        byte[] pixels = new byte[width * height * 4]; for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static byte[] GifFixture(bool twoRows = false, int additionalFrames = 0)
    {
        var bytes = new List<byte>(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
        bytes.AddRange([3, 0, (byte)(twoRows ? 2 : 1), 0, 0x81, 0, 0]);
        bytes.AddRange([0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255]);
        void Frame(byte x, byte[] indices, byte disposal, byte delay, bool transparent, byte y = 0, byte height = 1)
        {
            bytes.AddRange([33, 249, 4, (byte)(disposal * 4 + (transparent ? 1 : 0)), delay, 0, 0, 0]);
            bytes.AddRange([44, x, 0, y, 0, (byte)(indices.Length / height), 0, height, 0, 0, 2]);
            var codes = new List<int>(); foreach (byte index in indices) { codes.Add(4); codes.Add(index); } codes.Add(5);
            var packed = new List<byte>(); int bits = 0, count = 0;
            foreach (int code in codes) { bits |= code << count; count += 3; while (count >= 8) { packed.Add((byte)bits); bits >>= 8; count -= 8; } }
            if (count > 0) packed.Add((byte)bits); bytes.Add((byte)packed.Count); bytes.AddRange(packed); bytes.Add(0);
        }
        Frame(0, twoRows ? [1, 1, 1, 1, 1, 1] : [1, 1, 1], 1, 10, false, height: (byte)(twoRows ? 2 : 1));
        Frame(1, [2], 2, 13, true, (byte)(twoRows ? 1 : 0)); Frame(2, [3], 3, 17, true);
        Frame(0, [2], 1, 19, true, (byte)(twoRows ? 1 : 0)); Frame(0, [0], 1, 2, true, (byte)(twoRows ? 1 : 0));
        for (int n = 0; n < additionalFrames; n++) Frame(0, [(byte)(n % 3 + 1)], 1, 10, false);
        bytes.Add(59); return bytes.ToArray();
    }
}
