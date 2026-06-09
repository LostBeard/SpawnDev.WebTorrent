using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SpawnDev.WebTorrent.Server;

/// <summary>
/// Web seed server - serves torrent pieces over HTTP range requests (BEP 17/19).
/// Clients request byte ranges; the server reads from local storage and responds.
/// </summary>
public class WebSeedServer
{
    private readonly string _storageRoot;

    public WebSeedServer(string storageRoot)
    {
        _storageRoot = storageRoot;
        Directory.CreateDirectory(_storageRoot);
    }

    /// <summary>Handle an HTTP request for a file in a torrent.</summary>
    public async Task HandleRequest(HttpContext context, string infoHash, string filePath)
    {
        var localPath = Path.GetFullPath(Path.Combine(_storageRoot, infoHash, filePath));
        // SECURITY: Prevent path traversal attacks (e.g., ../../etc/passwd)
        if (!localPath.StartsWith(Path.GetFullPath(_storageRoot), StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (!System.IO.File.Exists(localPath))
        {
            context.Response.StatusCode = 404;
            return;
        }

        var fileInfo = new FileInfo(localPath);
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["Accept-Ranges"] = "bytes";

        // Handle Range header for partial content (BEP 17/19). HttpByteRange clamps an over-long last-byte-pos
        // to (length-1) per RFC 7233 §4.1 — without it an explicit over-EOF end made us promise a Content-Length
        // we could not stream, closing the body short -> net::ERR_CONTENT_LENGTH_MISMATCH in a browser fetch and
        // an unverifiable web-seed piece. (Also fixes the prior (int) length cast overflow for files > 2GB.)
        if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            switch (HttpByteRange.ParseSingle(rangeHeader.ToString(), fileInfo.Length, out long start, out long end))
            {
                case HttpByteRange.Result.Unsatisfiable:
                    context.Response.StatusCode = 416; // Range Not Satisfiable
                    context.Response.Headers["Content-Range"] = $"bytes */{fileInfo.Length}";
                    return;

                case HttpByteRange.Result.Satisfiable:
                    long length = end - start + 1;
                    context.Response.StatusCode = 206;
                    context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{fileInfo.Length}";
                    context.Response.ContentLength = length;

                    using (var fs = System.IO.File.OpenRead(localPath))
                    {
                        fs.Seek(start, SeekOrigin.Begin);
                        var buffer = new byte[(int)Math.Min(length, 65536)];
                        long remaining = length;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(remaining, buffer.Length);
                            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
                            if (read == 0) break;
                            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read));
                            remaining -= read;
                        }
                    }
                    return;

                // Result.None: not a usable single byte-range — fall through to the full-file response below.
            }
        }

        // Full file
        context.Response.ContentLength = fileInfo.Length;
        await context.Response.SendFileAsync(localPath);
    }
}

/// <summary>
/// ASP.NET Core routing helpers for <see cref="WebSeedServer"/>.
/// </summary>
public static class WebSeedServerExtensions
{
    /// <summary>
    /// Map the web seed endpoint at <c>/seed/{infoHash}/{**filePath}</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapWebSeedServer(this IEndpointRouteBuilder app, WebSeedServer webSeed)
    {
        app.MapGet("/seed/{infoHash}/{**filePath}", async (HttpContext ctx, string infoHash, string filePath) =>
        {
            await webSeed.HandleRequest(ctx, infoHash, filePath);
        });
        return app;
    }
}
