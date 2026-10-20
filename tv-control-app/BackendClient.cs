using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TVCitation.Control;

internal sealed class BackendClient : IDisposable
{
    private readonly HttpClient client;
    private readonly string tokenPath;
    private string? token;

    public BackendClient(Uri endpoint, string? credentialPath = null)
    {
        tokenPath = credentialPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TVCITATION", "Control", "api-token");
        client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = endpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<JsonElement> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        cancellationToken = timeout.Token;
        using var request = CreateRequest(HttpMethod.Get, "/status");
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    public async Task PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(path == "/cast/start" ? 120 : 10));
        cancellationToken = timeout.Token;
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) return;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if ((root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) ||
                (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) ||
                (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False))
                throw new InvalidOperationException(StatusValue.Text(root, "error", "message") ?? "The TV did not complete this operation.");
        }
        catch (JsonException) { }
    }

    internal string ReadToken()
    {
        if (string.IsNullOrEmpty(token))
        {
            if (!File.Exists(tokenPath)) throw new IOException("Waiting for the local control service to get ready.");
            token = File.ReadAllText(tokenPath).Trim();
            if (string.IsNullOrEmpty(token)) throw new IOException("The local control service is not ready yet.");
        }
        return token;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-TVCitation-Token", ReadToken());
        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized) token = null;
        var message = $"Local service unavailable (HTTP {(int)response.StatusCode}).";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            message = StatusValue.Text(document.RootElement, "message", "error") ?? message;
        }
        catch (JsonException) { }
        throw new InvalidOperationException(message);
    }

    public static string? FindBackendLauncher()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        // publish -> tv-control-app -> workspace (three locations at most).
        for (var depth = 0; depth < 3 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "start-backend.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static void StartBackend(string launcher)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(launcher)!
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", launcher })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the local control service.");
    }

    public void Dispose() => client.Dispose();
}

internal static class StatusValue
{
    public static JsonElement? Find(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var property in root.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }
    public static bool? Boolean(JsonElement root, params string[] names)
    {
        var value = Find(root, names);
        if (value?.ValueKind == JsonValueKind.True) return true;
        if (value?.ValueKind == JsonValueKind.False) return false;
        return value?.ValueKind == JsonValueKind.String && bool.TryParse(value.Value.GetString(), out var parsed) ? parsed : null;
    }
    public static string? Text(JsonElement root, params string[] names)
    {
        var value = Find(root, names);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }
    public static double? Number(JsonElement root, params string[] names)
    {
        var value = Find(root, names);
        return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var number) ? number : null;
    }
}
