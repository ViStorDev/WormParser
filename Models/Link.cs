namespace WebParser.Models;

public class Link
{
    public string? Url { get; set; }
    public int WordCount => Data?.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
    public string? Data { get; set; }
}
