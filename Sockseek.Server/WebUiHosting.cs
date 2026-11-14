using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace Sockseek.Server;

internal static class WebUiHosting
{
    public static bool Map(WebApplication app)
    {
        string indexPath = Path.Combine(app.Environment.WebRootPath
            ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
        if (!File.Exists(indexPath)) return false;
        string template = File.ReadAllText(indexPath);
        var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

        Task Shell(HttpContext context)
        {
            var settings = options.WebUi;
            string scheme = settings.Theme == "auto" ? "dark light" : settings.Theme;
            // Include preferences in the initial document: no bootstrap request
            // or wrong-theme flash, and no credentials in the public shell.
            string attributes = $"style=\"color-scheme:{HtmlEncoder.Default.Encode(scheme)}\" "
                + $"data-search-mode=\"{HtmlEncoder.Default.Encode(settings.DefaultSearchMode)}\" ";
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Content(template.Replace("<html ", "<html " + attributes, StringComparison.Ordinal),
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
