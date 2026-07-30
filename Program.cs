using System.Diagnostics;
using System.Reflection;
using Weaver.Services;
using Weaver.Hubs;

WeaverLogo.DisplayLogo();
var builder = WebApplication.CreateBuilder(args);
var basePath = builder.Environment.ContentRootPath;
var weaverDataDir = Path.Combine(basePath, "data");
var configPath = Path.Combine(basePath, "weaverconfig.json");

// Database lives in Weaver's data/ folder alongside other persisted data
var dbPath = Path.Combine(weaverDataDir, "weaver.db");

// DatabaseService (central SQLite store — replaces all JSON file storage)
var dbService = new DatabaseService(dbPath, weaverDataDir, configPath);
builder.Services.AddSingleton(dbService);

builder.Services.AddSingleton<TerminalService>();
builder.Services.AddSingleton<ConfigFileService>(sp => new ConfigFileService(sp.GetRequiredService<DatabaseService>()));
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton(new FileHintsManager(dbService));
builder.Services.AddSingleton(new CalendarService(dbService));
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<PushNotificationService>(sp => new PushNotificationService(dbService));
builder.Services.AddHttpClient("llama", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddControllers();
builder.Services.AddSingleton<BoardDataService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<BoardDataService>>();
    return new BoardDataService(dbService, logger);
});
builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
}));
var app = builder.Build();
app.UseRouting();
app.UseCors();

// Serve static files from wwwroot/ on disk with caching (fast path)
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", "public, max-age=3600");
    }
});

var assembly = Assembly.GetExecutingAssembly();
var resources = assembly.GetManifestResourceNames();
// Serve index.html at root (fallback to embedded resource)
app.MapGet("/", async context =>
{
    var indexRes = resources.First(r => r.EndsWith("wwwroot.index.html"));
    using var stream = assembly.GetManifestResourceStream(indexRes)!;
    using var reader = new StreamReader(stream);
    var html = await reader.ReadToEndAsync();
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});
// Serve static files from embedded resources (fallback if not on disk)
app.MapGet("/{**path}", async context =>
{
    string path = context.Request.Path.Value!.TrimStart('/').Replace("/", ".");
    string? resourceName = resources.FirstOrDefault(r => r.EndsWith(path));
    if (resourceName == null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    using var stream = assembly.GetManifestResourceStream(resourceName)!;
    context.Response.ContentType = Path.GetExtension(path) switch
    {
        ".js" => "application/javascript",
        ".css" => "text/css",
        ".html" => "text/html",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
    await stream.CopyToAsync(context.Response.Body);
});
app.MapControllers();
app.MapHub<CoEditHub>("/hubs/coEdit");
var runTask = app.RunAsync();

// Now Kestrel has started and URLs are populated
var url = app.Urls.First();
if (!args.Contains("--no-open-browser"))
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

await runTask;