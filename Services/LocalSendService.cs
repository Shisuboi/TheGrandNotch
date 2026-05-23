using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace TheGrandNotch.Services;

/// <summary>
/// Découverte des appareils LocalSend (multicast UDP 224.0.0.167:53317),
/// envoi de fichiers (HTTP client) et réception de fichiers (serveur TCP HTTP).
/// </summary>
public sealed class LocalSendService : IDisposable
{
    private const string MulticastGroup = "224.0.0.167";
    private const int    Port           = 53317;
    private const string ApiBase        = "/api/localsend/v2";

    private readonly string _fingerprint = Guid.NewGuid().ToString("N")[..16];
    private UdpClient?  _udp;
    private readonly CancellationTokenSource _cts = new();

    // DangerousAcceptAny : nécessaire pour les certs auto-signés de LocalSend iOS/Android
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    }) { Timeout = TimeSpan.FromMinutes(2) };

    public ObservableCollection<LocalSendPeer> Peers { get; } = [];

    // ── Événements réception ──────────────────────────────────────────────

    /// <summary>Levé sur le thread TCP quand un appareil veut envoyer des fichiers.</summary>
    public event Action<IncomingTransfer>? TransferRequested;

    /// <summary>Levé sur le thread TCP quand un fichier a été enregistré localement.</summary>
    public event Action<string, string>? FileReceived; // (alias, chemin local)

    // ── Sessions entrantes ────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, ActiveSession> _activeSessions = new();

    private sealed class ActiveSession
    {
        public required IncomingTransfer            Transfer   { get; init; }
        public required Dictionary<string, string>  Tokens     { get; init; } // fileId → token
        public required string                      SaveFolder { get; init; }
    }

    // ── Démarrage ──────────────────────────────────────────────────────────

    public void Start()
    {
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            _udp.JoinMulticastGroup(IPAddress.Parse(MulticastGroup));
            _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
            _ = ListenLoopAsync(_cts.Token);
            _ = AnnounceLoopAsync(_cts.Token);
        }
        catch { /* port occupé ou réseau absent */ }

        _ = TcpServerLoopAsync(_cts.Token);
    }

    // ── Découverte (UDP multicast) ─────────────────────────────────────────

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                var dto    = JsonSerializer.Deserialize<PeerDto>(
                                 Encoding.UTF8.GetString(result.Buffer));
                if (dto?.Fingerprint is null || dto.Fingerprint == _fingerprint) continue;

                var ip   = result.RemoteEndPoint.Address.ToString();
                var port = dto.Port > 0 ? dto.Port : Port;
                var peer = new LocalSendPeer(
                    dto.Alias ?? ip, ip, port,
                    dto.DeviceType ?? "mobile",
                    dto.Fingerprint,
                    dto.Protocol ?? "http");

                await OnUi(() =>
                {
                    var existing = Peers.FirstOrDefault(p => p.Fingerprint == dto.Fingerprint);
                    if (existing != null) Peers[Peers.IndexOf(existing)] = peer;
                    else                  Peers.Add(peer);
                });

                if (dto.Announce == true)
                    await SendAnnouncementAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SendAnnouncementAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(4), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendAnnouncementAsync(CancellationToken ct)
    {
        try
        {
            var dto = new PeerDto
            {
                Alias = "TheGrandNotch", Version = "2.0",
                DeviceModel = "Windows PC", DeviceType = "desktop",
                Fingerprint = _fingerprint, Port = Port,
                Protocol = "http", Download = true,   // true = on accepte les envois entrants
                Announce = true, Announcement = true
            };
            var data = JsonSerializer.SerializeToUtf8Bytes(dto);
            await _udp!.SendAsync(data, new IPEndPoint(IPAddress.Parse(MulticastGroup), Port), ct);
        }
        catch { }
    }

    // ── Envoi de fichiers (client HTTP) ───────────────────────────────────

    public async Task<bool> SendAsync(
        LocalSendPeer peer,
        IReadOnlyList<SendPayload> payloads,
        IProgress<(int done, int total)>? progress = null)
    {
        var baseUrl = $"{peer.Protocol}://{peer.Ip}:{peer.Port}{ApiBase}";

        var prepBody = new
        {
            info = new
            {
                alias = "TheGrandNotch", version = "2.0",
                deviceModel = "Windows PC", deviceType = "desktop",
                fingerprint = _fingerprint
            },
            files = payloads.ToDictionary(
                p => p.Id,
                p => (object)new
                {
                    id = p.Id, fileName = p.FileName,
                    size = p.Size, fileType = p.MimeType,
                    preview = (string?)null
                })
        };

        HttpResponseMessage prepResp;
        try   { prepResp = await _http.PostAsync($"{baseUrl}/prepare-upload", ToJson(prepBody)); }
        catch { return false; }

        if (!prepResp.IsSuccessStatusCode) return false;

        var prep = JsonSerializer.Deserialize<PrepareUploadResponse>(
            await prepResp.Content.ReadAsStringAsync());
        if (prep?.SessionId is null) return false;

        int done = 0;
        foreach (var p in payloads)
        {
            if (!prep.Files.TryGetValue(p.Id, out var token)) continue;
            try
            {
                using var stream  = p.OpenStream();
                var content       = new StreamContent(stream);
                content.Headers.ContentType   =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(p.MimeType);
                content.Headers.ContentLength = p.Size;

                var url  = $"{baseUrl}/upload?sessionId={prep.SessionId}" +
                           $"&fileId={p.Id}&token={Uri.EscapeDataString(token)}";
                var resp = await _http.PostAsync(url, content);

                if (!resp.IsSuccessStatusCode)
                {
                    try { await _http.PostAsync($"{baseUrl}/cancel?sessionId={prep.SessionId}", null); }
                    catch { }
                    return false;
                }
            }
            catch { return false; }

            progress?.Report((++done, payloads.Count));
        }
        return true;
    }

    // ── Serveur TCP HTTP (réception) ──────────────────────────────────────

    private async Task TcpServerLoopAsync(CancellationToken ct)
    {
        var tcp = new TcpListener(IPAddress.Any, Port);
        try
        {
            tcp.Start();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await tcp.AcceptTcpClientAsync(ct);
                    _ = HandleTcpClientAsync(client, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }
        catch { }
        finally { try { tcp.Stop(); } catch { } }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        client.ReceiveTimeout = 30_000;
        client.SendTimeout    = 30_000;
        try
        {
            using var stream = client.GetStream();

            // 1. Lire les en-têtes HTTP
            var (method, path, query, headers) = await ReadHttpHeadersAsync(stream, ct);

            // 2. Route
            if (method == "POST" && path.EndsWith("/prepare-upload", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadBodyAsync(stream, headers, ct);
                await HandlePrepareUploadAsync(stream, body, ct);
            }
            else if (method == "POST" && path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUploadAsync(stream, query, headers, ct);
            }
            else if (method == "POST" && path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase))
            {
                var q = ParseQuery(query);
                if (q.TryGetValue("sessionId", out var sid) &&
                    _activeSessions.TryRemove(sid, out var sess))
                    sess.Transfer.Decline();
                await WriteHttpAsync(stream, 204, "No Content", "");
            }
            else
            {
                await WriteHttpAsync(stream, 404, "Not Found", "{}");
            }
        }
        catch { }
        finally { try { client.Close(); } catch { } }
    }

    private async Task HandlePrepareUploadAsync(NetworkStream stream, string body, CancellationToken ct)
    {
        PrepareUploadRequest? req;
        try   { req = JsonSerializer.Deserialize<PrepareUploadRequest>(body); }
        catch { await WriteHttpAsync(stream, 400, "Bad Request", "{}"); return; }

        if (req is null) { await WriteHttpAsync(stream, 400, "Bad Request", "{}"); return; }

        var files = req.Files.Select(kv => new IncomingTransferFile(
            kv.Key,
            kv.Value.FileName ?? kv.Key,
            kv.Value.Size,
            kv.Value.FileType ?? "application/octet-stream")).ToList();

        var sessionId = Guid.NewGuid().ToString("N")[..16];
        var transfer  = new IncomingTransfer(sessionId, req.Info?.Alias ?? "Appareil inconnu", files);

        // Notifier l'interface utilisateur
        await OnUi(() => TransferRequested?.Invoke(transfer));

        // Attendre la décision (60 s max)
        bool accepted;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            accepted = await transfer.WaitForDecisionAsync(cts.Token);
        }
        catch { accepted = false; }

        if (!accepted) { await WriteHttpAsync(stream, 403, "Forbidden", "{}"); return; }

        // Générer un token par fichier
        var tokens  = files.ToDictionary(f => f.Id, _ => Guid.NewGuid().ToString("N")[..16]);
        var saveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(saveDir);

        _activeSessions[sessionId] = new ActiveSession
        {
            Transfer   = transfer,
            Tokens     = tokens,
            SaveFolder = saveDir
        };

        var resp = JsonSerializer.Serialize(new { sessionId, files = tokens });
        await WriteHttpAsync(stream, 200, "OK", resp);
    }

    private async Task HandleUploadAsync(NetworkStream stream, string query,
                                          Dictionary<string, string> headers, CancellationToken ct)
    {
        var q = ParseQuery(query);
        q.TryGetValue("sessionId", out var sessionId);
        q.TryGetValue("fileId",    out var fileId);
        q.TryGetValue("token",     out var token);

        if (sessionId is null || fileId is null || token is null ||
            !_activeSessions.TryGetValue(sessionId, out var session) ||
            !session.Tokens.TryGetValue(fileId,     out var expected) ||
            token != expected)
        {
            // Consommer le corps pour ne pas bloquer l'expéditeur
            await DrainBodyAsync(stream, headers, ct);
            await WriteHttpAsync(stream, 403, "Forbidden", "{}");
            return;
        }

        var file     = session.Transfer.Files.FirstOrDefault(f => f.Id == fileId);
        var fileName = SanitizeFileName(file?.FileName ?? fileId);
        var savePath = Path.Combine(session.SaveFolder, fileName);

        // Éviter l'écrasement
        if (File.Exists(savePath))
        {
            var ext  = Path.GetExtension(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            int n    = 1;
            do { savePath = Path.Combine(session.SaveFolder, $"{name} ({n++}){ext}"); }
            while (File.Exists(savePath));
        }

        // Écrire le fichier
        int contentLength = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out int cll)
            ? cll : -1;

        try
        {
            await using var fs  = File.Create(savePath);
            var buf             = new byte[81_920];
            int remaining       = contentLength >= 0 ? contentLength : int.MaxValue;

            while (remaining > 0)
            {
                int toRead = Math.Min(buf.Length, remaining);
                int read   = await stream.ReadAsync(buf.AsMemory(0, toRead), ct);
                if (read == 0) break;
                await fs.WriteAsync(buf.AsMemory(0, read), ct);
                if (contentLength >= 0) remaining -= read;
            }
        }
        catch
        {
            try { File.Delete(savePath); } catch { }
            await WriteHttpAsync(stream, 500, "Internal Server Error", "{}");
            return;
        }

        await WriteHttpAsync(stream, 204, "No Content", "");

        // Signaler la réception à l'UI
        session.Tokens.Remove(fileId);
        FileReceived?.Invoke(session.Transfer.SenderAlias, savePath);

        if (session.Tokens.Count == 0)
            _activeSessions.TryRemove(sessionId, out _);
    }

    // ── Helpers HTTP bas niveau ────────────────────────────────────────────

    private static async Task<(string Method, string Path, string Query, Dictionary<string, string> Headers)>
        ReadHttpHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf      = new byte[8192];
        int total    = 0;
        var oneByte  = new byte[1];

        while (total < buf.Length)
        {
            int r = await stream.ReadAsync(oneByte.AsMemory(), ct);
            if (r == 0) break;
            buf[total++] = oneByte[0];

            if (total >= 4 &&
                buf[total-4] == '\r' && buf[total-3] == '\n' &&
                buf[total-2] == '\r' && buf[total-1] == '\n')
                break;
        }

        var text  = Encoding.UTF8.GetString(buf, 0, total);
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        var first  = lines[0].Split(' ', 3);
        var method = first.Length > 0 ? first[0] : "GET";
        var rawUrl = first.Length > 1 ? first[1] : "/";
        var qi     = rawUrl.IndexOf('?');
        var path   = qi >= 0 ? rawUrl[..qi] : rawUrl;
        var query  = qi >= 0 ? rawUrl[(qi+1)..] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) headers[line[..idx].Trim()] = line[(idx+1)..].Trim();
        }

        return (method, path, query, headers);
    }

    private static async Task<string> ReadBodyAsync(NetworkStream stream,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!headers.TryGetValue("content-length", out var cls) || !int.TryParse(cls, out int cl) || cl <= 0)
            return "";
        var body = new byte[cl];
        await stream.ReadExactlyAsync(body.AsMemory(), ct);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task DrainBodyAsync(NetworkStream stream,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!headers.TryGetValue("content-length", out var cls) || !int.TryParse(cls, out int cl)) return;
        var discard   = new byte[4096];
        int remaining = cl;
        while (remaining > 0)
        {
            int r = await stream.ReadAsync(discard.AsMemory(0, Math.Min(discard.Length, remaining)), ct);
            if (r == 0) break;
            remaining -= r;
        }
    }

    private static async Task WriteHttpAsync(NetworkStream stream, int status, string statusText, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head      = $"HTTP/1.1 {status} {statusText}\r\n" +
                        $"Content-Type: application/json; charset=utf-8\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        $"Connection: close\r\n\r\n";
        var headBytes = Encoding.UTF8.GetBytes(head);
        await stream.WriteAsync(headBytes.AsMemory());
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes.AsMemory());
        await stream.FlushAsync();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return r;
        foreach (var part in query.Split('&'))
        {
            var i = part.IndexOf('=');
            if (i > 0) r[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i+1)..]);
        }
        return r;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    // ── Utilitaires partagés ───────────────────────────────────────────────

    private static StringContent ToJson(object obj)
        => new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    private static Task OnUi(Action a)
        => Application.Current.Dispatcher.InvokeAsync(a).Task;

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _http.Dispose();
    }

    // ── DTOs internes ──────────────────────────────────────────────────────

    private sealed class PeerDto
    {
        [JsonPropertyName("alias")]        public string? Alias        { get; set; }
        [JsonPropertyName("version")]      public string? Version      { get; set; }
        [JsonPropertyName("deviceModel")]  public string? DeviceModel  { get; set; }
        [JsonPropertyName("deviceType")]   public string? DeviceType   { get; set; }
        [JsonPropertyName("fingerprint")]  public string? Fingerprint  { get; set; }
        [JsonPropertyName("port")]         public int     Port         { get; set; }
        [JsonPropertyName("protocol")]     public string? Protocol     { get; set; }
        [JsonPropertyName("download")]     public bool?   Download     { get; set; }
        [JsonPropertyName("announce")]     public bool?   Announce     { get; set; }
        [JsonPropertyName("announcement")] public bool?   Announcement { get; set; }
    }

    private sealed class PrepareUploadResponse
    {
        [JsonPropertyName("sessionId")] public string?                    SessionId { get; set; }
        [JsonPropertyName("files")]     public Dictionary<string, string> Files     { get; set; } = [];
    }

    private sealed class PrepareUploadRequest
    {
        [JsonPropertyName("info")]  public PeerDto?                      Info  { get; set; }
        [JsonPropertyName("files")] public Dictionary<string, FileDto>   Files { get; set; } = [];
    }

    private sealed class FileDto
    {
        [JsonPropertyName("id")]       public string? Id       { get; set; }
        [JsonPropertyName("fileName")] public string? FileName { get; set; }
        [JsonPropertyName("size")]     public long    Size     { get; set; }
        [JsonPropertyName("fileType")] public string? FileType { get; set; }
    }
}
