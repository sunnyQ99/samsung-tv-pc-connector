using System.Runtime.InteropServices;

namespace TVCitation.Control;

internal static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static void Main(string[] args)
    {
        // Give the app its own taskbar/window identity regardless of its launcher.
        SetCurrentProcessExplicitAppUserModelID("TVCITATION.Control");
        ApplicationConfiguration.Initialize();
        try
        {
            // Match the IPv4 listener; localhost may first retry an unbound ::1.
            var endpoint = new Uri("http://127.0.0.1:18765");
            var autoConnect = true;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--backend-url" && index + 1 < args.Length)
                    endpoint = new Uri(args[++index]);
                else if (args[index] == "--auto-connect") autoConnect = true;
                else if (args[index] == "--manual-connect") autoConnect = false;
                else
                    throw new ArgumentException("Supported options: --backend-url <local service URL> and --manual-connect.");
            }
            if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttp ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                throw new ArgumentException("The TV control service must be a local HTTP address, e.g. http://127.0.0.1:18765.");
            Application.Run(new MainForm(endpoint, autoConnect));
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Samsung TV PC Connector", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
