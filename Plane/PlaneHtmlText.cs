using System.Net;
using System.Text.RegularExpressions;

internal static class PlaneHtmlText
{
    public static string ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        value = Regex.Replace(
            value,
            @"</?br\s*/?>",
            "\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        value = Regex.Replace(
            value,
            @"</?(?:p|div|blockquote|h[1-6])\b[^>]*>",
            "\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        value = Regex.Replace(
            value,
            @"<li\b[^>]*>",
            "• ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        value = Regex.Replace(
            value,
            @"</li\s*>",
            "\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        value = Regex.Replace(value, @"<[^>]+>", "", RegexOptions.Singleline);
        value = WebUtility.HtmlDecode(value)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        value = Regex.Replace(value, @"[ \t]+\n", "\n");
        value = Regex.Replace(value, @"\n{3,}", "\n\n");

        return value.Trim();
    }
}
