using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AnkiDeck.Models;

namespace AnkiDeck.Services;

/// <summary>Detects HTML markup in Anki fields and converts such fields to plain text.</summary>
public static class HtmlMarkup
{
    /// <summary>Matches line-break tags, which do not count as HTML markup on their own.</summary>
    private static readonly Regex LineBreakTagPattern = new(
        @"<\s*/?\s*br\s*/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTagPattern = new(
        @"<\s*/?\s*[a-z][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches HTML character entities such as &amp;nbsp;, &amp;lt; or &amp;#160;.</summary>
    private static readonly Regex HtmlEntityPattern = new(
        @"&(#\d+|#x[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*);",
        RegexOptions.Compiled);

    /// <summary>
    /// Checks the Front, Back and Audio fields for HTML markup: tags (a plain <c>&lt;br&gt;</c>
    /// is ignored) or character entities such as <c>&amp;nbsp;</c>. Note types without those
    /// field names fall back to their first two fields.
    /// </summary>
    public static bool IsPresent(IReadOnlyList<AnkiField> fields)
    {
        var candidates = fields
            .Where(field => IsMarkupField(field.Name))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = fields.Take(2).ToList();
        }

        return candidates.Any(field => Contains(field.Value));
    }

    /// <summary>Fields that are checked for markup and cleaned when a note is processed.</summary>
    public static bool IsMarkupField(string name) =>
        string.Equals(name, "Front", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Back", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true when the value contains an HTML tag (except a plain line break) or an HTML entity.</summary>
    public static bool Contains(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var withoutLineBreaks = LineBreakTagPattern.Replace(value, string.Empty);
        return HtmlTagPattern.IsMatch(withoutLineBreaks) || HtmlEntityPattern.IsMatch(value);
    }

    /// <summary>
    /// Removes HTML tags and decodes character entities, keeping line breaks as literal
    /// <c>&lt;br&gt;</c> text so the result can be compared with the original field.
    /// </summary>
    public static string Strip(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var result = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            var current = html[index];
            if (current != '<')
            {
                result.Append(current);
                index++;
                continue;
            }

            // Comments are markup too and are dropped entirely.
            if (string.CompareOrdinal(html, index, "<!--", 0, 4) == 0)
            {
                var commentEnd = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = commentEnd < 0 ? html.Length : commentEnd + 3;
                continue;
            }

            // A '<' that does not start a tag (for example "5 < 10") stays as plain text.
            if (index + 1 >= html.Length || !IsTagStart(html[index + 1]))
            {
                result.Append(current);
                index++;
                continue;
            }

            var tagEnd = FindTagEnd(html, index);
            if (tagEnd < 0)
            {
                result.Append(current);
                index++;
                continue;
            }

            var tag = html[(index + 1)..tagEnd].Trim();
            index = tagEnd + 1;

            var tagName = ReadTagName(tag);
            if (tagName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                result.Append("<br>");
                continue;
            }

            if (tagName.Equals("script", StringComparison.OrdinalIgnoreCase)
                || tagName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                // Drop the whole element including its content.
                var closingTag = html.IndexOf($"</{tagName}", index, StringComparison.OrdinalIgnoreCase);
                if (closingTag >= 0)
                {
                    var closingEnd = FindTagEnd(html, closingTag);
                    index = closingEnd < 0 ? html.Length : closingEnd + 1;
                }
                else
                {
                    index = html.Length;
                }
            }
        }

        return WebUtility.HtmlDecode(result.ToString()).Replace('\u00a0', ' ');
    }

    private static bool IsTagStart(char value) =>
        char.IsLetter(value) || value is '/' or '!' or '?';

    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var index = start + 1; index < html.Length; index++)
        {
            var current = html[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static string ReadTagName(string tag)
    {
        var index = 0;
        if (tag.Length > 0 && tag[0] is '/' or '!' or '?')
        {
            index++;
        }

        var start = index;
        while (index < tag.Length && (char.IsLetterOrDigit(tag[index]) || tag[index] is '-' or ':'))
        {
            index++;
        }

        return index > start ? tag[start..index] : string.Empty;
    }
}
