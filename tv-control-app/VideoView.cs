using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TVCitation.Control;

internal sealed class VideoView : Panel
{
    private readonly Uri endpoint;
    private readonly BackendClient backend;
    private readonly string userDataDirectory;
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(8, 12, 18), Visible = false };
    private readonly System.Windows.Forms.Timer pageRetry = new() { Interval = 5000 };
    private bool initialized;
    private bool initializing;
    private bool disposed;

    public event Action<JsonElement>? PlayerMessage;
    public event Action<string>? PlayerError;

    public VideoView(Uri endpoint, BackendClient backend, string? browserDataDirectory = null)
    {
        this.endpoint = endpoint;
        this.backend = backend;
        userDataDirectory = browserDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TVCITATION", "Control", "WebView2");
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(8, 12, 18);
        Visible = false;
        Controls.Add(browser);
        pageRetry.Tick += (_, _) => { if (initialized && !disposed) browser.CoreWebView2.Navigate(new Uri(endpoint, "/player").AbsoluteUri); };
    }

    public async Task StartAsync()
    {
        if (disposed || initializing || initialized) return;
        initializing = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataDirectory);
            if (disposed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (disposed) return;
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (SameBackend(args.Request.Uri))
                {
                    try { args.Request.Headers.SetHeader("X-TVCitation-Token", backend.ReadToken()); }
                    catch { args.Response = environment.CreateWebResourceResponse(null, 503, "Service Unavailable", ""); }
                }
                else if (!AllowedBlob(args.Request.Uri))
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.NavigationStarting += (_, args) =>
            {
                if (!IsPlayerPage(args.Uri)) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += (_, args) =>
            {
                if (disposed) return;
                if (args.IsSuccess && args.HttpStatusCode is >= 200 and < 300) { pageRetry.Stop(); Visible = true; browser.Visible = true; }
                else
                {
                    browser.Visible = false;
                    Visible = false;
                    pageRetry.Start();
                    PlayerError?.Invoke("Live picture unavailable. The remote and PC casting still work.");
                }
            };
            core.WebMessageReceived += (_, args) =>
            {
                if (!IsPlayerPage(args.Source)) return;
                try
                {
                    using var document = JsonDocument.Parse(args.WebMessageAsJson);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (Environment.GetEnvironmentVariable("TVCITATION_PLAYBACK_DIAGNOSTICS") == "1" &&
                            document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "videoPerf" &&
                            args.WebMessageAsJson.Length < 4096)
                        {
                            // Optional bounded local metrics, never video pixels or input contents.
                            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(userDataDirectory)!, $"playback-metrics-{Environment.ProcessId}.json"), args.WebMessageAsJson); }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                        PlayerMessage?.Invoke(document.RootElement.Clone());
                    }
                }
                catch (JsonException) { }
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PlayerInputBridge.Script);
            initialized = true;
            core.Navigate(new Uri(endpoint, "/player").AbsoluteUri);
        }
        catch (Exception exception)
        {
            if (!disposed) PlayerError?.Invoke("Live picture unavailable: " + exception.Message);
        }
        finally { initializing = false; }
    }

    public void UpdateInputAvailability(bool connected, bool pointerEnabled)
    {
        if (!initialized || disposed) return;
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "inputState", connected, pointerEnabled }));
    }

    internal bool SameBackend(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp && uri.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == endpoint.Port && string.IsNullOrEmpty(uri.UserInfo);

    private bool AllowedBlob(string value) => value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) && SameBackend(value[5..]);
    private bool IsPlayerPage(string value) => SameBackend(value) && new Uri(value).AbsolutePath == "/player";

    protected override void Dispose(bool disposing)
    {
        disposed = true;
        if (disposing) { pageRetry.Stop(); pageRetry.Dispose(); browser.Dispose(); }
        base.Dispose(disposing);
    }
}
