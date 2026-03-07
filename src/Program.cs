using EndpointSignalAgent.Tray;
using System.Windows.Forms;

namespace EndpointSignalAgent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory) && Directory.Exists(baseDirectory))
        {
            Environment.CurrentDirectory = baseDirectory;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(args));
    }
}
