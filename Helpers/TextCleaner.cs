namespace WebParser.Helpers;

public static class TextCleaner
{
    public static string ExtractCleanTextFromHtml(string htmlString)
    {
        if (string.IsNullOrWhiteSpace(htmlString))
        {
            Log.Warning("[TextCleaner] Вхідний HTML-контент відсутній або некоректний.");
            return string.Empty;
        }

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(htmlString);

        // Видаляємо <script> і <style>, але <a> перетворюємо на їхній текст
        htmlDoc.DocumentNode
            .SelectNodes("//script|//style")
            ?.ToList()
            .ForEach(n => n.Remove());

        htmlDoc.DocumentNode
            .SelectNodes("//a")
            ?.ToList()
            .ForEach(n => n.ParentNode.ReplaceChild(HtmlTextNode.CreateNode(n.InnerText), n));

        string cleanText = WebUtility.HtmlDecode(htmlDoc.DocumentNode.InnerText);

        // Нормалізуємо абзаци
        cleanText = Regex.Replace(cleanText, @"(\r?\n\s*){2,}", "\n\n", RegexOptions.Multiline | RegexOptions.Compiled);

        // Нормалізуємо пробіли (але не чіпаємо \n\n для абзаців)
        cleanText = Regex.Replace(cleanText, @"[ \t]+", " ", RegexOptions.Compiled);

        // Видаляємо керуючі символи
        cleanText = Regex.Replace(cleanText, @"[\u0000-\u001F\u007F-\u009F]", "", RegexOptions.Compiled);

        cleanText = cleanText.Trim();

        Log.Information("[TextCleaner] Успішно вилучено чистий текст. Довжина: {Length} символів.", cleanText.Length);
        return cleanText;
    }
}
