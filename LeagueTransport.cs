using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace LolMatchAssistant;

internal record ClientConnection(int Pid, int Port, string Secret)
{
    public override string ToString() => $"League client {Pid}";
}

internal static class ClientDiscovery
{
    private const uint QueryLimitedInformation = 0x1000;

    public static ClientConnection? Find(string? selectedLockfile)
    {
        if (selectedLockfile is not null && ReadLockfile(selectedLockfile) is { } selected) return selected;
        foreach (var process in Process.GetProcessesByName("LeagueClientUx"))
        {
            using (process)
            {
                using var handle = OpenProcess(QueryLimitedInformation, false, process.Id);
                if (handle.IsInvalid) continue;
                var arguments = ReadArguments(handle);
                var port = Regex.Match(arguments, "--app-port=(\\d+)");
                var token = Regex.Match(arguments, "--remoting-auth-token=([^\\s\"]+)");
                if (port.Success && token.Success && int.TryParse(port.Groups[1].Value, out var value) && ValidPort(value))
                    return new(process.Id, value, token.Groups[1].Value);
                var path = new StringBuilder(32768);
                var length = path.Capacity;
                if (QueryFullProcessImageName(handle, 0, path, ref length) &&
                    Path.GetDirectoryName(path.ToString()) is { } directory &&
                    ReadLockfile(Path.Combine(directory, "lockfile")) is { } found) return found;
            }
        }
        return null;
    }

    internal static ClientConnection? ReadLockfile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024) return null;
            using var reader = new StreamReader(stream);
            var fields = reader.ReadToEnd().Trim().Split(':');
            if (fields.Length != 5 || fields[0] is not ("LeagueClient" or "LeagueClientUx") ||
                !int.TryParse(fields[1], out var pid) || !int.TryParse(fields[2], out var port) ||
                !ValidPort(port) || fields[3].Length == 0 || fields[4] != "https") return null;
            using var process = Process.GetProcessById(pid);
            return process.ProcessName == fields[0] ? new(pid, port, fields[3]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ValidPort(int port) => port is > 0 and <= 65535;

    private static string ReadArguments(SafeProcessHandle handle)
    {
        const int ProcessCommandLineInformation = 60;
        const int bufferSize = 65536;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, bufferSize, out _) != 0) return "";
            var length = (ushort)Marshal.ReadInt16(buffer);
            var address = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            var offset = address.ToInt64() - buffer.ToInt64();
            return length > 0 && offset >= IntPtr.Size * 2 && offset + length <= bufferSize
                ? Marshal.PtrToStringUni(address, length / 2) ?? "" : "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        IntPtr buffer, int length, out int returned);
}

internal interface ILeagueTransport
{
    Task<JsonElement> Call(HttpMethod method, string path, CancellationToken ct, object? body = null);
    Task<JsonElement> Shard(Uri url, string bearer, string version, CancellationToken ct);
    Task<JsonElement> LivePlayers(CancellationToken ct);
}

internal sealed class LeagueTransport(ClientConnection connection) : ILeagueTransport
{
    private static readonly HttpClient Local = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        // The client issues its own certificate. This handler serves only the discovered loopback listener.
        ServerCertificateCustomValidationCallback = (request, _, _, _) =>
            request.RequestUri is { Scheme: "https", Host: "127.0.0.1" }
    }) { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly HttpClient Remote = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<JsonElement> Call(HttpMethod method, string path, CancellationToken ct, object? body = null)
    {
        using var request = LocalRequest(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Send(Local, request, ct);
    }

    public Task<byte[]?> ChampionIcon(int champion, CancellationToken ct) =>
        ReadIcon($"/lol-game-data/assets/v1/champion-icons/{champion}.png", ct);

    public async Task<byte[]?> ReadIcon(string path, CancellationToken ct)
    {
        if (!path.StartsWith("/lol-game-data/assets/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("..") || path.Contains('\\') || path.Contains('?') || path.Contains('#')) return null;
        using var request = LocalRequest(HttpMethod.Get, path);
        using var response = await Local.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length is > 0 and <= 1_048_576 ? bytes : null;
    }

    private HttpRequestMessage LocalRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"https://127.0.0.1:{connection.Port}{path}");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{connection.Secret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        return request;
    }

    public async Task<JsonElement> Shard(Uri url, string bearer, string version, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.UserAgent.ParseAdd($"LeagueOfLegendsClient/{version} (rcp-be-lol-match-history)");
        request.Headers.Accept.ParseAdd("application/json");
        return await Send(Remote, request, ct);
    }

    public async Task<JsonElement> LivePlayers(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:2999/liveclientdata/playerlist");
        try { return await Send(Local, request, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return default; }
        catch (LeagueException) { return default; }
    }

    private static async Task<JsonElement> Send(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                throw new LeagueException(code, $"数据接口返回 HTTP {code}。");
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return default;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return default;
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (HttpRequestException)
        {
            throw new LeagueException(0, "无法连接数据接口。");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LeagueException(0, "请求超时，请重试。");
        }
        catch (JsonException)
        {
            throw new LeagueException(0, "数据接口返回了无法识别的内容。");
        }
    }
}

internal sealed class LeagueException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
