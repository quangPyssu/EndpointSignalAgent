namespace EndpointSignalAgent.Shared.Utilities;

using System.Diagnostics;

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
            return "Other";

        if (s_appCategoryMap.TryGetValue(normalized, out var category))
            return category;

        var inferredKey = normalized; // preserve the process name for caching

        category = InferFromName(inferredKey);
        if (category != "Other")
        {
            s_appCategoryMap[inferredKey] = category;
            return category;
        }

        category = CategorizeFromFileInfo(exeName);
        if (!s_appCategoryMap.ContainsKey(inferredKey)) // check whether it has been cached before
        {
            s_appCategoryMap[inferredKey] = category;
        }

        return category;
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

    private static string InferFromName(string name) => name switch
    {
        _ when name.EndsWith("browser")                         => "Browser",
        _ when name.EndsWith("studio") || name.EndsWith("ide")  => "IDE",
        _ when name.EndsWith("term") || name.EndsWith("console")=> "Terminal",
        _ when name.EndsWith("chat") || name.EndsWith("meet")   => "Comms",
        _ when name.EndsWith("mail")                            => "Email",
        _ when name.EndsWith("db") || name.EndsWith("sql")      => "Database",
        _ when name.EndsWith("player") || name.EndsWith("media")=> "Media",
        _ when name.Contains("remote") || name.Contains("rdp")  => "RemoteAccess",
        _                                                       => "Other"
    };


    private static string CategorizeFromFileInfo(string exePath)
    {
        if (!File.Exists(exePath)) return "Other";
        
        var info = FileVersionInfo.GetVersionInfo(exePath);
        
        // FileDescription is usually the human-readable app name
        var description = (info.FileDescription ?? info.ProductName ?? "").ToLowerInvariant();
        
        return description switch
        {
            _ when description.Contains("browser")    => "Browser",
            _ when description.Contains("terminal")   => "Terminal",
            _ when description.Contains("studio")     => "IDE",
            _ when description.Contains("mail")       => "Email",
            // ...
            _                                         => "Other"
        };
    }
}
