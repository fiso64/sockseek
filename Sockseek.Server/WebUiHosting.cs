using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;

namespace Sockseek.Server;

internal static class WebUiHosting
{
    public static bool Map(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        IFileProvider DirectoryFiles(string path)
        {
            if (!Directory.Exists(path)) return new NullFileProvider();
            var files = new PhysicalFileProvider(path);
            app.Lifetime.ApplicationStopped.Register(files.Dispose);
            return files;
        }

        var manual = options.WebAssetsAssembly is { } manualAssembly ? new ManifestEmbeddedFileProvider(manualAssembly, "manual")
            : DirectoryFiles(Path.Combine(app.Environment.ContentRootPath, "manual"));
        // Reloadable, like the other options the shell reads per request.
        bool hasManual = manual.GetFileInfo("index.html").Exists;
        bool ServeDocs() => hasManual && options.ServeDocumentation;
        app.Map("/docs", docs =>
        {
            docs.Use((context, next) =>
            {
                context.Response.Headers["X-Robots-Tag"] = "noindex";
                return next(context);
            });
            docs.UseWhen(_ => ServeDocs(), served => served.UseFileServer(new FileServerOptions { FileProvider = manual }));
            docs.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        });

        var ui = options.WebUiDirectory is { } custom ? DirectoryFiles(custom)
            : options.WebAssetsAssembly is { } uiAssembly ? new ManifestEmbeddedFileProvider(uiAssembly, "wwwroot") : app.Environment.WebRootFileProvider;
        var index = ui.GetFileInfo("index.html");
        if (!index.Exists)
        {
            if (options.WebUiDirectory != null)
                app.Logger.LogError("WebUI unavailable: web-ui-dir {Directory} contains no index.html", options.WebUiDirectory);
            return false;
        }
        using var reader = new StreamReader(index.CreateReadStream());
        string template = reader.ReadToEnd();

        Task Shell(HttpContext context)
        {
            var settings = options.WebUi;
            string scheme = settings.ColorScheme == "auto" ? "dark light" : settings.ColorScheme;
            // Include preferences in the initial document: no bootstrap request
            // or wrong-theme flash, and no credentials in the public shell.
            string attributes = $"{(ServeDocs() ? "data-docs " : "")}style=\"color-scheme:{HtmlEncoder.Default.Encode(scheme)}\" "
                + $"data-theme=\"{HtmlEncoder.Default.Encode(settings.Theme)}\" "
                + $"data-search-mode=\"{HtmlEncoder.Default.Encode(settings.DefaultSearchMode)}\" "
                + $"data-search-sort=\"{HtmlEncoder.Default.Encode(settings.DefaultSort)}\" ";
            context.Response.Headers.CacheControl = "no-cache";
            // A stripping reverse proxy can serve the same shell at any prefix.
            // Resolve relative assets from the app root, including on deep bookmarks.
            int depth = context.Request.Path.Value?.Count(c => c == '/') ?? 0;
            string relativeRoot = depth < 2 ? "./" : string.Concat(Enumerable.Repeat("../", depth - 1));
            var html = template.Replace("<base href=\"/\"", $"<base href=\"{relativeRoot}\"", StringComparison.Ordinal)
                .Replace("<html ", "<html " + attributes, StringComparison.Ordinal);
            return Results.Content(html,
                "text/html; charset=utf-8").ExecuteAsync(context);
        }

        app.Use(async (context, next) =>
        {
            // Share addresses end in peer file names such as 01.flac, which the
            // fallback would take for static files; slsk: addresses are pasted links.
            PathString path = context.Request.Path;
            if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && (path is { Value: "/" or "/index.html" } || path.StartsWithSegments("/shares")
                    || path.Value?.StartsWith("/slsk:", StringComparison.OrdinalIgnoreCase) == true
                    || path.Value?.StartsWith("/users/slsk:", StringComparison.OrdinalIgnoreCase) == true))
                await Shell(context);
            else await next(context);
        });
        // An alternate UI may contain an api/ folder. The daemon owns that route.
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), branch => branch.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = ui,
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                options.WebUiDirectory == null && context.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public,max-age=31536000,immutable" : "no-cache",
        }));
        app.MapFallback(Shell).AllowAnonymous().ExcludeFromDescription();
        return true;
    }
}
