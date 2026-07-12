using EndpointSignalAgent.Tray;
using System.Windows.Forms;

namespace EndpointSignalAgent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The agent persists its enrollment/signal spool as paths relative to
        // the current working directory (FeatureStore, EnrollmentStore, the
        // SignalCollection collectors, etc.). It used to pin that to
        // AppContext.BaseDirectory (the exe's own folder) for determinism
        // regardless of how the process was launched -- but the installer
        // places the exe under Program Files, which a non-elevated logon
        // scheduled task cannot write to, so the app would start but never
        // create any state. This does NOT affect config/icon loading:
        // AgentHostBootstrap.BuildHost pins the Host's ContentRootPath to
        // AppContext.BaseDirectory explicitly (its default is actually this
        // process's CWD, which would otherwise break appsettings*.json
        // resolution once CWD moves here), and TrayApplicationContext loads
        // the icon via AppContext.BaseDirectory directly, not CWD.
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ContinuousAuth", "agent");
        Directory.CreateDirectory(dataDirectory);
        Environment.CurrentDirectory = dataDirectory;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(args));
    }
}
