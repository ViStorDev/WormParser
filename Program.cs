var builder = WebApplication.CreateBuilder(args);

// --- ������������ Serilog ---
// ��������� ������������ ������
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug() // ̳�������� ����� ���������: Debug � ����
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning) // ������� ����� ��� ���� �� Microsoft-����������
    .MinimumLevel.Override("System", LogEventLevel.Warning)    // ������� ����� ��� ���� �� System-����������
    .Enrich.FromLogContext() // �������� �������� ��������� ���������� �� ����
    .Enrich.WithMachineName() // ����� �������� ������� Serilog.Enrichers.Environment
    .Enrich.WithProcessId()   // ����� �������� ������� Serilog.Enrichers.Process
    .Enrich.WithThreadId()    // ����� �������� ������� Serilog.Enrichers.Thread
    .WriteTo.Console() // ����� ���� � ������� (������� ��� Docker)
    .WriteTo.File(
        path: "logs/log-.txt", // ���� �� ����� ���� �������� ����������
        rollingInterval: RollingInterval.Day, // ����� �������� ������� Serilog.Sinks.File
        fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB �� ����
        rollOnFileSizeLimit: true, // ���������� ����� ���� ��� ��������� ����
        retainedFileCountLimit: 7, // �������� ������ 7 �����
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger(); // ��������� ��������� ������

// ��������� Serilog � ������ ASP.NET Core
// �� ����� ����������, ���� �������� ������� Serilog.AspNetCore
builder.Host.UseSerilog();
// --- ʳ���� ������������ Serilog ---


// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// ������ middleware ��� ��������� HTTP-������.
// �� ����� ����������, ���� ����� �������� ������� Serilog.AspNetCore
app.UseSerilogRequestLogging();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();