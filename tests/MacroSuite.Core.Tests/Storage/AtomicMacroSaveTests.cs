using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Storage;

public sealed class AtomicMacroSaveTests
{
    [Fact]
    public void Save_ReplacesWholeDocument_WithoutLeavingTemporaryFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zenith-save-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "macro.json");
        try
        {
            var serializer = new MacroJsonSerializer();
            serializer.SerializeToFile(new MacroModel { Name = "First version" }, path);
            serializer.SerializeToFile(new MacroModel { Name = "Second version" }, path);
            serializer.DeserializeFromFile(path).Name.Should().Be("Second version");
            Directory.GetFiles(folder).Should().Equal(path);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Save_WhenDestinationIsLocked_PreservesLastSavedDocument()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zenith-save-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "macro.json");
        try
        {
            var serializer = new MacroJsonSerializer();
            serializer.SerializeToFile(new MacroModel { Name = "Keep me" }, path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Action save = () => serializer.SerializeToFile(new MacroModel { Name = "Unsaved" }, path);
                save.Should().Throw<Exception>().Where(ex => ex is IOException || ex is UnauthorizedAccessException);
            }
            serializer.DeserializeFromFile(path).Name.Should().Be("Keep me");
            Directory.GetFiles(folder).Should().Equal(path);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
}
