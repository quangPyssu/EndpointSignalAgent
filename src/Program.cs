using System.Runtime.InteropServices;
using EndpointSignalAgent.Tray;
using System.Windows.Forms;

namespace EndpointSignalAgent;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [STAThread]
    private static void Main(string[] args)
    {
        // Toggle this flag to true to enable console output for debugging
        bool enableConsoleDebug = true; 
        if (enableConsoleDebug)
        {
            AllocConsole();
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory) && Directory.Exists(baseDirectory))
        {
            Environment.CurrentDirectory = baseDirectory;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(args));
    }
}
