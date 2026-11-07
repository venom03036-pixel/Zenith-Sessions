using System.ComponentModel;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed record StartupOptions(string DataRoot,bool SampleProfiles,bool SelfCheck=false)
{
    public static string DefaultRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZenithSessions","Personal");
    public static StartupOptions Parse(IReadOnlyList<string> arguments)=>Parse(arguments,DefaultRoot);
    // Windows keeps at most 127 characters of tray-icon text, and NotifyIcon.Text throws above that.
    public const int TrayTextLimit=127;
    /// <summary>Names which manager this is, so copies running side by side (personal, sample preview, another folder) can be told apart.</summary>
    public static string DescribeInstance(string dataRoot,bool sample)=>DescribeInstance(dataRoot,sample,DefaultRoot);
    public static string DescribeInstance(string dataRoot,bool sample,string personalRoot)
    {
        if(sample) return "Sample preview";
        if(NamesSameFolder(dataRoot,personalRoot)) return "Personal";
        string full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)),name=Path.GetFileName(full);
        // "Library" keeps a separate folder that happens to be named Personal from reading as the personal library.
        return "Library "+(name.Length>0?name:full);
    }
    /// <summary>The tray, window and taskbar title for this manager, cut to fit the tray.</summary>
    public static string TitleFor(string dataRoot,bool sample)
    {
        string title="Zenith Sessions · "+DescribeInstance(dataRoot,sample);
        if(title.Length<=TrayTextLimit) return title;
        int cut=TrayTextLimit-1;
        if(char.IsHighSurrogate(title[cut-1])) cut--;
        return title[..cut]+"…";
    }
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
        try { return ResolvesToSameFolder(root,personal); }
        // Fails closed: a test mode never starts on a folder that could not be told apart from the personal library.
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or Win32Exception)
        { throw new ArgumentException("The test folder could not be checked against your normal personal library. Choose a separate local test folder. No data was changed."); }
    }
    // Only a label depends on this, so a folder Windows cannot resolve is compared by its full spelling instead of stopping startup.
    private static bool NamesSameFolder(string root,string personal)
    {
        try { return ResolvesToSameFolder(root,personal); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(personal)),StringComparison.OrdinalIgnoreCase);
        }
    }
    private static bool ResolvesToSameFolder(string root,string personal)=>
        Path.TrimEndingDirectorySeparator(DirectoryIdentity.Canonical(root))
            .Equals(Path.TrimEndingDirectorySeparator(DirectoryIdentity.Canonical(personal)),StringComparison.OrdinalIgnoreCase);
}
