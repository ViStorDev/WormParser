namespace WebParser.Helpers;

public static class HtmlLinkExtractor
{
    // Читає filterConfig.txt, повертає список рядків
    private static string[] LoadExcludedSubstrings()
    {
        try
        {
            string configPath = "filterConfig.txt";
            if (!File.Exists(configPath))
            {
                // fallback для ASP.NET хостингу (AppContext.BaseDirectory)
                var baseDirPath = Path.Combine(AppContext.BaseDirectory, "filterConfig.txt");
                if (File.Exists(baseDirPath))
                    configPath = baseDirPath;
                else
                {
                    Console.WriteLine("⚠️ filterConfig.txt не знайдено ні в поточній директорії, ні в AppContext.BaseDirectory.");
                    return Array.Empty<string>();
                }
            }

            var lines = File.ReadAllLines(configPath);
            var cleaned = lines
                .Select(l => (l ?? string.Empty).Trim().TrimStart('\uFEFF', '\u200B'))
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")) // дозволяємо коментарі
                .ToArray();

            return cleaned;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Помилка читання filterConfig.txt: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static bool IsHttpOrHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    public static List<string> ExtractAbsoluteLinksOnlyWithRegex(string htmlContent, string baseUrl)
    {
        HashSet<string> uniqueLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] excludedSubstrings = LoadExcludedSubstrings();

        var htmlDoc = new HtmlAgilityPack.HtmlDocument();
        htmlDoc.LoadHtml(htmlContent);

        var allHrefs = htmlDoc.DocumentNode
            .SelectNodes("//a[@href] | //*[@src]")
            ?.Select(node => node.GetAttributeValue("href", null) ?? node.GetAttributeValue("src", null))
            .Where(href => !string.IsNullOrEmpty(href))
            .ToList() ?? new List<string>();

        foreach (string foundUrl in allHrefs)
        {
            if (Uri.TryCreate(foundUrl, UriKind.Absolute, out Uri urlObj) && IsHttpOrHttps(urlObj))
            {
                string cleaned = string.IsNullOrEmpty(urlObj.Fragment)
                    ? urlObj.AbsoluteUri
                    : urlObj.GetLeftPart(UriPartial.Path);

                if (!excludedSubstrings.Any(sub => cleaned.Contains(sub, StringComparison.OrdinalIgnoreCase)))
                {
                    uniqueLinks.Add(cleaned.TrimEnd('/'));
                }
            }
        }

        var relativeLinks = ExtractSpecificRelativePaths(htmlContent, baseUrl);
        foreach (var rel in relativeLinks)
        {
            if (!excludedSubstrings.Any(sub => rel.Contains(sub, StringComparison.OrdinalIgnoreCase)))
                uniqueLinks.Add(rel.TrimEnd('/'));
        }

        return uniqueLinks.ToList();
    }

    public static List<string> ExtractSpecificRelativePaths(string htmlContent, string baseUrl)
    {
        HashSet<string> extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUriObject))
            return new List<string>();

        extractedPaths.Add(baseUriObject.AbsoluteUri.TrimEnd('/'));

        var htmlDoc = new HtmlAgilityPack.HtmlDocument();
        htmlDoc.LoadHtml(htmlContent);

        var relativeHrefs = htmlDoc.DocumentNode
            .SelectNodes("//a[@href] | //*[@src]")
            ?.Select(node => node.GetAttributeValue("href", null) ?? node.GetAttributeValue("src", null))
            .Where(href => !string.IsNullOrEmpty(href) && href.StartsWith("/"))
            .ToList() ?? new List<string>();

        foreach (var foundRelativePath in relativeHrefs)
        {
            try
            {
                Uri combinedUri = new Uri(baseUriObject, foundRelativePath);
                if (IsHttpOrHttps(combinedUri))
                    extractedPaths.Add(combinedUri.AbsoluteUri.TrimEnd('/'));
            }
            catch { /* ігноруємо невалідні */ }
        }

        return extractedPaths.ToList();
    }
}