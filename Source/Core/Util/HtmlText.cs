using System.Net;
using System.Text.RegularExpressions;

namespace UnVault.Core.Util;

/// <summary>Fab's descriptions are HTML. They're shown as plain text: nothing in them is run, loaded or linked.</summary>
public static partial class HtmlText
{
    /// <summary>The text of a piece of HTML, with paragraphs apart and list items on lines of their own.</summary>
    public static string ToPlain(string html)
    {
        string text = ParagraphEndRegex().Replace(html, "\n\n");
        text = LineEndRegex().Replace(text, "\n");
        text = ListItemRegex().Replace(text, "• ");
        text = WebUtility.HtmlDecode(TagRegex().Replace(text, "")).Replace(' ', ' ');
        return BlankLinesRegex().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"</p>|</h[1-6]>|</ul>|</ol>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphEndRegex();

    [GeneratedRegex(@"<br\s*/?>|</li>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex LineEndRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    /// <summary>Empty paragraphs leave runs of blank lines; one is enough.</summary>
    [GeneratedRegex(@"[ \t]*(\r?\n[ \t]*){2,}")]
    private static partial Regex BlankLinesRegex();
}
