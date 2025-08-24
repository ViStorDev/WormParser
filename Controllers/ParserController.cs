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
    private static SemaphoreSlim _semaphore;

    private string DomainNamePattern => _configuration?.GetValue<string>("FilterSettings:DomainNamePattern") ?? "";

    // ⚡ новий параметр інтервалу (в секундах)
    private static int _intervalSeconds = 0;

    public ParserController(IConfiguration configuration, ILogger<ParserController> logger)
    {
        _configuration = configuration;
        _logger = logger;
        var maxExec = _configuration?.GetValue<int>("ParallelExecutions:MaxNumber") ?? 10;
        _semaphore = new SemaphoreSlim(maxExec, maxExec);
        _urlDomainRegex = new Regex(DomainNamePattern, RegexOptions.IgnoreCase);
    }

    [HttpGet("test")]
    public IActionResult Index() => Ok("ParserController is ready to parse URLs.");

    [HttpGet("siteSummary")]
    public async Task<IActionResult> Get(
        [FromQuery] IEnumerable<string> urls,
        [FromQuery] string? webhookUrl = null,
        [FromQuery] int maxLinks = 0,
        [FromQuery] bool isClean = true,
        [FromQuery] int intervalSeconds = 0) // ✅ новий параметр
    {
        _intervalSeconds = intervalSeconds; // зберігаємо інтервал

        _logger.LogInformation("Отримано запит на парсинг URL-адрес: {Urls}", string.Join(", ", urls));
        if (!string.IsNullOrEmpty(webhookUrl))
        {
            _logger.LogInformation("Webhook URL для відправки Link об'єктів: {WebhookUrl}", webhookUrl);
            if (_intervalSeconds > 0)
                _logger.LogInformation("Використовується інтервал відправки вебхуків: {IntervalSeconds} сек.", _intervalSeconds);
        }

        var summaries = new List<SiteSummary>();
        var visitedUrls = new HashSet<string>();

        var tasks = new List<Task>();

        foreach (var url in urls)
        {
            tasks.Add(Task.Run(async () =>
            {
                await _semaphore.WaitAsync();
                try
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var _))
                    {
                        _logger.LogWarning("Недійсний стартовий URL: {Url}", url);
                        return;
                    }

                    var matchValue = _urlDomainRegex.Matches(url).FirstOrDefault()?.Groups[1].Value;
                    var summ = new SiteSummary { Url = url };

                    await GetSiteSummaryRecursive(url, summ, visitedUrls, webhookUrl, matchValue, maxLinks, isClean);

                    if (string.IsNullOrEmpty(webhookUrl))
                    {
                        summaries.Add(summ);
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }));
        }

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

    private async Task GetSiteSummaryRecursive(
        string url,
        SiteSummary summ,
        HashSet<string> visitedUrls,
        string? webhookUrl,
        string? matchValue,
        int maxLinks,
        bool isClean,
        int currentCount = 0)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var currentUri))
        {
            _logger.LogWarning("Недійсний URL: {Url}", url);
            return;
        }

        if (maxLinks > 0 && currentCount >= maxLinks)
        {
            _logger.LogInformation("Досягнуто ліміт посилань ({MaxLinks}) для URL {Url}", maxLinks, url);
            return;
        }

        string normalizedUrl = currentUri.AbsoluteUri.TrimEnd('/');
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
            _logger.LogInformation("Посилання на додаткові ресурси. {NormalizedUrl}", normalizedUrl);
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

                link.Data = isClean
                    ? TextCleaner.ExtractCleanTextFromHtml(content)
                    : content;

                if (!string.IsNullOrEmpty(webhookUrl))
                    await SendWebhookAsync(webhookUrl, link);
                else
                    summ.Links.Add(link);

                var linksOnPage = HtmlLinkExtractor.ExtractAbsoluteLinksOnlyWithRegex(content, normalizedUrl);
                if (linksOnPage != null && linksOnPage.Any())
                {
                    foreach (var l in linksOnPage)
                    {
                        await GetSiteSummaryRecursive(l, summ, visitedUrls, webhookUrl, matchValue, maxLinks, isClean, summ.Links.Count);
                    }
                }
            }
            else
            {
                _logger.LogError("HTTP Error для {NormalizedUrl}: {StatusCode}", normalizedUrl, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка обробки URL {NormalizedUrl}: {ErrorMessage}", normalizedUrl, ex.Message);
        }
    }

    private async Task SendWebhookAsync(string webhookUrl, Link link)
    {
        try
        {
            var jsonContent = JsonConvert.SerializeObject(link);
            var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

            if (_intervalSeconds > 0)
            {
                await Task.Delay(_intervalSeconds * 1000); // ⏳ додаємо паузу
            }

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

