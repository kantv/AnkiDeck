using System.Globalization;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnkiDeck.Models;

namespace AnkiDeck.Services;

/// <summary>An error reported by the Zvukogram API or by the connection to it.</summary>
public sealed class ZvukogramException : Exception
{
    public ZvukogramException(string message) : base(message)
    {
    }

    public ZvukogramException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Thin client for the Zvukogram text-to-speech API (/text endpoint).</summary>
public sealed class ZvukogramClient
{
    public const int MaxTextLength = 1000;

    private readonly HttpClient _httpClient;

    public ZvukogramClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Sends the text to Zvukogram and downloads the resulting audio file.</summary>
    public async Task<GeneratedAudio> SynthesizeAsync(
        string text,
        ZvukogramSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ZvukogramException("Поле Front пустое — нечего озвучивать.");
        }

        if (text.Length > MaxTextLength)
        {
            throw new ZvukogramException(
                $"Текст для озвучки длиннее {MaxTextLength} символов. Сократите поле Front.");
        }

        var values = new Dictionary<string, string>
        {
            ["token"] = settings.Token.Trim(),
            ["email"] = settings.Email.Trim(),
            ["voice"] = settings.Voice.Trim(),
            ["text"] = text,
            ["format"] = NormalizeFormat(settings.Format),
            ["channels"] = "1",
        };

        if (!string.IsNullOrWhiteSpace(settings.SampleRate))
        {
            values["sample_rate"] = settings.SampleRate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.Bitrate))
        {
            values["bitrate"] = settings.Bitrate.Trim();
        }

        using var content = new FormUrlEncodedContent(values);
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.PostAsync("index.php?r=api/text", content, cancellationToken);
        }
        catch (TaskCanceledException exception)
        {
            throw new ZvukogramException("Сервис Звукограм не ответил вовремя. Попробуйте ещё раз.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ZvukogramException(DescribeConnectionError(exception), exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new ZvukogramException($"Сервис Звукограм вернул HTTP {(int)response.StatusCode}.");
            }

            var result = ParseResponse(body, response);
            if (result.Status != 1)
            {
                throw new ZvukogramException(string.IsNullOrWhiteSpace(result.Error)
                    ? "Сервис Звукограм вернул ошибку."
                    : result.Error);
            }

            if (string.IsNullOrWhiteSpace(result.File))
            {
                throw new ZvukogramException("Сервис Звукограм не вернул ссылку на аудиофайл.");
            }

            byte[] audio;
            try
            {
                audio = await _httpClient.GetByteArrayAsync(result.File, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                throw new ZvukogramException(
                    exception is HttpRequestException httpException
                        ? DescribeConnectionError(httpException)
                        : "Не удалось скачать аудиофайл у сервиса Звукограм.",
                    exception);
            }

            if (audio.Length == 0)
            {
                throw new ZvukogramException("Сервис Звукограм вернул пустой аудиофайл.");
            }

            var format = NormalizeFormat(string.IsNullOrWhiteSpace(result.Format) ? settings.Format : result.Format);
            return new GeneratedAudio(audio, format, result.Duration);
        }
    }

    private static string DescribeConnectionError(HttpRequestException exception) =>
        exception.InnerException is AuthenticationException
            ? "Не удалось установить защищённое соединение (TLS) с сервисом Звукограм. "
              + "Проверьте доступ в интернет, системное время и настройки прокси."
            : $"Не удалось связаться с сервисом Звукограм: {exception.Message}";

    /// <summary>
    /// The API normally returns numbers, but field types have changed over time; accept both
    /// numbers and numeric strings and report a readable snippet when the body is not JSON.
    /// </summary>
    private static ZvukogramResult ParseResponse(string body, HttpResponseMessage response)
    {
        var trimmed = (body ?? string.Empty).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.Length == 0)
        {
            throw new ZvukogramException("Сервис Звукограм вернул пустой ответ.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException exception)
        {
            throw new ZvukogramException(BuildUnexpectedResponseMessage(trimmed, response), exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ZvukogramException(BuildUnexpectedResponseMessage(trimmed, response));
            }

            return new ZvukogramResult(
                ReadInt32(root, "status"),
                ReadString(root, "file"),
                ReadString(root, "format"),
                ReadDouble(root, "duration"),
                ReadString(root, "error"));
        }
    }

    private static int ReadInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static double ReadDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return 0;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static string BuildUnexpectedResponseMessage(string body, HttpResponseMessage response)
    {
        var preview = Regex.Replace(body, @"\s+", " ").Trim();
        if (preview.Length > 200)
        {
            preview = preview[..200] + "…";
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "неизвестно";
        return $"Не удалось разобрать ответ сервиса Звукограм "
               + $"(HTTP {(int)response.StatusCode}, {mediaType}). Ответ: {preview}";
    }

    private static string NormalizeFormat(string? format)
    {
        var candidate = format?.Trim().ToLowerInvariant();
        return candidate is "mp3" or "wav" or "ogg" or "opus" or "flac" ? candidate : "mp3";
    }

    private sealed record ZvukogramResult(
        int Status,
        string? File,
        string? Format,
        double Duration,
        string? Error);
}
