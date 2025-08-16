namespace WebParser.Controllers;

[ApiController]
[Route("parser")]
public class ParserController : ControllerBase
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly ILogger<ParserController> _logger;
    private readonly Regex _urlDomainRegex;
    private readonly IConfiguration _configuration;

    // Обмежувач паралельних запитів для запобігання перевантаженню.
    // За замовчуванням дозволяє 10 одночасних запитів.
    private static SemaphoreSlim _semaphore;

    private string DomainNamePattern => _configuration?.GetValue<string>("FilterSettings:DomainNamePattern") ?? "";

    public ParserController(IConfiguration configuration, ILogger<ParserController> logger)
    {
        _configuration = configuration;
        _logger = logger;
        // Створення Regex об'єкта в конструкторі, щоб уникнути перевитрат ресурсів
        // при кожному запиті.
        _semaphore = new SemaphoreSlim(10, _configuration?.GetValue<int>("ParallelExecutions:MaxNumber") ?? 10);
        _urlDomainRegex = new Regex(DomainNamePattern, RegexOptions.IgnoreCase);
    }

    [HttpGet("test")]
    public IActionResult Index() => Ok("ParserController is ready to parse URLs.");

    [HttpGet("siteSummary")]
    public async Task<IActionResult> Get(
        [FromQuery] IEnumerable<string> urls,
        [FromQuery] string? webhookUrl = null)
    {
        _logger.LogInformation("Отримано запит на парсинг URL-адрес: {Urls}", string.Join(", ", urls));

        if (!string.IsNullOrEmpty(webhookUrl))
        {
            _logger.LogInformation("Webhook URL для відправки Link об'єктів: {WebhookUrl}", webhookUrl);
        }

        var summaries = new List<SiteSummary>();
        var visitedUrls = new HashSet<string>();

        // Створюємо список завдань, щоб обробляти початкові URL паралельно.
        var tasks = new List<Task>();

        foreach (var url in urls)
        {
            tasks.Add(Task.Run(async () =>
            {
                // Зачекати, поки не звільниться слот в семафорі.
                await _semaphore.WaitAsync();
                try
                {
                    var matchValue = _urlDomainRegex.Matches(url).FirstOrDefault()?.Groups[1].Value;
                    var summ = new SiteSummary { Url = url };

                    // Викликаємо рекурсивну функцію для обробки сайту.
                    await GetSiteSummaryRecursive(url, summ, visitedUrls, webhookUrl, matchValue);

                    if (string.IsNullOrEmpty(webhookUrl))
                    {
                        // Додаємо зведення, тільки якщо не використовуємо вебхук.
                        summaries.Add(summ);
                    }
                }
                finally
                {
                    // Звільняємо слот, щоб інше завдання могло виконуватися.
                    _semaphore.Release();
                }
            }));
        }

        // Чекаємо, поки всі початкові завдання (для кожного URL з вхідного списку) будуть завершені.
        await Task.WhenAll(tasks);

        if (!string.IsNullOrEmpty(webhookUrl))
        {
            _logger.LogInformation("Запит на парсинг завершено (обробка вебхуків).");
            return Ok("Webhook processing initiated. Links will be sent to the provided URL.");
        }
        else
        {
            _logger.LogInformation("Запит на парсинг завершено. Знайдено {Count} зведень.", summaries.Count);
            return Ok(summaries);
        }
    }

    private async Task GetSiteSummaryRecursive(string url, SiteSummary summ, HashSet<string> visitedUrls, string? webhookUrl, string? matchValue)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var currentUri))
        {
            _logger.LogWarning("Недійсний URL: {Url}", url);
            // Якщо webhookUrl надано, відправляємо Link з помилкою.
            if (!string.IsNullOrEmpty(webhookUrl))
            {
                await SendWebhookAsync(webhookUrl, new Link { Url = url, Data = "Error: Invalid URL" });
            }
            return;
        }

        string normalizedUrl = currentUri.AbsoluteUri.TrimEnd('/');

        // Перевірка та додавання до відвіданих в одному виразі.
        if (!visitedUrls.Add(normalizedUrl))
        {
            _logger.LogInformation("URL {NormalizedUrl} вже був відвіданий. Пропуск рекурсії.", normalizedUrl);
            return;
        }

        var link = new Link { Url = normalizedUrl };
        var domainName = _urlDomainRegex.Matches(url).FirstOrDefault()?.Groups[1]?.Value;
        bool isExternalDomain = !string.IsNullOrEmpty(matchValue) && !string.IsNullOrEmpty(domainName) && matchValue != domainName;

        if (isExternalDomain)
        {
            link.Data = "Посилання на додаткові ресурси";
            if (!string.IsNullOrEmpty(webhookUrl))
            {
                await SendWebhookAsync(webhookUrl, link);
            }
            else
            {
                summ.Links.Add(link);
            }
            return;
        }

        try
        {
            var response = await GetContentAsync(normalizedUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                link.Data = TextCleaner.ExtractCleanTextFromHtml(content);

                // Відправка або додавання Link об'єкта.
                if (!string.IsNullOrEmpty(webhookUrl))
                {
                    await SendWebhookAsync(webhookUrl, link);
                }
                else
                {
                    summ.Links.Add(link);
                }

                var linksOnPage = HtmlLinkExtractor.ExtractAbsoluteLinksOnlyWithRegex(content, normalizedUrl);
                if (linksOnPage != null && linksOnPage.Any())
                {
                    // Рекурсивні виклики тепер також виконуються паралельно за допомогою Task.WhenAll.
                    var tasks = linksOnPage.Select(l => GetSiteSummaryRecursive(l, summ, visitedUrls, webhookUrl, matchValue)).ToList();
                    await Task.WhenAll(tasks);
                }
            }
            else
            {
                link.Data = $"Error: {response.StatusCode}";
                if (!string.IsNullOrEmpty(webhookUrl))
                {
                    await SendWebhookAsync(webhookUrl, link);
                }
                else
                {
                    summ.Links.Add(link);
                }
                _logger.LogError("HTTP Error для {NormalizedUrl}: {StatusCode}", normalizedUrl, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка обробки URL {NormalizedUrl}: {ErrorMessage}", normalizedUrl, ex.Message);
            link.Data = $"Error: {ex.Message}";
            if (!string.IsNullOrEmpty(webhookUrl))
            {
                await SendWebhookAsync(webhookUrl, link);
            }
            else
            {
                summ.Links.Add(link);
            }
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