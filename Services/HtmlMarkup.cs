using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AnkiDeck.Models;

namespace AnkiDeck.Services;

/// <summary>Detects HTML markup in Anki fields and converts such fields to plain text.</summary>
public static class HtmlMarkup
{
    /// <summary>Matches line-break tags, which are handled separately from other markup.</summary>
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

    /// <summary>Matches Anki media references such as [sound:file.mp3] or [img:file.png].</summary>
    private static readonly Regex MediaReferencePattern = new(
        @"\[(?:sound|img):[^\]]+\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Checks the Front, Back and Audio fields for HTML markup. A line break is only ignored
    /// when it actually separates visible text; a trailing, leading or lone <c>&lt;br&gt;</c>
    /// counts as markup because it has no meaning. Note types without those field names fall
    /// back to their first two fields.
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

    /// <summary>
    /// Returns true when the value contains a tag (including a meaningless line break) or an
    /// HTML entity. A <c>&lt;br&gt;</c> that separates visible text is not considered markup.
    /// </summary>
    public static bool Contains(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (HtmlEntityPattern.IsMatch(value))
        {
            return true;
        }

        // Any tag other than a line break is markup.
        var withoutLineBreaks = LineBreakTagPattern.Replace(value, string.Empty);
        if (HtmlTagPattern.IsMatch(withoutLineBreaks))
        {
            return true;
        }

        var tokens = Tokenize(value);
        var meaningful = FindMeaningfulLineBreaks(tokens);

        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == MarkupTokenKind.LineBreak && !meaningful[index])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a field to text suitable for speech synthesis: markup is removed, meaningful
    /// line breaks become spaces and Anki media references are dropped.
    /// </summary>
    public static string ToSpeechText(string html)
    {
        var text = Strip(html).Replace("<br>", " ", StringComparison.OrdinalIgnoreCase);
        text = MediaReferencePattern.Replace(text, " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Removes HTML tags and decodes character entities. Meaningful line breaks are kept as
    /// literal <c>&lt;br&gt;</c> text; line breaks that separate nothing are dropped.
    /// </summary>
    public static string Strip(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var tokens = Tokenize(html);
        var meaningful = FindMeaningfulLineBreaks(tokens);
        var result = new StringBuilder(html.Length);

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == MarkupTokenKind.Text)
            {
                result.Append(token.Value);
            }
            else if (meaningful[index])
            {
                result.Append("<br>");
            }
        }

        return WebUtility.HtmlDecode(result.ToString()).Replace('\u00a0', ' ');
    }

    /// <summary>
    /// Splits the field into text chunks and line-break tokens. All other tags are dropped;
    /// the text inside them is kept, while script and style bodies are removed entirely.
    /// </summary>
    private static List<MarkupToken> Tokenize(string html)
    {
        var tokens = new List<MarkupToken>();
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length > 0)
            {
                tokens.Add(new MarkupToken(MarkupTokenKind.Text, text.ToString()));
                text.Clear();
            }
        }

        var index = 0;
        while (index < html.Length)
        {
            var current = html[index];
            if (current != '<')
            {
                text.Append(current);
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
                text.Append(current);
                index++;
                continue;
            }

            var tagEnd = FindTagEnd(html, index);
            if (tagEnd < 0)
            {
                text.Append(current);
                index++;
                continue;
            }

            var tag = html[(index + 1)..tagEnd].Trim();
            index = tagEnd + 1;

            var tagName = ReadTagName(tag);
            if (tagName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                FlushText();
                tokens.Add(new MarkupToken(MarkupTokenKind.LineBreak, string.Empty));
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

        FlushText();
        return tokens;
    }

    /// <summary>
    /// A line break is meaningful only when there is visible text both before and after it.
    /// </summary>
    private static bool[] FindMeaningfulLineBreaks(IReadOnlyList<MarkupToken> tokens)
    {
        var visible = new bool[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            visible[index] = tokens[index].Kind == MarkupTokenKind.Text
                             && HasVisibleText(tokens[index].Value);
        }

        var hasVisibleBefore = new bool[tokens.Count];
        var seen = false;
        for (var index = 0; index < tokens.Count; index++)
        {
            hasVisibleBefore[index] = seen;
            seen |= visible[index];
        }

        var hasVisibleAfter = new bool[tokens.Count];
        seen = false;
        for (var index = tokens.Count - 1; index >= 0; index--)
        {
            hasVisibleAfter[index] = seen;
            seen |= visible[index];
        }

        var meaningful = new bool[tokens.Count];
        for (var index = 0; index < tokens.Count; index++)
        {
            meaningful[index] = tokens[index].Kind == MarkupTokenKind.LineBreak
                                && hasVisibleBefore[index]
                                && hasVisibleAfter[index];
        }

        return meaningful;
    }

    private static bool HasVisibleText(string text) =>
        WebUtility.HtmlDecode(text).Any(character => !char.IsWhiteSpace(character));

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

    private enum MarkupTokenKind
    {
        Text,
        LineBreak,
    }

    private readonly record struct MarkupToken(MarkupTokenKind Kind, string Value);
}
