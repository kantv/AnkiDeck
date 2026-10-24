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

var app = builder.Build();

app.UseSession();
app.UseAntiforgery();

app.MapGet("/", () => Results.Redirect("/anki"));

app.MapGet("/anki/export", ExportDeck);

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
