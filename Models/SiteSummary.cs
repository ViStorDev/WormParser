namespace WebParser.Models;

public class SiteSummary
{
    public string? Url { get; set; }
    //public string? Data { get; set; }
    //public List<SiteSummary> Summaries { get; set; } = new();
    public List<Link> Links { get; set; } = new();
}
