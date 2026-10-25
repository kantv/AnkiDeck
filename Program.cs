using AnkiDeck.Components;
using AnkiDeck.Services;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Anki deck packages can be large (media included), so allow up to 512 MB per upload.
const long maxUploadBytes = 512L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxUploadBytes);

builder.Services.AddRazorComponents();
builder.Services.AddSingleton<ApkgReader>();
builder.Services.AddSingleton<DeckWorkspaceStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    // The uploaded deck lives in DeckWorkspaceStore; the session only keeps its identifier.
    options.IdleTimeout = TimeSpan.FromHours(4);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// The Zvukogram endpoint is configurable so the app can be pointed at a test double.
var zvukogramBaseUrl = builder.Configuration["Zvukogram:BaseUrl"] ?? "https://zvukogram.com/";
builder.Services.AddHttpClient<ZvukogramClient>(client =>
{
    client.BaseAddress = new Uri(zvukogramBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AnkiDeck/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

var app = builder.Build();

app.UseSession();
app.UseAntiforgery();

app.MapGet("/", () => Results.Redirect("/anki"));

app.MapGet("/anki/export", ExportDeck);
app.MapGet("/anki/audio/{noteId:long}/{index:int}", GetGeneratedAudio);
app.MapGet("/anki/media/{fileName}", GetDeckMedia);
app.MapGet("/anki/audio-job/progress", GetAudioJobProgress);
app.MapPost("/anki/audio-job/pause", PauseAudioJob);
app.MapPost("/anki/audio-job/resume", ResumeAudioJob);
app.MapPost("/anki/audio-job/cancel", CancelAudioJob);
app.MapPost("/anki/audio-job/dismiss", DismissAudioJob);

app.MapStaticAssets();
app.MapRazorComponents<App>();

app.Run();

static async Task<IResult> ExportDeck(HttpContext context, DeckWorkspaceStore store)
{
    var workspaceId = context.Session.GetString(DeckWorkspaceStore.SessionKey);
    if (!store.TryGet(workspaceId, out var workspace))
    {
        return Results.Redirect("/anki");
    }

    var package = await workspace.ExportAsync(context.RequestAborted);
    return Results.File(package, "application/octet-stream", workspace.FileName);
}

static IResult GetGeneratedAudio(long noteId, int index, HttpContext context, DeckWorkspaceStore store)
{
    var workspaceId = context.Session.GetString(DeckWorkspaceStore.SessionKey);
    if (!store.TryGet(workspaceId, out var workspace))
    {
        return Results.NotFound();
    }

    var files = workspace.GetGeneratedAudioFiles(noteId);
    if (index < 0 || index >= files.Count)
    {
        return Results.NotFound();
    }

    var audio = files[index].Audio;
    return Results.File(audio.Content, audio.ContentType);
}

static IResult GetAudioJobProgress(HttpContext context, DeckWorkspaceStore store)
{
    var job = FindWorkspace(context, store)?.AudioJob;
    return Results.Json(job?.Snapshot() ?? IdleAudioSnapshot());
}

static IResult PauseAudioJob(HttpContext context, DeckWorkspaceStore store)
{
    var job = FindWorkspace(context, store)?.AudioJob;
    job?.Pause();
    return Results.Json(job?.Snapshot() ?? IdleAudioSnapshot());
}

static IResult ResumeAudioJob(HttpContext context, DeckWorkspaceStore store)
{
    var job = FindWorkspace(context, store)?.AudioJob;
    job?.Resume();
    return Results.Json(job?.Snapshot() ?? IdleAudioSnapshot());
}

static IResult CancelAudioJob(HttpContext context, DeckWorkspaceStore store)
{
    var job = FindWorkspace(context, store)?.AudioJob;
    job?.Cancel();
    return Results.Json(job?.Snapshot() ?? IdleAudioSnapshot());
}

static IResult DismissAudioJob(HttpContext context, DeckWorkspaceStore store)
{
    var workspace = FindWorkspace(context, store);
    if (workspace?.AudioJob is { } job && !job.Snapshot().Active)
    {
        workspace.AudioJob = null;
    }

    return Results.Ok();
}

static DeckWorkspace? FindWorkspace(HttpContext context, DeckWorkspaceStore store)
{
    var workspaceId = context.Session.GetString(DeckWorkspaceStore.SessionKey);
    return store.TryGet(workspaceId, out var workspace) ? workspace : null;
}

static BulkAudioSnapshot IdleAudioSnapshot() =>
    new(false, "idle", 0, 0, 0, 0, string.Empty, null);

static async Task<IResult> GetDeckMedia(string fileName, HttpContext context, DeckWorkspaceStore store)
{
    var workspaceId = context.Session.GetString(DeckWorkspaceStore.SessionKey);
    if (!store.TryGet(workspaceId, out var workspace))
    {
        return Results.NotFound();
    }

    var media = await workspace.TryReadMediaAsync(fileName, context.RequestAborted);
    return media is null
        ? Results.NotFound()
        : Results.File(media.Value.Content, media.Value.ContentType);
}
