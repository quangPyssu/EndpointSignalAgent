using EndpointSignalAgent.Shared.Utilities;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class ApplicationCategorizerTests
{
    [Fact]
    public void GetAllCategories_DoesNotIncludeInferredEntries()
    {
        // "mybrowser" is not in the known map; inference will hit -> "Browser"
        ApplicationCategorizer.Categorize("mybrowser");
        var categories = ApplicationCategorizer.GetAllCategories().ToList();
        // The inferred "mybrowser" key must not pollute the query output.
        // "Other" is never a known-map value, so its absence confirms no bleed.
        Assert.DoesNotContain("Other", categories);
        Assert.Contains("Browser", categories);
        // Verify the inferred key itself is not in the known-map values enumeration
        Assert.DoesNotContain("mybrowser", ApplicationCategorizer.GetApplicationsByCategory("Browser"));
    }

    [Fact]
    public void GetApplicationsByCategory_DoesNotIncludeInferredEntries()
    {
        // Trigger inference for a name not in the known map
        ApplicationCategorizer.Categorize("anotherbrowser");
        var browsers = ApplicationCategorizer.GetApplicationsByCategory("Browser").ToList();
        Assert.DoesNotContain("anotherbrowser", browsers);
    }

    [Theory]
    [InlineData("rider64",   "rider")]
    [InlineData("idea64",    "idea")]
    [InlineData("obs32",     "obs")]
    [InlineData("appx86",    "app")]
    [InlineData("appx64",    "app")]
    [InlineData("appx8664",  "app")]    // x86_64 after NormalizeProcessName strips '_' -> x8664
    [InlineData("chrome",    "chrome")] // no suffix -> unchanged
    [InlineData("code",      "code")]   // no suffix -> unchanged
    [InlineData("app64bit",  "app64bit")] // '64' is not at the trailing position
    [InlineData("",          "")]         // empty input -> unchanged
    public void StripArchSuffix_RemovesOnlyTrailingArchTokens(string input, string expected)
    {
        var result = ApplicationCategorizer.StripArchSuffix(input.AsSpan());
        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    // Arch suffix stripping enables correct inference
    [InlineData("somestudio64",  "IDE")]      // strip 64 -> somestudio -> EndsWith("studio")
    [InlineData("myide32",       "IDE")]      // strip 32 -> myide -> EndsWith("ide")
    [InlineData("mybrowser",     "Browser")]  // EndsWith("browser")
    [InlineData("xterm",         "Terminal")] // EndsWith("term")
    [InlineData("bashterm32",    "Terminal")] // strip 32 -> bashterm -> EndsWith("term")
    [InlineData("teamchat",      "Comms")]    // EndsWith("chat")
    [InlineData("videomail",     "Email")]    // EndsWith("mail")
    [InlineData("localdb",       "Database")] // EndsWith("db")
    [InlineData("mediaplayer64", "Media")]    // strip 64 -> mediaplayer -> EndsWith("player")
    [InlineData("mydesign",      "Design")]   // EndsWith("design")
    [InlineData("mygame",        "Gaming")]   // EndsWith("game")
    [InlineData("gamelauncher",  "Gaming")]   // EndsWith("launcher")
    [InlineData("filemanager",   "FileManager")] // EndsWith("manager")
    [InlineData("quicknote",     "Office")]   // EndsWith("note")
    [InlineData("rdpclient",     "RemoteAccess")] // Contains("rdp")
    [InlineData("vncclient",     "RemoteAccess")] // Contains("vnc")
    [InlineData("servicebroker", "System")]   // Contains("broker")
    [InlineData("randomapp",     "Other")]    // no pattern match
    // Additional coverage per arm
    [InlineData("myshell",      "Terminal")]  // EndsWith("shell")
    [InlineData("firebase",     "Database")]  // EndsWith("base")
    [InlineData("nosqldb",      "Database")]  // EndsWith("db") — no suffix stripping needed
    [InlineData("screencast",   "Media")]     // EndsWith("cast")
    [InlineData("mygames64",    "Gaming")]    // strip 64 -> mygames -> EndsWith("games")
    [InlineData("filefm",       "FileManager")] // EndsWith("fm")
    [InlineData("myfiles",      "FileManager")] // EndsWith("files")
    [InlineData("officedocs",   "Office")]    // EndsWith("docs")
    [InlineData("remotesession","RemoteAccess")] // Contains("remote")
    [InlineData("mysvchost",    "System")]    // EndsWith("host")
    [InlineData("mysvc",        "System")]    // EndsWith("svc")
    public void InferFromName_ReturnsCorrectCategory(string normalizedName, string expectedCategory)
    {
        // Call InferFromName directly — bypasses s_inferredCache entirely.
        // Input must be an already-normalized name (lowercase, alphanumeric only),
        // exactly as produced by NormalizeProcessName.
        var result = ApplicationCategorizer.InferFromName(normalizedName);
        Assert.Equal(expectedCategory, result);
    }
}
