using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpSandbox.Docker;

/// <summary>An error the Docker Engine answered with.</summary>
public sealed class DockerEngineException(HttpStatusCode status, string message) : Exception($"Docker: {message} ({(int)status})")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The few Docker Engine API calls the provider makes (API 1.44, Docker 25 and later), over a Unix socket
/// (unix:///var/run/docker.sock), a Windows named pipe (npipe://./pipe/docker_engine) or TCP (http://host:2375).
/// No client library: the calls are plain HTTP and JSON.
/// </summary>
internal sealed class DockerEngine : IDisposable
{
    private const string Api = "/v1.44";
    private readonly HttpClient http;

    public DockerEngine(string endpoint)
    {
        Uri uri = new(endpoint);
        SocketsHttpHandler handler = new() { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) };
        Uri address;
        switch (uri.Scheme)
        {
            case "unix":
                string socket = uri.AbsolutePath;
                handler.ConnectCallback = async (_, ct) =>
                {
                    Socket s = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                        return new NetworkStream(s, ownsSocket: true);
                    }
                    catch
                    {
                        s.Dispose();
                        throw;
                    }
                };
                address = new Uri("http://docker");
                break;
            case "npipe":
                // npipe://./pipe/docker_engine: the server is the host part, the pipe's name the path after /pipe/.
                string server = string.IsNullOrEmpty(uri.Host) ? "." : uri.Host;
                string pipe = uri.AbsolutePath.StartsWith("/pipe/", StringComparison.OrdinalIgnoreCase) ? uri.AbsolutePath[6..] : uri.AbsolutePath.TrimStart('/');
                handler.ConnectCallback = async (_, ct) =>
                {
                    NamedPipeClientStream p = new(server, pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await p.ConnectAsync(ct);
                        return p;
                    }
                    catch
                    {
                        await p.DisposeAsync();
                        throw;
                    }
                };
                address = new Uri("http://docker");
                break;
            case "http" or "tcp":
                address = new Uri($"http://{uri.Authority}");
                break;
            default:
                throw new ArgumentException($"'{endpoint}' isn't a Docker endpoint (unix://, npipe:// or http://).", nameof(endpoint));
        }

        http = new HttpClient(handler) { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string DefaultEndpoint => OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock";

    public async Task<bool> ImageExistsAsync(string reference, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.GetAsync($"{Api}/images/{reference}/json", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureAsync(res, ct);
        return true;
    }

    /// <summary>Pulls an image; the progress stream is read to its end, and an error in it is thrown.</summary>
    public async Task PullAsync(string reference, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.PostAsync($"{Api}/images/create?fromImage={Uri.EscapeDataString(reference)}", null, ct);
        await EnsureAsync(res, ct);
        using StreamReader reader = new(await res.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Contains("\"error\"", StringComparison.Ordinal) && JsonNode.Parse(line)?["error"]?.GetValue<string>() is { } error)
            {
                throw new DockerEngineException(HttpStatusCode.InternalServerError, error);
            }
        }
    }

    public async Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.PostAsync($"{Api}/containers/create?name={Uri.EscapeDataString(name)}", Json(body), ct);
        await EnsureAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!["Id"]!.GetValue<string>();
    }

    public async Task PutArchiveAsync(string id, string path, byte[] tar, CancellationToken ct)
    {
        using ByteArrayContent content = new(tar);
        content.Headers.ContentType = new("application/x-tar");
        using HttpResponseMessage res = await http.PutAsync($"{Api}/containers/{id}/archive?path={Uri.EscapeDataString(path)}", content, ct);
        await EnsureAsync(res, ct);
    }

    /// <summary>A path's tar from the container, or null when it isn't there.</summary>
    public async Task<Stream?> GetArchiveAsync(string id, string path, CancellationToken ct)
    {
        HttpResponseMessage res = await http.GetAsync($"{Api}/containers/{id}/archive?path={Uri.EscapeDataString(path)}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            res.Dispose();
            return null;
        }

        await EnsureAsync(res, ct);
        return new OwningStream(await res.Content.ReadAsStreamAsync(ct), res);
    }

    public async Task StartAsync(string id, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.PostAsync($"{Api}/containers/{id}/start", null, ct);
        if (res.StatusCode != HttpStatusCode.NotModified)
        {
            await EnsureAsync(res, ct);
        }
    }

    /// <summary>Waits for the container to stop and gives its exit code.</summary>
    public async Task<int> WaitAsync(string id, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.PostAsync($"{Api}/containers/{id}/wait?condition=not-running", null, ct);
        await EnsureAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!["StatusCode"]!.GetValue<int>();
    }

    public async Task KillAsync(string id, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.PostAsync($"{Api}/containers/{id}/kill", null, ct);
        if (res.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Conflict))
        {
            await EnsureAsync(res, ct);
        }
    }

    /// <summary>Removes the container and its volumes, running or not; one that's gone already is fine.</summary>
    public async Task RemoveContainerAsync(string id, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.DeleteAsync($"{Api}/containers/{id}?force=1&v=1", ct);
        if (res.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Conflict))
        {
            await EnsureAsync(res, ct);
        }
    }

    /// <summary>The container's stdout and stderr, each up to a cap.</summary>
    public async Task<(string Stdout, string Stderr, bool Truncated)> LogsAsync(string id, long cap, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.GetAsync($"{Api}/containers/{id}/logs?stdout=1&stderr=1", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(res, ct);
        Demux demux = new(cap);
        await demux.ReadAsync(await res.Content.ReadAsStreamAsync(ct), stopAtCap: true, ct);
        return demux.Result;
    }

    public async Task<string> ExecCreateAsync(string id, JsonObject body, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.PostAsync($"{Api}/containers/{id}/exec", Json(body), ct);
        await EnsureAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!["Id"]!.GetValue<string>();
    }

    /// <summary>
    /// Runs an exec and reads its output until it ends; past the cap, output is read and dropped so the command
    /// doesn't block on a full pipe. A cancellation stops reading and gives what was read.
    /// </summary>
    public async Task<(string Stdout, string Stderr, bool Truncated, bool Cancelled)> ExecStartAsync(string execId, long cap, CancellationToken ct)
    {
        Demux demux = new(cap);
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Post, $"{Api}/exec/{execId}/start") { Content = Json(new JsonObject { ["Detach"] = false, ["Tty"] = false }) };
            using HttpResponseMessage res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureAsync(res, ct);
            await demux.ReadAsync(await res.Content.ReadAsStreamAsync(ct), stopAtCap: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            (string o, string e, bool t) = demux.Result;
            return (o, e, t, true);
        }

        (string stdout, string stderr, bool truncated) = demux.Result;
        return (stdout, stderr, truncated, false);
    }

    public async Task<int?> ExecExitCodeAsync(string execId, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.GetAsync($"{Api}/exec/{execId}/json", ct);
        await EnsureAsync(res, ct);
        JsonObject exec = (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        return exec["Running"]?.GetValue<bool>() == true ? null : exec["ExitCode"]?.GetValue<int?>();
    }

    /// <summary>Saves a container's root as an image with labels, and gives the image's ID.</summary>
    public async Task<string> CommitAsync(string id, string repo, string tag, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        string changes = string.Join("&", labels.Select(l => "changes=" + Uri.EscapeDataString($"LABEL {l.Key}=\"{l.Value}\"")));
        using HttpResponseMessage res = await http.PostAsync(
            $"{Api}/commit?container={id}&repo={Uri.EscapeDataString(repo)}&tag={Uri.EscapeDataString(tag)}&pause=true&{changes}", Json(new JsonObject()), ct);
        await EnsureAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!["Id"]!.GetValue<string>();
    }

    public async Task<JsonArray> ListContainersAsync(string label, CancellationToken ct)
    {
        string filters = Uri.EscapeDataString(new JsonObject { ["label"] = new JsonArray(label) }.ToJsonString());
        using HttpResponseMessage res = await http.GetAsync($"{Api}/containers/json?all=1&filters={filters}", ct);
        await EnsureAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<JsonArray>(ct))!;
    }

    public async Task<JsonArray> ListImagesAsync(string label, CancellationToken ct)
    {
        string filters = Uri.EscapeDataString(new JsonObject { ["label"] = new JsonArray(label) }.ToJsonString());
        using HttpResponseMessage res = await http.GetAsync($"{Api}/images/json?filters={filters}", ct);
        await EnsureAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<JsonArray>(ct))!;
    }

    public async Task RemoveImageAsync(string id, CancellationToken ct)
    {
        using HttpResponseMessage res = await http.DeleteAsync($"{Api}/images/{id}", ct);
        if (res.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureAsync(res, ct);
        }
    }

    public void Dispose() => http.Dispose();

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task EnsureAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            return;
        }

        string text = await res.Content.ReadAsStringAsync(ct);
        string message = text;
        try
        {
            message = JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text;
        }
        catch (JsonException)
        {
        }

        throw new DockerEngineException(res.StatusCode, message.Trim());
    }

    /// <summary>Docker's multiplexed stream (a container without a TTY): 8-byte frames saying stdout or stderr and the length.</summary>
    private sealed class Demux(long cap)
    {
        private readonly MemoryStream stdout = new();
        private readonly MemoryStream stderr = new();
        private bool truncated;

        public (string, string, bool) Result => (Text(stdout), Text(stderr), truncated);

        public async Task ReadAsync(Stream stream, bool stopAtCap, CancellationToken ct)
        {
            byte[] header = new byte[8];
            byte[] buffer = new byte[16 * 1024];
            while (await FillAsync(stream, header, ct))
            {
                int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                MemoryStream target = header[0] == 2 ? stderr : stdout;
                while (length > 0)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length)), ct);
                    if (read == 0)
                    {
                        return;
                    }

                    length -= read;
                    long room = cap - target.Length;
                    if (room >= read)
                    {
                        target.Write(buffer, 0, read);
                    }
                    else
                    {
                        if (room > 0)
                        {
                            target.Write(buffer, 0, (int)room);
                        }

                        truncated = true;
                        if (stopAtCap && stdout.Length >= cap && stderr.Length >= cap)
                        {
                            return;
                        }
                    }
                }
            }
        }

        private static async Task<bool> FillAsync(Stream stream, byte[] header, CancellationToken ct)
        {
            int got = 0;
            while (got < header.Length)
            {
                int read = await stream.ReadAsync(header.AsMemory(got), ct);
                if (read == 0)
                {
                    return false;
                }

                got += read;
            }

            return true;
        }

        private static string Text(MemoryStream s) => Encoding.UTF8.GetString(s.GetBuffer(), 0, (int)s.Length);
    }

    /// <summary>A response's stream that disposes the response with it.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
