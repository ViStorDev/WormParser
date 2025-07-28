namespace WebParser.Helpers;

public static class HtmlLinkExtractor
{
    /// <summary>
    /// Витягує унікальні абсолютні посилання (URL) з HTML-контенту,
    /// включаючи специфічні відносні шляхи, які перетворюються на абсолютні URL.
    /// Виключає посилання, що містять певні підрядки.
    /// </summary>
    /// <param name="htmlContent">Рядок, що містить HTML-контент.</param>
    /// <param name="baseUrl">Базовий URL поточної сторінки для коректного об'єднання відносних шляхів.</param>
    /// <returns>Список унікальних відфільтрованих абсолютних посилань.</returns>
    public static List<string> ExtractAbsoluteLinksOnlyWithRegex(string htmlContent, string baseUrl)
    {
        Log.Information("Початок вилучення абсолютних посилань з HTML-контенту. Базовий URL: {BaseUrl}", baseUrl);

        List<string> directAbsoluteLinks = new List<string>();

        string[] excludedSubstrings = File.ReadAllLines("filterConfig.txt")
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToArray();

        // 1. Витягуємо абсолютні посилання безпосередньо з HTML
        // Увага: ваш початковий регекс має " arbitration" на початку. Можливо, це помилка?
        // Я припускаю, що це було "href" або "src" і виправляю на загальний шаблон.
        Regex attrUrlRegex = new Regex(@"(?:href|src)=[""'](https?:\/\/(?:www\.)?[a-zA-Z0-9\-\.]+\.[a-zA-Z]{2,}(?:\/[^""']*)?)[""']", RegexOptions.IgnoreCase);

        foreach (Match match in attrUrlRegex.Matches(htmlContent))
        {
            string foundUrl = match.Groups[1].Value;
            string urlToAdd = foundUrl;

            try
            {
                if (Uri.TryCreate(foundUrl, UriKind.Absolute, out Uri urlObj))
                {
                    if (urlObj.Fragment.Length > 0)
                    {
                        urlToAdd = urlObj.GetLeftPart(UriPartial.Path);
                        Log.Debug("Видалено фрагмент з URL: {OriginalUrl} -> {CleanedUrl}", foundUrl, urlToAdd);
                    }
                }
            }
            catch (Exception ex)
            {
                // Логуємо помилки парсингу Uri
                Log.Warning(ex, "Помилка парсингу URI '{FoundUrl}' в ExtractAbsoluteLinksOnlyWithRegex. Помилка: {Message}", foundUrl, ex.Message);
            }

            // Фільтрація за виключеними підрядками
            bool shouldAddLink = true;
            foreach (string sub in excludedSubstrings)
            {
                if (urlToAdd.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    shouldAddLink = false;
                    Log.Debug("Посилання '{Url}' відфільтровано через підрядок: {Substring}", urlToAdd, sub);
                    break;
                }
            }

            if (shouldAddLink)
            {
                directAbsoluteLinks.Add(urlToAdd);
                Log.Debug("Додано пряме абсолютне посилання: {Url}", urlToAdd);
            }
        }
        Log.Information("Знайдено {Count} прямих абсолютних посилань.", directAbsoluteLinks.Count);

        // 2. Витягуємо специфічні відносні посилання та об'єднуємо їх з baseUrl
        List<string> relativeLinksAsAbsolute = ExtractSpecificRelativePaths(htmlContent, baseUrl);
        Log.Information("Знайдено {Count} відносних посилань (перетворених на абсолютні).", relativeLinksAsAbsolute.Count);


        // 3. Фільтруємо відносні посилання за excludedSubstrings
        HashSet<string> filteredRelativeLinks = new HashSet<string>();
        foreach (string link in relativeLinksAsAbsolute)
        {
            bool shouldAddRelativeLink = true;
            foreach (string sub in excludedSubstrings)
            {
                if (link.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    shouldAddRelativeLink = false;
                    Log.Debug("Відносне посилання '{Link}' відфільтровано через підрядок: {Substring}", link, sub);
                    break;
                }
            }

            if (shouldAddRelativeLink)
            {
                filteredRelativeLinks.Add(link);
            }
        }
        Log.Information("Після фільтрації залишилося {Count} відфільтрованих відносних посилань.", filteredRelativeLinks.Count);


        // 4. Об'єднуємо два списки за допомогою LINQ Union()
        // Union повертає новий IEnumerable<string>, який містить унікальні елементи з обох колекцій.
        // Потім перетворюємо його на List<string>.
        List<string> finalLinks = directAbsoluteLinks.Union(filteredRelativeLinks).ToList();
        Log.Information("Завершено вилучення посилань. Загальна кількість унікальних посилань: {Count}", finalLinks.Count);
        return finalLinks;
    }


    /// <summary>
    /// Витягує специфічні відносні шляхи з HTML-контенту та перетворює їх на абсолютні URL,
    /// об'єднуючи з наданим базовим URL за допомогою класу Uri.
    /// </summary>
    /// <param name="htmlContent">Рядок, що містить HTML-контент.</param>
    /// <param name="baseUrl">Базовий URL, який буде використовуватися для формування абсолютних шляхів.</param>
    /// <returns>Список унікальних абсолютних URL, отриманих з відносних шляхів.</returns>
    public static List<string> ExtractSpecificRelativePaths(string htmlContent, string baseUrl)
    {
        Log.Information("Початок вилучення специфічних відносних шляхів. Базовий URL: {BaseUrl}", baseUrl);
        HashSet<string> extractedPaths = new HashSet<string>();

        Uri baseUriObject;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUriObject))
        {
            // <-- Логуємо помилку замість Console.WriteLine
            Log.Error("Помилка: Недійсний базовий URL для ExtractSpecificRelativePaths: {BaseUrl}", baseUrl);
            return new List<string>();
        }

        // Додаємо базовий URL як перше витягнуте посилання, оскільки він сам по собі є "шляхом".
        extractedPaths.Add(baseUriObject.AbsoluteUri.TrimEnd('/'));
        Log.Debug("Додано базовий URL: {BaseUrl} до витягнутих шляхів.", baseUriObject.AbsoluteUri.TrimEnd('/'));

        // Ваш поточний регулярний вираз виглядає дуже специфічним: href=["'](\/[a-zA-Z0-9-]+\/)["']
        // Він знаходить лише посилання, які починаються з / і мають тільки букви, цифри та дефіси, закінчуючись на /
        // Це може бути занадто обмежувальним. Якщо ви хочете знайти всі відносні посилання,
        // варто переглянути цей регулярний вираз.
        Regex relativePathRegex = new Regex(@"(?:href|src)=[""'](\/[a-zA-Z0-9\-\/.]+)[""']", RegexOptions.IgnoreCase);

        foreach (Match match in relativePathRegex.Matches(htmlContent))
        {
            string foundRelativePath = match.Groups[1].Value;

            try
            {
                Uri combinedUri = new Uri(baseUriObject, foundRelativePath);
                string absoluteCombinedUri = combinedUri.AbsoluteUri.TrimEnd('/');
                extractedPaths.Add(absoluteCombinedUri);
                Log.Debug("Знайдено відносний шлях '{RelativePath}', об'єднано в '{AbsoluteUri}'", foundRelativePath, absoluteCombinedUri);
            }
            catch (UriFormatException ex)
            {
                // <-- Логуємо попередження замість Console.WriteLine
                Log.Warning(ex, "Помилка об'єднання URL: '{FoundRelativePath}' з '{BaseUrl}'. Помилка: {Message}", foundRelativePath, baseUrl, ex.Message);
            }
        }
        Log.Information("Завершено вилучення специфічних відносних шляхів. Знайдено {Count} унікальних шляхів.", extractedPaths.Count);
        return extractedPaths.ToList();
    }
}