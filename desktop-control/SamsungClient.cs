using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TVCitation.Remote;

internal sealed record TvInformation(string Model, string OperatingSystem, string DeveloperMode);

internal sealed class SamsungClient : IDisposable
{
    internal const string Address = "192.168.1.5";
    private const string CertificateSha256 = "8fd97833aeaf03254ee936530e3247b40b034684b3333dabf93e2fd87631fa48";
    private const string BrowserPath = "api/v2/applications/3202010022079";
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "KEY_UP", "KEY_DOWN", "KEY_LEFT", "KEY_RIGHT", "KEY_ENTER", "KEY_RETURN",
        "KEY_HOME", "KEY_MENU", "KEY_EXIT", "KEY_VOLUP", "KEY_VOLDOWN", "KEY_MUTE"
    };
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false })
    {
        BaseAddress = new Uri($"http://{Address}:8001/"),
        Timeout = TimeSpan.FromSeconds(3)
    };
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private ClientWebSocket? socket;
    private CancellationTokenSource? session;
    private volatile bool connected;
    private bool disposed;

    internal bool IsConnected => connected && socket?.State == WebSocketState.Open;
    internal event Action<bool, string>? ConnectionChanged;

    internal async Task ConnectAsync(CancellationToken cancellationToken)
    {
        Disconnect();
        var tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TVCITATION", "samsung-token.dpapi");
        string token;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(tokenPath, cancellationToken);
            var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try { token = Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            if (string.IsNullOrWhiteSpace(token)) throw new CryptographicException();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            throw new RemoteException("Cannot read the local pairing credential. Run this app as the Windows user that paired the TV.");
        }

        var currentSocket = new ClientWebSocket();
        var certificateMismatch = false;
        currentSocket.Options.Proxy = null;
        currentSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        // Samsung sends its own ping frames but may not answer client pings.
        // Zero selects unsolicited pong keep-alives instead of ping/pong timeout checks.
        currentSocket.Options.KeepAliveTimeout = TimeSpan.Zero;
        currentSocket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            var matches = certificate is not null && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(CertificateSha256));
            certificateMismatch = !matches;
            return matches;
        };
        session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        socket = currentSocket;
        var authorized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var name = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes("TVCITATION")));
        // The existing pairing token is used only in memory for this connection; never log the URI.
        var endpoint = new Uri($"wss://{Address}:8002/api/v2/channels/samsung.remote.control?name={name}&token={Uri.EscapeDataString(token)}");
        token = string.Empty;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await currentSocket.ConnectAsync(endpoint, timeout.Token);
            _ = ReceiveAsync(currentSocket, authorized, session.Token);
            await authorized.Task.WaitAsync(timeout.Token);
            if (currentSocket.State != WebSocketState.Open) throw new WebSocketException();
            connected = true;
            ConnectionChanged?.Invoke(true, "Remote connected; this does not grant system file access.");
        }
        catch (RemoteException)
        {
            Disconnect();
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Disconnect();
            throw new RemoteException(certificateMismatch
                ? "The TV certificate does not match the paired device; connection stopped."
                : "Could not connect the remote. Keep the TV on and make sure the PC and TV share a local network.");
        }
    }

    private async Task ReceiveAsync(ClientWebSocket currentSocket, TaskCompletionSource<bool> authorized,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested && currentSocket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await currentSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (message.Length + result.Count > 131072) throw new WebSocketException();
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Text) continue;
                try
                {
                    using var document = JsonDocument.Parse(message.ToArray());
                    if (!document.RootElement.TryGetProperty("event", out var eventElement)) continue;
                    var eventName = eventElement.GetString();
                    if (eventName == "ms.channel.connect") authorized.TrySetResult(true);
                    else if (eventName is "ms.channel.unauthorized" or "ms.channel.timeOut")
                    {
                        authorized.TrySetException(new RemoteException("The TV did not authorize this pairing credential. Check device connection permissions on the TV."));
                        return;
                    }
                }
                catch (JsonException) { /* Unrelated or malformed events do not authorize controls. */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            authorized.TrySetException(new RemoteException("The TV remote connection closed. Please reconnect."));
            if (ReferenceEquals(socket, currentSocket))
            {
                connected = false;
                ConnectionChanged?.Invoke(false, "Remote connection closed. Please reconnect.");
            }
        }
    }

    internal async Task SendKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (!AllowedKeys.Contains(key)) throw new ArgumentOutOfRangeException(nameof(key));
        if (!IsConnected) throw new RemoteException("Not connected to the TV; the key was not sent.");
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected) throw new RemoteException("Connection lost; the key was not sent.");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                method = "ms.remote.control",
                @params = new { Cmd = "Click", DataOfCmd = key, Option = "false", TypeOfRemote = "SendRemoteKey" }
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await socket!.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            Disconnect();
            ConnectionChanged?.Invoke(false, "Send failed and the connection stopped. Please reconnect.");
            if (cancellationToken.IsCancellationRequested) throw;
            throw new RemoteException("The key was not sent completely. Reconnect and try again.");
        }
        finally { sendGate.Release(); }
    }

    internal async Task<TvInformation> ReadInformationAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("api/v2/", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var device = root.TryGetProperty("device", out var value) ? value : root;
        var model = ReadText(device, "modelName");
        if (model == "not reported") model = ReadText(device, "model");
        var developer = ReadText(device, "developerMode");
        developer = developer.ToLowerInvariant() switch
        {
            "0" or "false" => "off", "1" or "true" => "on", _ => developer
        };
        return new TvInformation(model, ReadText(device, "OS"), developer);
    }

    internal async Task<bool> OpenBrowserAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected) throw new RemoteException("The remote is not connected; the browser request was not sent.");
        using (var response = await http.PostAsync(BrowserPath, null, cancellationToken))
            response.EnsureSuccessStatusCode();

        using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        polling.CancelAfter(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();
        try
        {
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                try
                {
                    using var status = await http.GetAsync(BrowserPath, polling.Token);
                    if (status.IsSuccessStatusCode)
                    {
                        using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync(polling.Token));
                        if (document.RootElement.TryGetProperty("visible", out var visible) &&
                            (visible.ValueKind == JsonValueKind.True ||
                             visible.ValueKind == JsonValueKind.String && visible.GetString() == "true")) return true;
                    }
                }
                catch (HttpRequestException) { }
                catch (JsonException) { }
                catch (OperationCanceledException) when (!polling.IsCancellationRequested) { }
                await Task.Delay(500, polling.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return false;
    }

    private static string ReadText(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var field) &&
        field.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? field.ToString() : "not reported";

    private void Disconnect()
    {
        connected = false;
        var oldSocket = socket;
        socket = null;
        session?.Cancel();
        session?.Dispose();
        session = null;
        oldSocket?.Abort();
        oldSocket?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Disconnect();
        http.Dispose();
    }
}

internal sealed class RemoteException(string message) : Exception(message);
