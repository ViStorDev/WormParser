namespace WebParser.Controllers;

[ApiController]
[Route("parser")]
public class ParserController : ControllerBase
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly HashSet<string> _visitedUrls = new HashSet<string>();
    private readonly ILogger<ParserController> _logger;
    string DomainNamePattern => _configuration?.GetValue<string>("FilterSettings:DomainNamePattern")??"";
    Regex urlDomainRegex;
    private readonly IConfiguration _configuration;

    public ParserController(IConfiguration configuration, ILogger<ParserController> logger)
    {
        urlDomainRegex = new Regex(DomainNamePattern, RegexOptions.IgnoreCase);
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("test")]
    public IActionResult Index()
    {
        return Ok("ParserController is ready to parse URLs.");
    }

    /// <summary>
    /// Основна точка входу API для отримання зведення сайту(ів) або відправки вебхуків.
    /// </summary>
    /// <param name="urls">Масив URL-адрес для парсингу.</param>
    /// <param name="webhookUrl">Опціональний URL вебхука для відправки кожного Link по одному.</param>
    /// <returns>Список об'єктів SiteSummary, або OK, якщо використовується вебхук.</returns>
    [HttpGet("siteSummary")]
    public async Task<IActionResult> Get(
        [FromQuery] IEnumerable<string> urls,
        [FromQuery] string? webhookUrl = null) // <--- Додано опціональний параметр webhookUrl
    {
        //Regex urlDomainRegex = new Regex(UrlPattern, RegexOptions.IgnoreCase);

        _logger.LogInformation("Отримано запит на парсинг URL-адрес: {Urls}", string.Join(", ", urls));
        if (!string.IsNullOrEmpty(webhookUrl))
        {
            _logger.LogInformation("Webhook URL для відправки Link об'єктів: {WebhookUrl}", webhookUrl);
        }

        List<SiteSummary> summaries = new List<SiteSummary>();
        _visitedUrls.Clear(); // Очищаємо історію відвідувань для нового запиту.

        foreach (var url in urls)
        {
            // Якщо є URL вебхука, ми не будемо збирати Links у summ,
            // а відправлятимемо їх напряму через вебхук.
            SiteSummary summ = new SiteSummary { Url = url }; // summ все одно потрібен для кореневого URL

            
            var matchValue = urlDomainRegex.Matches(url).FirstOrDefault().Groups[1].Value;


            await GetSiteSummaryRecursive(url, summ, _visitedUrls, webhookUrl, matchValue); // <--- Передаємо webhookUrl

            // Якщо webhookUrl не надано, ми все одно повертаємо SiteSummary з агрегованими Link.
            // Якщо webhookUrl надано, summ.Links буде порожнім (або міститиме лише Link для кореня, якщо ви зміните логіку).
            // Залежно від вашої вимоги, ви можете повертати Ok("Webhook processing initiated")
            // якщо webhookUrl присутній, і не додавати summ до summaries.
            if (string.IsNullOrEmpty(webhookUrl))
            {
                summaries.Add(summ);
            }
        }

        if (!string.IsNullOrEmpty(webhookUrl))
        {
            _logger.LogInformation("Запит на парсинг завершено (обробка вебхуків).");
            return Ok("Webhook processing initiated. Links will be sent to the provided URL.");
        }
        else
        {
            _logger.LogInformation("Запит на парсинг завершено. Знайдено {Count} зведень.", summaries.Count);
            // Використовуємо автоматичну серіалізацію, оскільки вона зазвичай працює коректно.
            // Якщо у вас були проблеми з нею, поверніть ручну серіалізацію.
            return Ok(summaries);
        }
    }

    /// <summary>
    /// Рекурсивно отримує зведену інформацію про сайт, обходячи посилання.
    /// Може відправляти кожен Link об'єкт через вебхук.
    /// </summary>
    /// <param name="url">URL сторінки для обробки.</param>
    /// <param name="summ">Об'єкт SiteSummary (використовується для кореневого URL та, можливо, агрегації без вебхука).</param>
    /// <param name="visitedUrls">HashSet для відстеження вже відвіданих URL, щоб уникнути циклів.</param>
    /// <param name="webhookUrl">Опціональний URL вебхука.</param>
    private async Task GetSiteSummaryRecursive(string url, SiteSummary summ, HashSet<string> visitedUrls, string? webhookUrl, string? matchValue)
    {
        Uri currentUri;
        bool isWebhookUrlProvided = !string.IsNullOrEmpty(webhookUrl);
        bool isMatchValueProvided = !string.IsNullOrEmpty(matchValue);
        //Regex urlDomainRegex = new Regex(UrlPattern, RegexOptions.IgnoreCase);
        var domainName = urlDomainRegex.Matches(url).FirstOrDefault()?.Groups[1]?.Value;
        bool isDomainNameProvided = !string.IsNullOrEmpty(domainName);

        if (!Uri.TryCreate(url, UriKind.Absolute, out currentUri))
        {
            _logger.LogWarning("Недійсний URL: {Url}", url);
            // Якщо URL недійсний, ігноруємо його для вебхука теж.
            if (isWebhookUrlProvided)
            {
                // Опціонально: відправити вебхук про помилку
                await SendWebhookAsync(webhookUrl, new Link { Url = url, Data = "Error: Invalid URL" });
            }
            return;
        }
        string normalizedUrl = currentUri.AbsoluteUri.TrimEnd('/');

        if (visitedUrls.Contains(normalizedUrl))
        {
            _logger.LogInformation("URL {NormalizedUrl} вже був відвіданий. Пропуск рекурсії.", normalizedUrl);
            return;
        }

        visitedUrls.Add(normalizedUrl);

        Link currentLink = new Link { Url = normalizedUrl }; // Створюємо Link для поточної сторінки
        
        if (isMatchValueProvided && isDomainNameProvided && matchValue != domainName)
        {
            _logger.LogInformation($"URL {normalizedUrl} не містить matchValue {matchValue}. Пропуск.", normalizedUrl, matchValue);
            currentLink.Data = "Посилання на додаткові ресурти";
            if (isWebhookUrlProvided)
            {
                // Якщо webhookUrl надано, відправляємо Link об'єкт через вебхук.
                await SendWebhookAsync(webhookUrl, currentLink);
            }
            else
            {
                // Інакше (якщо вебхук не використовується), додаємо Link до SiteSummary.
                summ.Links.Add(currentLink);
            }
            return; // Якщо URL не містить matchValue, пропускаємо його
        }

        try
        {
            var response = await GetContentAsync(normalizedUrl);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                string extractedCleanText = TextCleaner.ExtractCleanTextFromHtml(content);

                currentLink.Data = string.IsNullOrWhiteSpace(extractedCleanText)
                                 ? "Не знайдено інформації"
                                 : extractedCleanText;

                // <--- КЛЮЧОВА ЛОГІКА ДЛЯ ВЕБХУКА --->
                if (isWebhookUrlProvided)
                {
                    // Якщо webhookUrl надано, відправляємо Link об'єкт через вебхук.
                    await SendWebhookAsync(webhookUrl, currentLink);
                }
                else
                {
                    // Інакше (якщо вебхук не використовується), додаємо Link до SiteSummary.
                    summ.Links.Add(currentLink);
                }
                // <----------------------------------->

                var linksOnPage = HtmlLinkExtractor.ExtractAbsoluteLinksOnlyWithRegex(content, normalizedUrl);

                if (linksOnPage == null || !linksOnPage.Any())
                {
                    _logger.LogInformation("Не знайдено посилань на сторінці: {NormalizedUrl}. Зупинка рекурсії.", normalizedUrl);
                    return;
                }

                foreach (var l in linksOnPage)
                {
                    // Рекурсивно викликаємо, передаючи той самий webhookUrl
                    await GetSiteSummaryRecursive(l, summ, visitedUrls, webhookUrl, matchValue);
                }
            }
            else
            {
                _logger.LogError("HTTP Error для {NormalizedUrl}: {StatusCode}", normalizedUrl, response.StatusCode);
                currentLink.Data = $"Error: {response.StatusCode}";

                // <--- ЛОГІКА ПОМИЛКИ З ВЕБХУКОМ/БЕЗ НЬОГО --->
                if (isWebhookUrlProvided)
                {
                    await SendWebhookAsync(webhookUrl, currentLink); // Відправляємо Link з помилкою
                }
                else
                {
                    summ.Links.Add(currentLink); // Додаємо Link з помилкою до SiteSummary
                }
                // <-------------------------------------->
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка обробки URL {NormalizedUrl}: {ErrorMessage}", normalizedUrl, ex.Message);
            currentLink.Data = $"Error: {ex.Message}";

            // <--- ЛОГІКА ПОМИЛКИ З ВЕБХУКОМ/БЕЗ НЬОГО --->
            if (isWebhookUrlProvided)
            {
                await SendWebhookAsync(webhookUrl, currentLink); // Відправляємо Link з помилкою
            }
            else
            {
                summ.Links.Add(currentLink); // Додаємо Link з помилкою до SiteSummary
            }
            // <-------------------------------------->
        }
    }

    /// <summary>
    /// Відправляє Link об'єкт на заданий URL вебхука.
    /// </summary>
    /// <param name="webhookUrl">URL вебхука.</param>
    /// <param name="link">Об'єкт Link для відправки.</param>
    private async Task SendWebhookAsync(string webhookUrl, Link link)
    {
        try
        {
            var jsonContent = JsonConvert.SerializeObject(link);
            var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            // Використовуємо _httpClient для відправки POST-запиту
            var response = await _httpClient.PostAsync(webhookUrl, httpContent);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Успішно відправлено Link на вебхук {WebhookUrl} для URL: {LinkUrl}", webhookUrl, link.Url);
            }
            else
            {
                _logger.LogWarning("Помилка відправки Link на вебхук {WebhookUrl} для URL {LinkUrl}: {StatusCode} - {ReasonPhrase}",
                    webhookUrl, link.Url, response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критична помилка при відправці вебхука на {WebhookUrl} для URL {LinkUrl}: {ErrorMessage}",
                webhookUrl, link.Url, ex.Message);
        }
    }

    private async Task<HttpResponseMessage> GetContentAsync(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP Request Error fetching {Url}: {StatusCode} - {Message}", url, ex.StatusCode, ex.Message);
            return new HttpResponseMessage(ex.StatusCode ?? System.Net.HttpStatusCode.BadRequest)
            {
                ReasonPhrase = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Error fetching {Url}: {Message}", url, ex.Message);
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = ex.Message
            };
        }
    }
}