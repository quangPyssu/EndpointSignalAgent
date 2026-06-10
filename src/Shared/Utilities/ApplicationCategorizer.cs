namespace EndpointSignalAgent.Shared.Utilities;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;

/// <summary>
/// Provides application categorization based on process executable names.
/// </summary>
public static class ApplicationCategorizer
{
    private static readonly FrozenDictionary<string, string> s_knownApps = new Dictionary<string, string>(StringComparer.Ordinal)
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
        ["vscode"] = "IDE",
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
        ["regedit"] = "System",

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
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, string> s_inferredCache = new(StringComparer.Ordinal);

    public static string Categorize(string exeName)
    {
        var normalized = NormalizeProcessName(exeName);
        if (string.IsNullOrWhiteSpace(normalized))
            return "Other";

        if (s_knownApps.TryGetValue(normalized, out var category))
            return category;

        if (s_inferredCache.TryGetValue(normalized, out category))
            return category;

        category = InferFromName(normalized);
        if (category != "Other")
        {
            s_inferredCache.TryAdd(normalized, category);
            return category;
        }

        category = CategorizeFromFileInfo(exeName);
        s_inferredCache.TryAdd(normalized, category);
        return category;
    }

    public static IEnumerable<string> GetAllCategories()
        => s_knownApps.Values.Distinct().OrderBy(c => c);

    public static IEnumerable<string> GetApplicationsByCategory(string category)
        => s_knownApps
            .Where(kvp => kvp.Value.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .OrderBy(name => name);

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

    internal static ReadOnlySpan<char> StripArchSuffix(ReadOnlySpan<char> name)
    {
        if (name.EndsWith("x8664",  StringComparison.Ordinal)) return name[..^5];
        if (name.EndsWith("x64",    StringComparison.Ordinal)) return name[..^3];
        if (name.EndsWith("x86",    StringComparison.Ordinal)) return name[..^3];
        if (name.EndsWith("x32",    StringComparison.Ordinal)) return name[..^3];
        if (name.EndsWith("arm64",  StringComparison.Ordinal)) return name[..^5];
        if (name.EndsWith("64",     StringComparison.Ordinal)) return name[..^2];
        if (name.EndsWith("32",     StringComparison.Ordinal)) return name[..^2];
        return name;
    }

    internal static string InferFromName(string name)
    {
        var s = StripArchSuffix(name.AsSpan());

        if (s.EndsWith("browser",  StringComparison.Ordinal)) return "Browser";

        if (s.EndsWith("studio",   StringComparison.Ordinal) ||
            s.EndsWith("ide",      StringComparison.Ordinal) ||
            s.EndsWith("edit",     StringComparison.Ordinal)) return "IDE";

        if (s.EndsWith("term",     StringComparison.Ordinal) ||
            s.EndsWith("console",  StringComparison.Ordinal) ||
            s.EndsWith("shell",    StringComparison.Ordinal)) return "Terminal";

        if (s.EndsWith("chat",     StringComparison.Ordinal) ||
            s.EndsWith("meet",     StringComparison.Ordinal)) return "Comms";

        if (s.EndsWith("mail",     StringComparison.Ordinal)) return "Email";

        if (s.EndsWith("db",       StringComparison.Ordinal) ||
            s.EndsWith("sql",      StringComparison.Ordinal) ||
            s.EndsWith("base",     StringComparison.Ordinal)) return "Database";

        if (s.EndsWith("player",   StringComparison.Ordinal) ||
            s.EndsWith("media",    StringComparison.Ordinal) ||
            s.EndsWith("cast",     StringComparison.Ordinal)) return "Media";

        if (s.EndsWith("design",   StringComparison.Ordinal) ||
            s.EndsWith("paint",    StringComparison.Ordinal)) return "Design";

        if (s.EndsWith("games",    StringComparison.Ordinal) ||
            s.EndsWith("game",     StringComparison.Ordinal) ||
            s.EndsWith("launcher", StringComparison.Ordinal)) return "Gaming";

        if (s.EndsWith("fm",       StringComparison.Ordinal) ||
            s.EndsWith("files",    StringComparison.Ordinal) ||
            s.EndsWith("manager",  StringComparison.Ordinal)) return "FileManager";

        if (s.EndsWith("office",   StringComparison.Ordinal) ||
            s.EndsWith("docs",     StringComparison.Ordinal) ||
            s.EndsWith("doc",      StringComparison.Ordinal) ||
            s.EndsWith("note",     StringComparison.Ordinal)) return "Office";

        if (s.Contains("remote",   StringComparison.Ordinal) ||
            s.Contains("rdp",      StringComparison.Ordinal) ||
            s.Contains("vnc",      StringComparison.Ordinal)) return "RemoteAccess";

        if (s.EndsWith("svc",      StringComparison.Ordinal) ||
            s.EndsWith("host",     StringComparison.Ordinal) ||
            s.Contains("broker",   StringComparison.Ordinal)) return "System";

        return "Other";
    }


    private static string CategorizeFromFileInfo(string exePath)
    {
        if (!File.Exists(exePath)) return "Other";

        var info = FileVersionInfo.GetVersionInfo(exePath);
        var description = (info.FileDescription ?? info.ProductName ?? "").ToLowerInvariant();

        if (description.Contains("browser"))                                    return "Browser";
        if (description.Contains("terminal") ||
            description.Contains("console")  ||
            description.Contains("shell"))                                      return "Terminal";
        if (description.Contains("studio")   ||
            description.Contains(" ide")     ||
            description.Contains("editor"))                                     return "IDE";
        if (description.Contains("mail")     ||
            description.Contains("email"))                                      return "Email";
        if (description.Contains("chat")     ||
            description.Contains("meeting")  ||
            description.Contains("conferenc"))                                  return "Comms";
        if (description.Contains("database") ||
            description.Contains(" sql")     ||
            description.Contains(" db "))                                       return "Database";
        if (description.Contains("media")    ||
            description.Contains("player"))                                     return "Media";
        if (description.Contains("design")   ||
            description.Contains("photo")    ||
            description.Contains("illustrat"))                                  return "Design";
        if (description.Contains("game")     ||
            description.Contains("launcher"))                                   return "Gaming";
        if (description.Contains("remote")   ||
            description.Contains("desktop"))                                    return "RemoteAccess";
        if (description.Contains("file manag") ||
            description.Contains("archiver"))                                   return "FileManager";
        if (description.Contains("office")   ||
            description.Contains("document") ||
            description.Contains("spreadsh"))                                   return "Office";

        return "Other";
    }
}
