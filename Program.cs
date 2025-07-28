var builder = WebApplication.CreateBuilder(args);

// --- Налаштування Serilog ---
// Створюємо конфігурацію логера
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug() // Мінімальний рівень логування: Debug і вище
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // Знижуємо рівень для логів від Microsoft-фреймворку
    .MinimumLevel.Override("System", LogEventLevel.Warning)    // Знижуємо рівень для логів від System-фреймворку
    .Enrich.FromLogContext() // Дозволяє додавати контекстні властивості до логів
    .Enrich.WithMachineName() // Тепер доступно завдяки Serilog.Enrichers.Environment
    .Enrich.WithProcessId()   // Тепер доступно завдяки Serilog.Enrichers.Process
    .Enrich.WithThreadId()    // Тепер доступно завдяки Serilog.Enrichers.Thread
    .WriteTo.Console() // Запис логів у консоль (корисно для Docker)
    .WriteTo.File(
        path: "logs/log-.txt", // Шлях до файлів логів усередині контейнера
        rollingInterval: RollingInterval.Day, // Тепер доступно завдяки Serilog.Sinks.File
        fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB на файл
        rollOnFileSizeLimit: true, // Створювати новий файл при досягненні ліміту
        retainedFileCountLimit: 7, // Зберігати останні 7 файлів
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger(); // Створюємо екземпляр логера

// Інтегруємо Serilog з хостом ASP.NET Core
// Це метод розширення, який надається пакетом Serilog.AspNetCore
builder.Host.UseSerilog();
// --- Кінець налаштування Serilog ---


// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Додаємо middleware для логування HTTP-запитів.
// Це метод розширення, який також надається пакетом Serilog.AspNetCore
app.UseSerilogRequestLogging();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();