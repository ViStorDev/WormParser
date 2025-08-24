namespace WebParser.Controllers;

[ApiController]
[Route("parser")]
public class ParserController : ControllerBase
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly ILogger<ParserController> _logger;
    private readonly Regex _urlDomainRegex;
    private readonly IConfiguration _configuration;

    private static SemaphoreSlim _semaphore;

    private string DomainNamePattern => _configuration?.GetValue<string>("FilterSettings:DomainNamePattern") ?? "";

    private static int _sleepTime = 0;

    // ✅ зберігаємо всі унікальні вже відправлені посилання
    private static readonly HashSet<string> _sentLinks = new HashSet<string>();

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
        [FromQuery] int sleepTime = 0)
    {
        _sleepTime = sleepTime;

        _logger.LogInformation("Отримано запит на парсинг URL-адрес: {Urls}", string.Join(", ", urls));
        if (!string.IsNullOrEmpty(webhookUrl))
        {
            _logger.LogInformation("Webhook URL для відправки Link об'єктів: {WebhookUrl}", webhookUrl);
            if (_sleepTime > 0)
                _logger.LogInformation("Використовується інтервал відправки вебхуків: {IntervalSeconds} сек.", _sleepTime);
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
            return Ok("Webhook processing initiated. Unique links will be sent to the provided URL.");
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
            if (!string.IsNullOrEmpty(webhookUrl))
                await SendWebhookAsync(webhookUrl, link);
            else
                summ.Links.Add(link);

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
            // ✅ Унікальність
            lock (_sentLinks)
            {
                if (!_sentLinks.Add(link.Url))
                {
                    _logger.LogInformation("URL {Url} вже був відправлений раніше. Пропускаємо.", link.Url);
                    return;
                }
            }

            if (_sleepTime > 0)
                await Task.Delay(_sleepTime * 1000);

            var jsonContent = JsonConvert.SerializeObject(link);
            var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(webhookUrl, httpContent);

            if (response.IsSuccessStatusCode)
                _logger.LogInformation("Успішно відправлено Link на вебхук {WebhookUrl} для URL: {LinkUrl}", webhookUrl, link.Url);
            else
                _logger.LogWarning("Помилка відправки Link на вебхук {WebhookUrl} для URL {LinkUrl}: {StatusCode} - {ReasonPhrase}",
                    webhookUrl, link.Url, response.StatusCode, response.ReasonPhrase);
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
        catch (Exception ex)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = ex.Message
            };
        }
    }
}

