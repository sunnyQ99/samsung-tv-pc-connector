using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TVCitation.Control;

internal static class Checks
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "ui-check-output");
        Directory.CreateDirectory(output);
        try
        {
            if (args.Contains("--playback-only")) { PlayerPlaybackChecks.Run(output); return 0; }
            if (args.Contains("--autoplay-probe")) { PlayerPlaybackChecks.Run(output, args.Contains("--muted") ? "muted" : "default"); return 0; }
            using (var preview = new TvPreview { Size = new Size(800, 600), Image = new Bitmap(1920, 1080) })
            {
                Require(preview.ToScreenCoordinates(new Point(0, 0)) is null, "Letterbox click must not reach TV.");
                Require(preview.ToScreenCoordinates(new Point(0, 75)) == new Point(0, 0), "Image upper-left mapping failed.");
                Require(preview.ToScreenCoordinates(new Point(400, 300)) == new Point(960, 540), "Image center mapping failed.");
                Require(preview.ToScreenCoordinates(new Point(800, 300)) is null, "Outside-right click must not reach TV.");
                preview.Image.Dispose(); preview.Image = null;
            }
            using (var form = new MainForm(new Uri("http://127.0.0.1:18765"), initializeOnShown: false))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.ShowInTaskbar = false;
                form.Show();
                Application.DoEvents();
                form.PerformLayout();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "ui-offline-default.png"), ImageFormat.Png); }
                form.Size = form.MinimumSize;
                form.PerformLayout();
                using var narrow = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(narrow, new Rectangle(Point.Empty, narrow.Size));
                narrow.Save(Path.Combine(output, "ui-offline-minimum.png"), ImageFormat.Png);
                using var pending = JsonDocument.Parse("""{"controlConnected":true,"videoConnected":false,"castActive":false,"castOperation":{"busy":true,"state":"starting","message":"Waiting for system authorization"}}""");
                form.ApplyStatus(pending.RootElement);
                Require(Descendants(form).OfType<Button>().Any(item => item.Text == "Casting in progress…" && !item.Enabled), "Pending casting must disable duplicate starts.");
                Require(!Descendants(form).OfType<Label>().Any(item => item.Text == "PC screen is casting to the TV"), "Pending HTTP 202 must not claim an active stream.");
                Require(Descendants(form).OfType<Label>().Any(item => item.Text == "●  Remote connected"), "Remote connection must not imply video connection.");
                using var active = JsonDocument.Parse("""{"controlConnected":true,"videoConnected":false,"castActive":true,"castOperation":{"busy":false,"state":"completed","ok":true}}""");
                form.ApplyStatus(active.RootElement);
                Require(Descendants(form).OfType<Label>().Any(item => item.Text == "PC screen is casting to the TV"), "Confirmed active casting should be shown.");
                using var recovery = JsonDocument.Parse("""{"controlConnected":true,"videoConnected":false,"castActive":false,"castOperation":{"busy":false,"state":"error","ok":false,"needsRecovery":true,"message":"Audio restore failed"}}""");
                form.ApplyStatus(recovery.RootElement);
                Require(Descendants(form).OfType<Label>().Any(item => item.Text == "PC audio needs restoring; click Stop casting"), "Audio recovery state must be visible.");
            }
            Task.Run(CheckHttpContract).GetAwaiter().GetResult();
            CheckFastBackendRestart();
            CheckWebViewPlayer();
            Console.WriteLine("PASS: coordinate bounds, offline UI rendering, authenticated HTTP contract, fast-backend-restart reconnect/manual disconnect, WebView2 player/fetch token injection and external-resource blocking.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control parent)
    {
        foreach (System.Windows.Forms.Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void CheckFastBackendRestart()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var tokenFile = Path.Combine(Path.GetTempPath(), "tvcitation-reconnect-check-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tokenFile, "reconnect-check-credential");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var backendAutoConnect = 0;
        var connectPosts = 0;
        var disconnectPosts = 0;
        var server = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                if (context.Request.Url!.AbsolutePath == "/favicon.ico") { context.Response.StatusCode = 404; context.Response.Close(); continue; }
                Require(context.Request.Headers["X-TVCitation-Token"] == "reconnect-check-credential", "Reconnect request lacked authentication: " + context.Request.HttpMethod + " " + context.Request.Url!.AbsolutePath);
                switch (context.Request.Url!.AbsolutePath)
                {
                    case "/status":
                        var enabled = Volatile.Read(ref backendAutoConnect) != 0;
                        await Respond(context, JsonSerializer.SerializeToUtf8Bytes(new { autoConnect = enabled, controlConnected = enabled, videoConnected = false }), "application/json");
                        break;
                    case "/connect":
                        Interlocked.Increment(ref connectPosts);
                        Interlocked.Exchange(ref backendAutoConnect, 1);
                        await Respond(context, Encoding.UTF8.GetBytes("{\"ok\":true}"), "application/json");
                        break;
                    case "/disconnect":
                        Interlocked.Increment(ref disconnectPosts);
                        Interlocked.Exchange(ref backendAutoConnect, 0);
                        await Respond(context, Encoding.UTF8.GetBytes("{\"ok\":true}"), "application/json");
                        break;
                    case "/player":
                        await Respond(context, Encoding.UTF8.GetBytes("<!doctype html><title>Reconnect check</title>"), "text/html");
                        break;
                    default:
                        context.Response.StatusCode = 404; context.Response.Close();
                        break;
                }
            }
        }, timeout.Token);
        try
        {
            using var form = new MainForm(new Uri($"http://127.0.0.1:{port}"), initializeOnShown: false, credentialPath: tokenFile, browserDataDirectory: tokenFile + ".webview")
            { Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
            Exception? failure = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 20000 };
            timer.Tick += (_, _) => { failure = new TimeoutException("Reconnect check timed out."); form.Close(); };
            form.Shown += async (_, _) =>
            {
                timer.Start();
                try
                {
                    await form.PollStatusAsync();
                    Require(Volatile.Read(ref connectPosts) == 1, "Initial automatic connection was not submitted.");
                    await form.PollStatusAsync();
                    Require(Volatile.Read(ref connectPosts) == 1, "Healthy auto-connect backend received a duplicate connect request.");
                    // The HTTP listener stays online: the GUI sees no failed request between backend instances.
                    Interlocked.Exchange(ref backendAutoConnect, 0);
                    await form.PollStatusAsync();
                    Require(Volatile.Read(ref connectPosts) == 2, "Fast backend restart lost the GUI's automatic connection intent.");
                    await form.PollStatusAsync();
                    Require(Volatile.Read(ref connectPosts) == 2, "Recovered backend received a duplicate connect request.");
                    await form.DisconnectAsync();
                    Require(Volatile.Read(ref disconnectPosts) == 1, "Manual disconnect was not submitted.");
                    await form.PollStatusAsync();
                    await form.PollStatusAsync();
                    Require(Volatile.Read(ref connectPosts) == 2, "Manual disconnect was overridden by automatic reconnect.");
                }
                catch (Exception exception) { failure = exception; }
                finally { form.Close(); }
            };
            Application.Run(form);
            if (failure is not null) throw failure;
        }
        finally
        {
            timeout.Cancel(); listener.Stop(); File.Delete(tokenFile);
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } catch (HttpListenerException) { }
        }
    }

    private static void CheckWebViewPlayer()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var tokenFile = Path.Combine(Path.GetTempPath(), "tvcitation-player-check-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tokenFile, "player-check-credential");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var server = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                Require(context.Request.Headers["X-TVCitation-Token"] == "player-check-credential", "WebView2 request did not contain native-injected token.");
                if (context.Request.Url!.AbsolutePath == "/player")
                {
                    var html = """
                        <!doctype html><meta charset="utf-8"><title>Local player contract check</title>
                        <script>
                        (async () => {
                          const data = await (await fetch('/video')).text();
                          const outside = await fetch('http://127.0.0.1:1/forbidden').catch(() => null);
                          chrome.webview.postMessage({type:'checkReady',value:data,blocked:outside === null || outside.status === 403,bridge:window.__tvcitationInputBridge === true});
                        })();
                        </script>
                        """;
                    await Respond(context, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
                }
                else if (context.Request.Url!.AbsolutePath == "/video")
                    await Respond(context, Encoding.UTF8.GetBytes("authenticated-video-route"), "text/plain");
                else { context.Response.StatusCode = 404; context.Response.Close(); }
            }
        }, cancellation.Token);
        try
        {
            using var backend = new BackendClient(new Uri($"http://127.0.0.1:{port}"), tokenFile);
            using var form = new Form { Size = new Size(640, 400), Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
            using var player = new VideoView(new Uri($"http://127.0.0.1:{port}"), backend, tokenFile + ".webview");
            form.Controls.Add(player);
            Require(player.SameBackend($"http://127.0.0.1:{port}/video"), "Same-origin route rejected.");
            Require(!player.SameBackend("http://127.0.0.1:1/video"), "Different port incorrectly accepted.");
            Require(!player.SameBackend($"http://user@127.0.0.1:{port}/video"), "URL with user-info incorrectly accepted.");
            var passed = false;
            string? error = null;
            player.PlayerMessage += message =>
            {
                if (StatusValue.Text(message, "type") != "checkReady") return;
                passed = StatusValue.Text(message, "value") == "authenticated-video-route" && StatusValue.Boolean(message, "blocked") == true && StatusValue.Boolean(message, "bridge") == true;
                form.Close();
            };
            player.PlayerError += message => { error = message; form.Close(); };
            using var timer = new System.Windows.Forms.Timer { Interval = 20000 };
            timer.Tick += (_, _) => { error = "WebView2 contract check timed out."; form.Close(); };
            form.Shown += async (_, _) => { timer.Start(); await player.StartAsync(); };
            Application.Run(form);
            Require(passed, error ?? "WebView2 authenticated fetch / message check failed.");
        }
        finally
        {
            cancellation.Cancel(); listener.Stop(); File.Delete(tokenFile);
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } catch (HttpListenerException) { }
        }
    }

    private static async Task CheckHttpContract()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var tokenFile = Path.Combine(Path.GetTempPath(), "tvcitation-ui-check-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tokenFile, "local-test-credential");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 4; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                Require(context.Request.Headers["X-TVCitation-Token"] == "local-test-credential", "Authentication header missing.");
                switch (context.Request.Url!.AbsolutePath)
                {
                    case "/status":
                        await Respond(context, Encoding.UTF8.GetBytes("{\"connected\":true,\"controlConnected\":true,\"videoConnected\":false,\"pointerAvailable\":false}"), "application/json");
                        break;
                    case "/cast/start":
                        Require(context.Request.HttpMethod == "POST", "Casting must be POST.");
                        using (var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: timeout.Token))
                            Require(body.RootElement.GetProperty("tvOnlyAudio").GetBoolean(), "Casting audio policy contract mismatch.");
                        context.Response.StatusCode = 202;
                        await Respond(context, Encoding.UTF8.GetBytes("{\"ok\":true,\"pending\":true}"), "application/json");
                        break;
                    case "/text":
                        using (var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: timeout.Token))
                            Require(body.RootElement.GetProperty("text").GetString() == "Café TV ü", "Text UTF-8 contract mismatch.");
                        await Respond(context, Encoding.UTF8.GetBytes("{\"ok\":true}"), "application/json");
                        break;
                    case "/remote":
                        Require(context.Request.HttpMethod == "POST", "Remote must be POST.");
                        using (var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: timeout.Token))
                            Require(body.RootElement.GetProperty("key").GetString() == "KEY_HOME", "Remote key contract mismatch.");
                        await Respond(context, Encoding.UTF8.GetBytes("{\"ok\":true}"), "application/json");
                        break;
                    default: throw new Exception("Unexpected mock endpoint.");
                }
            }
        }, timeout.Token);
        try
        {
            using var client = new BackendClient(new Uri($"http://127.0.0.1:{port}"), tokenFile);
            var status = await client.GetStatusAsync(timeout.Token);
            Require(StatusValue.Boolean(status, "connected") == true, "Status parse failed.");
            Require(StatusValue.Boolean(status, "pointerAvailable") == false, "Pointer gating parse failed.");
            await client.PostAsync("/remote", new { key = "KEY_HOME" }, timeout.Token);
            await client.PostAsync("/text", new { text = "Café TV ü" }, timeout.Token);
            await client.PostAsync("/cast/start", new { tvOnlyAudio = true }, timeout.Token);
            await server.WaitAsync(timeout.Token);
        }
        finally { File.Delete(tokenFile); listener.Stop(); }
    }

    private static async Task Respond(HttpListenerContext context, byte[] bytes, string type)
    {
        context.Response.ContentType = type;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
