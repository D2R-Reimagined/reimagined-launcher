namespace ReimaginedLauncher.HttpClients.Models;

public class GitHubAnnouncementBlock
{
    public string Text { get; set; } = string.Empty;
    public string Kind { get; set; } = "paragraph";
    public bool IsHeading1 => Kind == "h1";
    public bool IsHeading2 => Kind == "h2";
    public bool IsHeading3 => Kind == "h3";
    public bool IsHeading4 => Kind == "h4";
    public bool IsHeading5 => Kind == "h5";
    public bool IsHeading6 => Kind == "h6";
    public bool IsParagraph => Kind == "paragraph";
    public bool IsListItem => Kind == "li";
}
