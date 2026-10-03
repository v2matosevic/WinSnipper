using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Version2.Capture;

public record CaptureDestination(string WorkspaceId, string WorkspaceName, string TargetId, string TargetName, string SessionId, string Window, string? ConversationKey = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => $"{WorkspaceName} / {TargetName}";
}
public record CaptureImage(string Name, string Sha256, string Data);
public record CaptureSubmission(string OperationId, CaptureDestination Destination, string Text, string Source, string CaptureId, string Revision, CaptureImage? Image = null);
public record CaptureReceipt(string OperationId, CaptureDestination Destination, string State, long CreatedAt, string Message);
public record CaptureEndpoint(string Url, string Token, string InstanceId);
public record CaptureResult(bool Accepted, string Message, string OperationId, string State);

// This contract client is deliberately in-box C# and ships in each standalone
// provider. Both copies are qualified against the same JSON/Rust fixture.
public sealed class CaptureClient
{
    public const string Service = "hephaestus-capture";
    public const int MaxImageBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly string _outbox;
    private readonly HttpClient _http;
    private readonly Func<CaptureEndpoint?> _resolve;
    public CaptureClient(string outbox, HttpClient? http = null, Func<CaptureEndpoint?>? resolve = null)
    {
        _outbox = outbox;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        _http.Timeout = TimeSpan.FromSeconds(12);
        _resolve = resolve ?? ReadEndpoint;
    }
    private static CaptureEndpoint? ReadEndpoint()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Version2", "services", Service + ".json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("service").GetString() != Service || root.GetProperty("recordVersion").GetInt32() != 1) return null;
            var pid = root.GetProperty("pid").GetInt32();
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return null;
            var url = root.GetProperty("url").GetString()!;
            var token = root.GetProperty("token").GetString()!;
            var instance = root.GetProperty("instanceId").GetString()!;
            return IsLocal(url) && token.Length >= 32 && Guid.TryParse(instance, out _) ? new(url, token, instance) : null;
        }
        catch { return null; }
    }
    public static bool IsLocal(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "http" && uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private async Task<CaptureEndpoint> ConnectAsync(CancellationToken token)
    {
        var endpoint = _resolve() ?? throw new InvalidOperationException("Open an ADE build with Capture support, then refresh.");
        if (!IsLocal(endpoint.Url)) throw new InvalidOperationException("Capture destination must be on this computer.");
        using var response = await _http.GetAsync(new Uri(new Uri(endpoint.Url), "/health"), token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = document.RootElement;
        if (root.GetProperty("service").GetString() != Service || root.GetProperty("instanceId").GetString() != endpoint.InstanceId
            || root.GetProperty("protocolVersion").GetInt32() != 1)
            throw new InvalidOperationException("That endpoint is not the expected ADE capture service. Nothing was sent.");
        return endpoint;
    }
    private HttpRequestMessage Request(CaptureEndpoint endpoint, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(endpoint.Url), path));
        request.Headers.Add("x-capture-token", endpoint.Token);
        return request;
    }
    public async Task<List<CaptureDestination>> DestinationsAsync(CancellationToken token = default)
    {
        var endpoint = await ConnectAsync(token);
        using var request = Request(endpoint, HttpMethod.Get, "/destinations");
        using var response = await _http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.GetProperty("destinations").Deserialize<List<CaptureDestination>>(Json) ?? [];
    }
    public static CaptureSubmission Text(CaptureDestination destination, string text) =>
        new(Guid.NewGuid().ToString("D"), destination, text, "talkty", Guid.NewGuid().ToString("D"), "1");
    public static CaptureSubmission Screenshot(CaptureDestination destination, string name, byte[] bytes)
    {
        if (bytes.Length > MaxImageBytes || bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidOperationException("Choose a PNG screenshot of up to 8 MiB.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(Guid.NewGuid().ToString("D"), destination, "", "winsnipper", Guid.NewGuid().ToString("D"), hash,
            new(Path.GetFileName(name), hash, Convert.ToBase64String(bytes)));
    }
    private string PendingPath(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidOperationException("Invalid capture operation ID.");
        return Path.Combine(_outbox, id + ".capture");
    }
    private void Save(CaptureSubmission submission)
    {
        Directory.CreateDirectory(_outbox);
        var path = PendingPath(submission.OperationId);
        var json = JsonSerializer.Serialize(submission, Json);
        if (File.Exists(path))
        {
            if (Encoding.UTF8.GetString(Protect(File.ReadAllBytes(path), false)) != json)
                throw new InvalidOperationException("This operation ID already belongs to another capture.");
            return;
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = Protect(Encoding.UTF8.GetBytes(json), true);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temp, path);
    }
    public List<CaptureSubmission> Pending()
    {
        if (!Directory.Exists(_outbox)) return [];
        return Directory.EnumerateFiles(_outbox, "*.capture").Select(path =>
            JsonSerializer.Deserialize<CaptureSubmission>(Protect(File.ReadAllBytes(path), false), Json)
                ?? throw new InvalidDataException("A saved capture could not be read.")).ToList();
    }
    public Task<CaptureResult> RetryAsync(CaptureSubmission submission, CancellationToken token = default) => StageAsync(submission, token);
    public async Task<CaptureResult> StageAsync(CaptureSubmission submission, CancellationToken token = default)
    {
        // Keep the exact frozen destination and bytes before touching a socket.
        // A retry uses this ID and payload, even when the current selection changed.
        await Task.Run(() => Save(submission), token);
        CaptureEndpoint? endpoint = null;
        try
        {
            endpoint = await ConnectAsync(token);
            using var request = Request(endpoint, HttpMethod.Post, "/drafts");
            request.Content = new StringContent(JsonSerializer.Serialize(submission, Json), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, token);
            if (response.IsSuccessStatusCode)
                return ReadReceipt(await response.Content.ReadAsStringAsync(token), submission);
            var message = "The selected draft could not accept this capture. It is saved here for retry.";
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (error.RootElement.TryGetProperty("error", out var detail)) message = detail.GetString() ?? message;
            return new(false, message, submission.OperationId, "saved");
        }
        catch (Exception) when (endpoint is not null && !token.IsCancellationRequested)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var request = Request(endpoint, HttpMethod.Get, "/operations/" + submission.OperationId);
                using var response = await _http.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode) return ReadReceipt(await response.Content.ReadAsStringAsync(timeout.Token), submission);
            }
            catch { /* Reconciliation never retries the POST. */ }
            return new(false, "Delivery could not be verified. Your capture is saved; retry uses the same receipt.", submission.OperationId, "uncertain");
        }
        catch (Exception ex)
        { return new(false, token.IsCancellationRequested ? "Cancelled. This capture remains saved for receipt lookup." : ex.Message, submission.OperationId, "saved"); }
    }
    private CaptureResult ReadReceipt(string body, CaptureSubmission submission)
    {
        var receipt = JsonSerializer.Deserialize<CaptureReceipt>(body, Json) ?? throw new InvalidDataException("Receipt is unavailable.");
        if (receipt.OperationId != submission.OperationId || receipt.Destination != submission.Destination)
            throw new InvalidDataException("Capture receipt does not match its destination.");
        var accepted = receipt.State is "staged" or "submitted";
        if (accepted) File.Delete(PendingPath(submission.OperationId));
        return new(accepted, receipt.Message, receipt.OperationId, receipt.State);
    }

    // Windows DPAPI, with the UI forbidden. Avoids a NuGet dependency in the
    // standalone WinSnipper flavor; no plain speech/media/token outbox on disk.
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, out IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default; IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("The encrypted capture store could not be accessed by this Windows user.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); if (description != IntPtr.Zero) LocalFree(description); }
    }
}
