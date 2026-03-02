namespace EndpointSignalAgent.Shared.Utilities;

/// <summary>
/// Provides application categorization based on process executable names.
/// </summary>
public static class ApplicationCategorizer
{
    private static readonly Dictionary<string, string> s_appCategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers
        ["chrome"] = "Browser",
        ["msedge"] = "Browser",
        ["firefox"] = "Browser",
        ["brave"] = "Browser",
        ["opera"] = "Browser",
        ["vivaldi"] = "Browser",
        ["safari"] = "Browser",
        ["iexplore"] = "Browser",
        ["microsoftedge"] = "Browser",
        ["tor"] = "Browser",
        
        // IDE / Development Tools
        ["devenv"] = "IDE",
        ["rider64"] = "IDE",
        ["code"] = "IDE",
        ["idea64"] = "IDE",
        ["clion64"] = "IDE",
        ["pycharm64"] = "IDE",
        ["webstorm64"] = "IDE",
        ["phpstorm64"] = "IDE",
        ["goland64"] = "IDE",
        ["rubymine64"] = "IDE",
        ["datagrip64"] = "IDE",
        ["eclipse"] = "IDE",
        ["netbeans"] = "IDE",
        ["sublimetext"] = "IDE",
        ["atom"] = "IDE",
        ["notepad++"] = "IDE",
        ["visualstudio"] = "IDE",
        ["androidstudio"] = "IDE",
        ["xcode"] = "IDE",
        
        // Terminal / Command Line
        ["cmd"] = "Terminal",
        ["powershell"] = "Terminal",
        ["pwsh"] = "Terminal",
        ["windowsterminal"] = "Terminal",
        ["wt"] = "Terminal",
        ["bash"] = "Terminal",
        ["sh"] = "Terminal",
        ["zsh"] = "Terminal",
        ["conemu64"] = "Terminal",
        ["conemu"] = "Terminal",
        ["cmder"] = "Terminal",
        ["alacritty"] = "Terminal",
        ["hyper"] = "Terminal",
        ["iterm2"] = "Terminal",
        
        // Communication
        ["slack"] = "Comms",
        ["teams"] = "Comms",
        ["discord"] = "Comms",
        ["zoom"] = "Comms",
        ["skype"] = "Comms",
        ["telegram"] = "Comms",
        ["whatsapp"] = "Comms",
        ["signal"] = "Comms",
        ["messenger"] = "Comms",
        ["webex"] = "Comms",
        ["gotomeeting"] = "Comms",
        ["bluejeans"] = "Comms",
        ["googlemeet"] = "Comms",
        ["msteams"] = "Comms",
        
        // Office / Productivity
        ["winword"] = "Office",
        ["excel"] = "Office",
        ["powerpnt"] = "Office",
        ["onenote"] = "Office",
        ["outlook"] = "Office",
        ["msaccess"] = "Office",
        ["mspub"] = "Office",
        ["visio"] = "Office",
        ["project"] = "Office",
        ["acrord32"] = "Office",
        ["acrobat"] = "Office",
        ["foxitreader"] = "Office",
        ["notion"] = "Office",
        ["evernote"] = "Office",
        ["obsidian"] = "Office",
        
        // Media / Entertainment
        ["spotify"] = "Media",
        ["vlc"] = "Media",
        ["musicbee"] = "Media",
        ["itunes"] = "Media",
        ["foobar2000"] = "Media",
        ["aimp"] = "Media",
        ["winamp"] = "Media",
        ["netflix"] = "Media",
        ["hulu"] = "Media",
        ["prime video"] = "Media",
        ["youtube"] = "Media",
        ["plex"] = "Media",
        ["kodi"] = "Media",
        ["mpc-hc64"] = "Media",
        ["potplayer"] = "Media",
        ["audacity"] = "Media",
        
        // Design / Creative
        ["photoshop"] = "Design",
        ["illustrator"] = "Design",
        ["indesign"] = "Design",
        ["premiere"] = "Design",
        ["aftereffects"] = "Design",
        ["lightroom"] = "Design",
        ["figma"] = "Design",
        ["sketch"] = "Design",
        ["gimp"] = "Design",
        ["inkscape"] = "Design",
        ["blender"] = "Design",
        ["unity"] = "Design",
        ["unrealengine"] = "Design",
        ["davinciresolve"] = "Design",
        ["canva"] = "Design",
        
        // System / Utilities
        ["explorer"] = "System",
        ["taskmgr"] = "System",
        ["regedit"] = "System",
        ["mmc"] = "System",
        ["control"] = "System",
        ["services"] = "System",
        ["eventvwr"] = "System",
        ["perfmon"] = "System",
        ["notepad"] = "System",
        ["calc"] = "System",
        ["mspaint"] = "System",
        
        // Database Tools
        ["ssms"] = "Database",
        ["mysql-workbench"] = "Database",
        ["pgadmin4"] = "Database",
        ["dbeaver"] = "Database",
        ["sqldeveloper"] = "Database",
        ["mongodb compass"] = "Database",
        ["robo3t"] = "Database",
        
        // Games
        ["steam"] = "Gaming",
        ["epicgameslauncher"] = "Gaming",
        ["battle.net"] = "Gaming",
        ["origin"] = "Gaming",
        ["gog galaxy"] = "Gaming",
        ["minecraft"] = "Gaming",
        
        // Remote Desktop / VPN
        ["mstsc"] = "RemoteAccess",
        ["teamviewer"] = "RemoteAccess",
        ["anydesk"] = "RemoteAccess",
        ["vnc"] = "RemoteAccess",
        ["chrome remote desktop"] = "RemoteAccess",
        ["citrix"] = "RemoteAccess",
        ["vmware"] = "RemoteAccess",
        
        // File Management
        ["totalcmd64"] = "FileManager",
        ["7zfm"] = "FileManager",
        ["winrar"] = "FileManager",
        ["filezilla"] = "FileManager",
        ["winscp"] = "FileManager",
        
        // Email Clients
        ["thunderbird"] = "Email",
        ["mailbird"] = "Email",
        ["emclient"] = "Email",
    };

    /// <summary>
    /// Categorizes an application based on its executable name.
    /// </summary>
    /// <param name="exeName">The process executable name (without extension).</param>
    /// <returns>The category name, or "Other" if not recognized.</returns>
    public static string Categorize(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return "Other";

        var normalizedName = exeName.ToLowerInvariant();

        if (s_appCategoryMap.TryGetValue(normalizedName, out var category))
            return category;

        return "Other";
    }

    /// <summary>
    /// Gets all defined categories.
    /// </summary>
    public static IEnumerable<string> GetAllCategories()
    {
        return s_appCategoryMap.Values.Distinct().OrderBy(c => c);
    }

    /// <summary>
    /// Gets all applications in a specific category.
    /// </summary>
    public static IEnumerable<string> GetApplicationsByCategory(string category)
    {
        return s_appCategoryMap
            .Where(kvp => kvp.Value.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .OrderBy(name => name);
    }
}
