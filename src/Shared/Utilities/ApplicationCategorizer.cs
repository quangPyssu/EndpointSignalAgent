namespace EndpointSignalAgent.Shared.Utilities;

/// <summary>
/// Provides application categorization based on process executable names.
/// </summary>
public static class ApplicationCategorizer
{
    private static readonly Dictionary<string, string> s_appCategoryMap = new(StringComparer.Ordinal)
    {
        // Browsers
        ["chrome"] = "Browser",
        ["msedge"] = "Browser",
        ["microsoftedge"] = "Browser",
        ["firefox"] = "Browser",
        ["brave"] = "Browser",
        ["opera"] = "Browser",
        ["vivaldi"] = "Browser",
        ["iexplore"] = "Browser",
        ["tor"] = "Browser",

        // IDE / Development
        ["devenv"] = "IDE",
        ["code"] = "IDE",
        ["codeinsiders"] = "IDE",
        ["rider64"] = "IDE",
        ["idea64"] = "IDE",
        ["pycharm64"] = "IDE",
        ["webstorm64"] = "IDE",
        ["clion64"] = "IDE",
        ["androidstudio64"] = "IDE",
        ["eclipse"] = "IDE",
        ["sublimetext"] = "IDE",
        ["notepadplusplus"] = "IDE",

        // Terminal
        ["cmd"] = "Terminal",
        ["powershell"] = "Terminal",
        ["pwsh"] = "Terminal",
        ["windowsterminal"] = "Terminal",
        ["wt"] = "Terminal",
        ["bash"] = "Terminal",
        ["wsl"] = "Terminal",
        ["conhost"] = "Terminal",
        ["mintty"] = "Terminal",

        // Comms
        ["teams"] = "Comms",
        ["msteams"] = "Comms",
        ["slack"] = "Comms",
        ["zoom"] = "Comms",
        ["zoomworkplace"] = "Comms",
        ["discord"] = "Comms",
        ["webex"] = "Comms",
        ["skype"] = "Comms",
        ["telegram"] = "Comms",

        // Office
        ["winword"] = "Office",
        ["excel"] = "Office",
        ["powerpnt"] = "Office",
        ["onenote"] = "Office",
        ["outlook"] = "Office",
        ["acrord32"] = "Office",
        ["acrobat"] = "Office",
        ["onenotem"] = "Office",
        ["notion"] = "Office",

        // Media
        ["spotify"] = "Media",
        ["vlc"] = "Media",
        ["wmplayer"] = "Media",
        ["obs64"] = "Media",
        ["audacity"] = "Media",

        // Design
        ["photoshop"] = "Design",
        ["illustrator"] = "Design",
        ["figma"] = "Design",
        ["blender"] = "Design",
        ["afterfx"] = "Design",

        // System hosts / shell
        ["explorer"] = "System",
        ["shellexperiencehost"] = "System",
        ["searchhost"] = "System",
        ["searchapp"] = "System",
        ["taskmgr"] = "System",
        ["dwm"] = "System",
        ["startmenuexperiencehost"] = "System",
        ["runtimebroker"] = "System",
        ["applicationframehost"] = "System",
        ["svchost"] = "System",

        // Database
        ["ssms"] = "Database",
        ["dbeaver"] = "Database",
        ["pgadmin4"] = "Database",
        ["mysqlworkbench"] = "Database",
        ["datagrip64"] = "Database",
        ["mongodbcompass"] = "Database",

        // Gaming
        ["steam"] = "Gaming",
        ["epicgameslauncher"] = "Gaming",
        ["battlenet"] = "Gaming",
        ["origin"] = "Gaming",

        // Remote access
        ["mstsc"] = "RemoteAccess",
        ["teamviewer"] = "RemoteAccess",
        ["anydesk"] = "RemoteAccess",
        ["vncviewer"] = "RemoteAccess",
        ["vmware"] = "RemoteAccess",

        // File manager
        ["totalcmd64"] = "FileManager",
        ["7zfm"] = "FileManager",
        ["winrar"] = "FileManager",
        ["winscp"] = "FileManager",
        ["filezilla"] = "FileManager",

        // Email
        ["thunderbird"] = "Email",
        ["emclient"] = "Email",
        ["mailbird"] = "Email"
    };

    public static string Categorize(string exeName)
    {
        var normalized = NormalizeProcessName(exeName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Other";
        }

        if (s_appCategoryMap.TryGetValue(normalized, out var category))
        {
            return category;
        }

        return "Other";
    }

    public static IEnumerable<string> GetAllCategories()
    {
        return s_appCategoryMap.Values.Distinct().OrderBy(c => c);
    }

    public static IEnumerable<string> GetApplicationsByCategory(string category)
    {
        return s_appCategoryMap
            .Where(kvp => kvp.Value.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .OrderBy(name => name);
    }

    internal static string NormalizeProcessName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(value.Trim());
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = value.Trim();
        }

        var noExtension = Path.GetFileNameWithoutExtension(fileName);
        var input = string.IsNullOrWhiteSpace(noExtension) ? fileName : noExtension;

        Span<char> buffer = stackalloc char[input.Length];
        var len = 0;
        foreach (var ch in input)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                continue;
            }

            buffer[len++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..len]);
    }
}
