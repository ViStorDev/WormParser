namespace WebParser.Helpers;

public static class HtmlLinkExtractor
{
    /// <summary>
    /// Витягує унікальні абсолютні посилання (URL) з HTML-контенту,
    /// включаючи специфічні відносні шляхи, які перетворюються на абсолютні URL.
    /// Виключає посилання, що містять певні підрядки.
    /// </summary>
    public static List<string> ExtractAbsoluteLinksOnlyWithRegex(string htmlContent, string baseUrl)
    {
        Log.Information("Початок вилучення абсолютних посилань з HTML-контенту. Базовий URL: {BaseUrl}", baseUrl);

        List<string> directAbsoluteLinks = new List<string>();

        string[] excludedSubstrings = File.ReadAllLines("filterConfig.txt")
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToArray();

        // 1. Витягуємо абсолютні посилання через HtmlAgilityPack
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(htmlContent);

        var allHrefs = htmlDoc.DocumentNode
            .SelectNodes("//a[@href] | //*[@src]") // і <a href>, і будь-які теги з src
            ?.Select(node =>
            {
                string href = node.GetAttributeValue("href", null) ?? node.GetAttributeValue("src", null);
                return href;
            })
            .Where(href => !string.IsNullOrEmpty(href))
            .ToList() ?? new List<string>();

        foreach (string foundUrl in allHrefs)
        {
            string urlToAdd = foundUrl;

            try
            {
                if (Uri.TryCreate(foundUrl, UriKind.Absolute, out Uri urlObj))
                {
                    // видалення #fragment
                    if (!string.IsNullOrEmpty(urlObj.Fragment))
                    {
                        urlToAdd = urlObj.GetLeftPart(UriPartial.Path);
                        Log.Debug("Видалено фрагмент з URL: {OriginalUrl} -> {CleanedUrl}", foundUrl, urlToAdd);
                    }
                }
                else
                {
                    continue; // ігноруємо невалідні абсолютні
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Помилка парсингу URI '{FoundUrl}' в ExtractAbsoluteLinksOnlyWithRegex. Помилка: {Message}", foundUrl, ex.Message);
                continue;
            }

            // Фільтрація за виключеннями
            if (excludedSubstrings.Any(sub => urlToAdd.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Log.Debug("Посилання '{Url}' відфільтровано через підрядок.", urlToAdd);
                continue;
            }

            directAbsoluteLinks.Add(urlToAdd);
            Log.Debug("Додано пряме абсолютне посилання: {Url}", urlToAdd);
        }
        Log.Information("Знайдено {Count} прямих абсолютних посилань.", directAbsoluteLinks.Count);

        // 2. Витягуємо відносні посилання та робимо їх абсолютними
        List<string> relativeLinksAsAbsolute = ExtractSpecificRelativePaths(htmlContent, baseUrl);
        Log.Information("Знайдено {Count} відносних посилань (перетворених на абсолютні).", relativeLinksAsAbsolute.Count);

        // 3. Фільтруємо відносні посилання
        HashSet<string> filteredRelativeLinks = new HashSet<string>();
        foreach (string link in relativeLinksAsAbsolute)
        {
            if (excludedSubstrings.Any(sub => link.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Log.Debug("Відносне посилання '{Link}' відфільтровано.", link);
                continue;
            }
            filteredRelativeLinks.Add(link);
        }
        Log.Information("Після фільтрації залишилося {Count} відфільтрованих відносних посилань.", filteredRelativeLinks.Count);

        // 4. Об’єднуємо
        List<string> finalLinks = directAbsoluteLinks.Union(filteredRelativeLinks).ToList();
        Log.Information("Завершено вилучення посилань. Загальна кількість унікальних посилань: {Count}", finalLinks.Count);

        return finalLinks;
    }

    /// <summary>
    /// Витягує специфічні відносні шляхи з HTML-контенту та перетворює їх на абсолютні URL,
    /// об'єднуючи з наданим базовим URL за допомогою класу Uri.
    /// </summary>
    public static List<string> ExtractSpecificRelativePaths(string htmlContent, string baseUrl)
    {
        Log.Information("Початок вилучення специфічних відносних шляхів. Базовий URL: {BaseUrl}", baseUrl);
        HashSet<string> extractedPaths = new HashSet<string>();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUriObject))
        {
            Log.Error("Недійсний базовий URL для ExtractSpecificRelativePaths: {BaseUrl}", baseUrl);
            return new List<string>();
        }

        extractedPaths.Add(baseUriObject.AbsoluteUri.TrimEnd('/'));
        Log.Debug("Додано базовий URL: {BaseUrl} до витягнутих шляхів.", baseUriObject.AbsoluteUri.TrimEnd('/'));

        // Використаємо HtmlAgilityPack для відносних посилань
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(htmlContent);

        var relativeHrefs = htmlDoc.DocumentNode
            .SelectNodes("//a[@href] | //*[@src]")
            ?.Select(node =>
            {
                string href = node.GetAttributeValue("href", null) ?? node.GetAttributeValue("src", null);
                return href;
            })
            .Where(href => !string.IsNullOrEmpty(href) && href.StartsWith("/"))
            .ToList() ?? new List<string>();

        foreach (var foundRelativePath in relativeHrefs)
        {
            try
            {
                Uri combinedUri = new Uri(baseUriObject, foundRelativePath);
                string absoluteCombinedUri = combinedUri.AbsoluteUri.TrimEnd('/');
                extractedPaths.Add(absoluteCombinedUri);
                Log.Debug("Знайдено відносний шлях '{RelativePath}', об'єднано в '{AbsoluteUri}'", foundRelativePath, absoluteCombinedUri);
            }
            catch (UriFormatException ex)
            {
                Log.Warning(ex, "Помилка об'єднання URL: '{FoundRelativePath}' з '{BaseUrl}'.", foundRelativePath, baseUrl);
            }
        }

        Log.Information("Завершено вилучення специфічних відносних шляхів. Знайдено {Count} унікальних шляхів.", extractedPaths.Count);
        return extractedPaths.ToList();
    }
}
