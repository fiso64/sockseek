using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;

namespace Sockseek.Server;

internal static class WebUiHosting
{
    public static bool Map(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        string manual = Path.Combine(app.Environment.ContentRootPath, "manual");
        bool serveDocs = options.ServeDocumentation && File.Exists(Path.Combine(manual, "index.html"));
        app.Map("/docs", docs =>
        {
            docs.Use((context, next) =>
            {
                context.Response.Headers["X-Robots-Tag"] = "noindex";
                return next(context);
            });
            if (serveDocs)
            {
                var files = new PhysicalFileProvider(manual);
                app.Lifetime.ApplicationStopped.Register(files.Dispose);
                docs.UseFileServer(new FileServerOptions { FileProvider = files });
            }
            docs.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        });

        string indexPath = Path.Combine(app.Environment.WebRootPath
            ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
        if (!File.Exists(indexPath)) return false;
        string template = File.ReadAllText(indexPath);
        string docsAttribute = serveDocs ? "data-docs " : "";

        Task Shell(HttpContext context)
        {
            var settings = options.WebUi;
            string scheme = settings.Theme switch
            {
                "light" or "2009-light" => "light",
                "auto" => "dark light",
                _ => "dark",
            };
            // Include preferences in the initial document: no bootstrap request
            // or wrong-theme flash, and no credentials in the public shell.
            string attributes = $"{docsAttribute}style=\"color-scheme:{HtmlEncoder.Default.Encode(scheme)}\" "
                + $"data-theme=\"{HtmlEncoder.Default.Encode(settings.Theme)}\" "
                + $"data-search-mode=\"{HtmlEncoder.Default.Encode(settings.DefaultSearchMode)}\" ";
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
            if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && context.Request.Path is { Value: "/" or "/index.html" })
                await Shell(context);
            else await next(context);
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                context.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public,max-age=31536000,immutable" : "no-cache",
        });
        app.MapFallback(Shell).AllowAnonymous().ExcludeFromDescription();
        return true;
    }
}
