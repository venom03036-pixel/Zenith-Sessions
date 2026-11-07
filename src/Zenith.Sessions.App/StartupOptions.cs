using System.ComponentModel;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed record StartupOptions(string DataRoot,bool SampleProfiles,bool SelfCheck=false)
{
    public static string DefaultRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZenithSessions","Personal");
    public static StartupOptions Parse(IReadOnlyList<string> arguments)=>Parse(arguments,DefaultRoot);
    public static StartupOptions Parse(IReadOnlyList<string> arguments,string defaultRoot)
    {
        string root=defaultRoot; bool explicitRoot=false,sample=false,selfCheck=false;
        for(int i=0;i<arguments.Count;i++)
        {
            switch(arguments[i])
            {
                case "--data-root":
                    if(explicitRoot || i+1>=arguments.Count || arguments[i+1].StartsWith("--",StringComparison.Ordinal)) throw new ArgumentException("Provide one data folder after --data-root.");
                    // Device and NT object spellings skip normal path handling, so they never name a data folder.
                    if(DirectoryIdentity.IsDevicePath(arguments[i+1])) throw new ArgumentException(@"Give --data-root a normal folder path, without a \\?\ or \\.\ prefix. No data was changed.");
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
        if((sample || selfCheck) && (!explicitRoot || IsSameFolder(root,defaultRoot)))
            throw new ArgumentException("Test modes require a separate, explicit test folder. Your normal personal library is excluded.");
        return new(root,sample,selfCheck);
    }
    // Compares what Windows resolves both folders to, so a junction, subst-drive or 8.3 spelling of the personal library is still that library.
    private static bool IsSameFolder(string root,string personal)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(DirectoryIdentity.Canonical(root))
                .Equals(Path.TrimEndingDirectorySeparator(DirectoryIdentity.Canonical(personal)),StringComparison.OrdinalIgnoreCase);
        }
        // Fails closed: a test mode never starts on a folder that could not be told apart from the personal library.
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or Win32Exception)
        { throw new ArgumentException("The test folder could not be checked against your normal personal library. Choose a separate local test folder. No data was changed."); }
    }
}
