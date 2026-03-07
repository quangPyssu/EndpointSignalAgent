using EndpointSignalAgent.Tray;
using System.Windows.Forms;

namespace EndpointSignalAgent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(args));
    }
}
