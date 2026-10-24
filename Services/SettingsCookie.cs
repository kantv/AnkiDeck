using System.Text.Json;
using AnkiDeck.Models;

namespace AnkiDeck.Services;

/// <summary>Reads and writes the Zvukogram settings cookie.</summary>
public static class SettingsCookie
{
    public const string CookieName = "ankideck.zvukogram";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static ZvukogramSettings Read(HttpContext context)
    {
        var raw = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw))
        {
            return new ZvukogramSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ZvukogramSettings>(raw, SerializerOptions) ?? new ZvukogramSettings();
        }
        catch (JsonException)
        {
            return new ZvukogramSettings();
        }
    }

    public static void Write(HttpContext context, ZvukogramSettings settings)
    {
        var value = JsonSerializer.Serialize(settings, SerializerOptions);

        context.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        });
    }
}
