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

var app = builder.Build();

app.UseAntiforgery();

app.MapGet("/", () => Results.Redirect("/anki"));

app.MapStaticAssets();
app.MapRazorComponents<App>();

app.Run();
