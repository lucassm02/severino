using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Severino.Proxy;

/// <summary>
/// The folder routes of one listener: each served by ASP.NET's own default files and static files
/// (index.html, types by extension, ranges, ETag, HEAD and 304), with no listing and
/// <c>Cache-Control: no-cache</c>, so a new build shows on the next F5. Nothing outside the folder
/// is served: the file provider refuses <c>..</c>, absolute paths and hidden files.
/// </summary>
internal sealed class FolderSites(IApplicationBuilder app) : IDisposable
{
    private readonly ConcurrentDictionary<(Guid Route, string Folder, string Prefix, bool Spa), Site> _sites = new();

    /// <summary>The pipeline for a route's folder; the folder must exist.</summary>
    /// <param name="prefix">The route's path, "" or "/docs": a request for /docs/a.css gets the folder's a.css.</param>
    public RequestDelegate For(Guid route, string folder, string prefix, bool spa, string domain) =>
        _sites.GetOrAdd((route, folder, prefix, spa), key => Build(key.Folder, key.Prefix, key.Spa, domain)).Serve;

    /// <summary>Routes changed: the next request builds what it needs again.</summary>
    public void Clear()
    {
        foreach (var site in _sites.Values)
            site.Files.Dispose();
        _sites.Clear();
    }

    public void Dispose() => Clear();

    private sealed record Site(PhysicalFileProvider Files, RequestDelegate Serve);

    private Site Build(string folder, string prefix, bool spa, string domain)
    {
        var files = new PhysicalFileProvider(folder);
        var requestPath = new PathString(prefix);
        var staticFiles = new StaticFileOptions
        {
            FileProvider = files,
            RequestPath = requestPath,
            ServeUnknownFileTypes = true,
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        };

        // Only the files: what the single-page fallback hands index.html to.
        var onlyFiles = app.New();
        onlyFiles.UseStaticFiles(staticFiles);
        onlyFiles.Run(context => NotFoundAsync(context, domain, folder));
        var serveFile = onlyFiles.Build();

        var site = app.New();
        // A folder asked without its slash gets one, so its relative links work: a 307, like every
        // Severino redirect, where ASP.NET's own would be a 301 the browser keeps.
        site.Use((context, next) =>
        {
            var request = context.Request;
            // "" is the route's own path asked bare (/docs), which is the folder too.
            var inside = request.Path.Value!.Length > prefix.Length ? request.Path.Value[prefix.Length..] : "";
            if (inside.EndsWith('/') || (inside.Length > 0 && !files.GetDirectoryContents(inside).Exists))
                return next(context);
            RequestLog.MarkFromProxy(context);
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = request.PathBase + request.Path + "/" + request.QueryString;
            return Task.CompletedTask;
        });
        site.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = requestPath, RedirectToAppendTrailingSlash = false });
        site.UseStaticFiles(staticFiles);
        site.Run(context =>
        {
            var request = context.Request;
            // A page the app routes itself (/painel/relatorios): what the browser navigates to, not a missing asset.
            var wantsPage = (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                && request.GetTypedHeaders().Accept.Any(a => a.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));
            if (spa && wantsPage && files.GetFileInfo("index.html").Exists)
            {
                request.Path = prefix + "/index.html";
                return serveFile(context);
            }
            return NotFoundAsync(context, domain, folder);
        });
        return new(files, site.Build());
    }

    private static Task NotFoundAsync(HttpContext context, string domain, string folder)
    {
        RequestLog.MarkFromProxy(context);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        return context.Response.WriteAsync(ErrorPages.FileNotFound(domain, context.Request.Path.Value ?? "/", folder));
    }
}
