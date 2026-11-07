namespace MacroSuite.Core.Models;

/// <summary>Optional targeting policy. An absent policy retains legacy document behavior.</summary>
public enum MacroTargetMode { Screen = 0, ActiveExternalWindow = 1, Application = 2, SpecificWindow = 3 }

public sealed record MacroTargetRule
{
    public MacroTargetMode Mode { get; init; } = MacroTargetMode.Screen;
    public string? ApplicationId { get; init; }
    public string? WindowTitle { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new InvalidDataException("Choose a supported macro target mode.");
        if (ApplicationId?.Length > 512 || WindowTitle?.Length > 2048
            || ApplicationId?.IndexOf('\0') >= 0 || WindowTitle?.IndexOf('\0') >= 0)
            throw new InvalidDataException("The saved macro target is invalid.");
        if (Mode is MacroTargetMode.Application or MacroTargetMode.SpecificWindow && string.IsNullOrWhiteSpace(ApplicationId))
            throw new InvalidDataException("Choose the application for this macro target.");
        if (Mode == MacroTargetMode.SpecificWindow && string.IsNullOrWhiteSpace(WindowTitle))
            throw new InvalidDataException("Choose the window for this macro target.");
    }
}
