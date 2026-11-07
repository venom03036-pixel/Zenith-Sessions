namespace Zenith.Sessions.App;

/// <summary>
/// Join-a-game page layout state: Find a game → Browse servers → Ready when you are, plus the
/// Public/Private server tab. Presentation only: changing a step never searches, queues or launches.
/// </summary>
public sealed partial class ShellViewModel
{
    private int _joinStep = 1;
    private bool _privateServerTab;
    private Command? _showJoinStep, _showServerTab;

    public int JoinStep => _joinStep;
    public bool IsJoinFindStep => _joinStep == 1;
    public bool IsJoinServerStep => _joinStep == 2;
    public bool IsJoinReadyStep => _joinStep == 3;
    public bool IsPrivateServerTab => _privateServerTab;
    public bool IsPublicServerTab => !_privateServerTab;

    /// <summary>Parameter "1", "2" or "3" (or the matching int). Anything else is ignored.</summary>
    public Command ShowJoinStep => _showJoinStep ??= new(parameter => SetJoinStep(parameter switch
    {
        "1" => 1, "2" => 2, "3" => 3, int value => value, _ => 0
    }));

    /// <summary>Parameter "Private" shows the private-server link tab; anything else shows public servers.</summary>
    public Command ShowServerTab => _showServerTab ??= new(parameter =>
    {
        bool isPrivate = parameter is "Private";
        if (isPrivate == _privateServerTab) return;
        _privateServerTab = isPrivate;
        Raise(nameof(IsPrivateServerTab)); Raise(nameof(IsPublicServerTab));
    });

    private void ResetJoinFlow() => SetJoinStep(1);

    private void SetJoinStep(int step)
    {
        if (step is < 1 or > 3 || step == _joinStep) return;
        _joinStep = step;
        Raise(nameof(JoinStep)); Raise(nameof(IsJoinFindStep)); Raise(nameof(IsJoinServerStep)); Raise(nameof(IsJoinReadyStep));
    }
}
