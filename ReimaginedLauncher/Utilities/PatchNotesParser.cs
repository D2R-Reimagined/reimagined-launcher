using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ReimaginedLauncher.Utilities;

public sealed record PatchRelease(string Version, string Date, string Title, string Highlights, string Url)
{
    public override string ToString() => Version;
}
public sealed record PatchInline(string Text, string? Url = null);
public sealed record PatchBlock(string Kind, string Text, int Depth, List<PatchInline> Inlines, List<List<List<PatchInline>>>? Rows = null);
public sealed record PatchArticle(List<PatchBlock> Blocks);

internal static class PatchNotesParser
{
    internal const string IndexUrl = "https://wiki.d2r-reimagined.com/en/Patch_Notes";
    private static readonly Uri Wiki = new(IndexUrl);

    internal static bool IsWikiPage(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == Wiki.Host && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        Regex.IsMatch(uri.AbsolutePath, @"^/(en/)?Patch_Notes(/[^/]+)?/?$");

    internal static string? SafeLink(string href)
    {
        if (!Uri.TryCreate(Wiki, href, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.AbsoluteUri;
    }

    private static string Text(HtmlNode node) => Regex.Replace(HtmlEntity.DeEntitize(node.InnerText), @"\s+", " ").Trim();
    private static HtmlNode Content(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var content = document.DocumentNode.SelectSingleNode("//template[@slot='contents']") ??
            throw new InvalidDataException("The wiki article content could not be found.");
        foreach (var node in content.SelectNodes(".//script|.//style|.//iframe|.//a[contains(@class,'toc-anchor')]")?.ToList() ?? []) node.Remove();
        foreach (var node in content.SelectNodes(".//br")?.ToList() ?? []) node.ParentNode?.ReplaceChild(document.CreateTextNode("\n"), node);
        return content;
    }

    internal static List<PatchRelease> ParseIndex(string html)
    {
        var releases = new List<PatchRelease>();
        foreach (var row in Content(html).SelectNodes(".//table//tr") ?? new HtmlNodeCollection(null))
        {
            var cells = row.SelectNodes("./td");
            if (cells == null || cells.Count < 4) continue;
            var anchor = cells[0].SelectSingleNode(".//a[@href]");
            var url = anchor == null ? null : SafeLink(anchor.GetAttributeValue("href", ""));
            var version = Text(cells[0]).TrimStart('\uFEFF', '\u200B');
            if (url == null || !Regex.IsMatch(version, @"^v?\d+\.\d+")) continue;
            // Older releases on this index link directly to the project's GitHub release notes.
            if (!IsWikiPage(url) && !(new Uri(url).Host == "github.com" && new Uri(url).AbsolutePath.StartsWith("/D2R-Reimagined/", StringComparison.OrdinalIgnoreCase))) continue;
            if (new Uri(url).AbsolutePath.StartsWith("/Patch_Notes")) url = "https://wiki.d2r-reimagined.com/en" + new Uri(url).AbsolutePath;
            releases.Add(new(version, Text(cells[1]), Text(cells[2]), Text(cells[3]), url));
        }
        if (releases.Count == 0) throw new InvalidDataException("The wiki version list could not be read.");
        return releases.DistinctBy(r => r.Version).OrderByDescending(r => VersionKey(r.Version)).ToList();
    }

    private static Version VersionKey(string text) => Version.TryParse(Regex.Match(text, @"\d+\.\d+(?:\.\d+)?").Value, out var version) ? version : new Version(0, 0);

    internal static bool MatchesVersion(string label, string? installed)
    {
        if (string.IsNullOrWhiteSpace(installed)) return false;
        var match = Regex.Match(installed, @"\d+\.\d+(?:\.\d+)?");
        if (!match.Success) return false;
        if (VersionKey(label) == VersionKey(match.Value)) return true;
        var range = Regex.Match(label, @"^(\d+)\.(\d+)\.(\d+)-(\d+)$");
        var v = VersionKey(match.Value);
        return range.Success && v.Major == int.Parse(range.Groups[1].Value) && v.Minor == int.Parse(range.Groups[2].Value) &&
            v.Build >= int.Parse(range.Groups[3].Value) && v.Build <= int.Parse(range.Groups[4].Value);
    }

    internal static PatchArticle ParseArticle(string html)
    {
        var blocks = new List<PatchBlock>();
        void Visit(HtmlNode node, int depth)
        {
            if (node.Name == "table")
            {
                var rows = node.SelectNodes(".//tr")?.Select(r => r.SelectNodes("./th|./td")?.Select(cell => ReadInlines(cell)).ToList() ?? []).Where(r => r.Count > 0).ToList();
                if (rows?.Count > 0) blocks.Add(new("table", "", depth, [], rows));
                return;
            }
            if (node.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "p" or "li" or "pre" or "blockquote")
            {
                var copy = node.CloneNode(true);
                foreach (var child in copy.SelectNodes(".//ul|.//ol")?.ToList() ?? []) child.Remove();
                var text = node.Name == "pre" ? HtmlEntity.DeEntitize(copy.InnerText).Trim() : Text(copy);
                if (text.Length > 0) blocks.Add(new(node.Name, text, depth, ReadInlines(copy, node.Name == "pre")));
                foreach (var list in node.SelectNodes("./ul|./ol") ?? new HtmlNodeCollection(null)) Visit(list, depth + 1);
                return;
            }
            if (node.Name == "img")
            {
                var url = SafeLink(node.GetAttributeValue("src", ""));
                if (url != null) blocks.Add(new("p", "", depth, [new(node.GetAttributeValue("alt", "View image on wiki"), url)]));
                return;
            }
            foreach (var child in node.ChildNodes) Visit(child, depth);
        }
        Visit(Content(html), 0);
        if (blocks.Count == 0) throw new InvalidDataException("The wiki release article was empty.");
        return new(blocks);
    }

    private static List<PatchInline> ReadInlines(HtmlNode node, bool preserveWhitespace = false)
    {
        var parts = new List<PatchInline>();
        void Read(HtmlNode current, string? url)
        {
            if (current.Name == "a")
            {
                var href = current.GetAttributeValue("href", "");
                url = href.Length > 0 && !href.StartsWith('#') ? SafeLink(href) : null;
            }
            if (current.NodeType == HtmlNodeType.Text)
            {
                var text = HtmlEntity.DeEntitize(current.InnerText);
                if (!preserveWhitespace) text = Regex.Replace(text, @"\s+", " ");
                if (parts.Count > 0 && parts[^1].Url == url)
                    parts[^1] = parts[^1] with { Text = parts[^1].Text + text };
                else parts.Add(new(text, url));
                return;
            }
            foreach (var child in current.ChildNodes) Read(child, url);
        }
        Read(node, null);
        if (parts.Count > 0)
        {
            parts[0] = parts[0] with { Text = parts[0].Text.TrimStart() };
            parts[^1] = parts[^1] with { Text = parts[^1].Text.TrimEnd() };
        }
        return parts.Where(p => p.Text.Length > 0).ToList();
    }
}
