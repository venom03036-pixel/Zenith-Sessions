namespace Zenith.Sessions.App;

/// <summary>Transient Accounts layout only; it never changes selection, drafts or stored settings.</summary>
public sealed partial class ShellViewModel
{
    private bool _accountInspectorNarrow,_accountInspectorOpen;
    private Command? _toggleAccountInspector;
    public bool IsAccountInspectorNarrow=>_accountInspectorNarrow;
    public bool IsAccountInspectorVisible=>!_accountInspectorNarrow || _accountInspectorOpen;
    public string AccountInspectorToggleLabel=>_accountInspectorOpen?"Hide details":"Details";
    public Command ToggleAccountInspector=>_toggleAccountInspector??=new(_=>
    {
        _accountInspectorOpen=!_accountInspectorOpen;
        Raise(nameof(IsAccountInspectorVisible));Raise(nameof(AccountInspectorToggleLabel));
    });
    public void SetAccountsViewportWidth(double width)
    {
        if(!double.IsFinite(width) || width<=0) return;
        bool narrow=width<900;
        if(narrow==_accountInspectorNarrow) return;
        _accountInspectorNarrow=narrow;
        Raise(nameof(IsAccountInspectorNarrow));Raise(nameof(IsAccountInspectorVisible));
    }
}
