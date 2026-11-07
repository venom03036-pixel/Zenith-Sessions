namespace Zenith.Sessions.App;

public sealed record StartupOptions(string DataRoot,bool SampleProfiles,bool SelfCheck=false)
{
    public static string DefaultRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZenithSessions","Personal");
    public static StartupOptions Parse(IReadOnlyList<string> arguments)
    {
        string root=DefaultRoot; bool explicitRoot=false,sample=false,selfCheck=false;
        for(int i=0;i<arguments.Count;i++)
        {
            switch(arguments[i])
            {
                case "--data-root":
                    if(explicitRoot || i+1>=arguments.Count || arguments[i+1].StartsWith("--",StringComparison.Ordinal)) throw new ArgumentException("Provide one data folder after --data-root.");
                    root=Path.GetFullPath(arguments[++i]); explicitRoot=true; break;
                case "--sample-profiles":
                    if(sample) throw new ArgumentException("The sample-library option was repeated."); sample=true; break;
                case "--self-check":
                    if(selfCheck) throw new ArgumentException("The self-check option was repeated."); selfCheck=true; break;
                default: throw new ArgumentException("Unknown startup option. No data was changed.");
            }
        }
        root=Path.GetFullPath(root);
        if(sample && selfCheck) throw new ArgumentException("Sample UI and non-interactive self-check are separate modes.");
        if((sample || selfCheck) && (!explicitRoot || root.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(DefaultRoot).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Test modes require a separate, explicit test folder. Your normal personal library is excluded.");
        return new(root,sample,selfCheck);
    }
}
