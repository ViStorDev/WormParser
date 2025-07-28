namespace WebParser.Helpers;

using HtmlAgilityPack; // Не забудьте додати цей using
using System;
using System.Linq;   // Для .Where, .ToList(), .ForEach
using System.Net;    // Для WebUtility.HtmlDecode
using System.Text.RegularExpressions; // Для нормалізації пробілів

public static class TextCleaner
{
    /// <summary>
    /// Витягує чистий текст з HTML-рядка за допомогою HtmlAgilityPack.
    /// Це більш надійний і рекомендований підхід для очищення HTML.
    /// </summary>
    /// <param name="htmlString">Вхідний HTML-контент.</param>
    /// <returns>Очищений текстовий рядок.</returns>
    public static string ExtractCleanTextFromHtml(string htmlString)
    {
        // Перевірка вхідних даних
        if (string.IsNullOrWhiteSpace(htmlString))
        {
            Console.WriteLine("[ExtractCleanTextFromHtml] Вхідний HTML-контент відсутній або некоректний.");
            return string.Empty; // Повертаємо порожній рядок, якщо вхід невалідний
        }

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(htmlString); // Завантажуємо HTML у DOM-модель

        // --- Крок 1: Видаляємо теги <script>, <style> та <a> (включаючи їх вміст) ---
        // HtmlAgilityPack дозволяє ефективно знаходити та видаляти вузли за їхньою назвою.
        htmlDoc.DocumentNode.Descendants()
            .Where(n => n.Name == "script" || n.Name == "style" || n.Name == "a")
            .ToList() // Важливо перетворити на List, щоб уникнути винятку під час зміни колекції
            .ForEach(n => n.Remove());

        // --- Крок 2: Отримуємо текст з документу ---
        // HtmlAgilityPack's InnerText автоматично видаляє всі інші HTML-теги
        // і виконує базове декодування HTML-сутностей.
        string cleanText = htmlDoc.DocumentNode.InnerText;

        // --- Крок 3: Додаткове декодування HTML-сутностей ---
        // WebUtility.HtmlDecode забезпечує більш повне декодування (наприклад, &#039;, &nbsp; тощо).
        cleanText = WebUtility.HtmlDecode(cleanText);

        // --- Крок 4: Нормалізуємо пробіли ---
        // Замінюємо більше одного пустого рядка на один-два (\n\n) для збереження структури абзаців.
        // RegexOptions.Multiline дозволяє ^ і $ збігатися з початком/кінцем рядка.
        // RegexOptions.Singleline дозволяє . збігатися з \n (хоча тут не критично, бо ми працюємо з \s)
        cleanText = Regex.Replace(cleanText, @"(\s*\r?\n\s*){2,}", "\n\n", RegexOptions.Multiline);

        // Замінюємо будь-яку послідовність пробілів (включаючи табуляції, нові рядки) на один пробіл.
        // Цей крок виконується ПІСЛЯ обробки абзаців, щоб не "злипати" абзаци в один рядок.
        cleanText = Regex.Replace(cleanText, @"\s+", " ", RegexOptions.None);

        // Видаляємо пробіли з початку та кінця рядка.
        cleanText = cleanText.Trim();

        // --- Крок 5: Видаляємо невидимі керуючі символи ---
        // Цей крок є останнім для забезпечення максимальної чистоти тексту.
        cleanText = Regex.Replace(cleanText, @"[\u0000-\u001F\u007F-\u009F]", string.Empty);

        Console.WriteLine($"[ExtractCleanTextFromHtml] Успішно вилучено чистий текст. Довжина: {cleanText.Length} символів.");
        // Console.WriteLine($"[ExtractCleanTextFromHtml] Перші 200 символів чистого тексту:\n{cleanText.Substring(0, Math.Min(cleanText.Length, 200))}...");

        return cleanText;
    }
}