using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CorreosVelsat;
using CorreosVelsat.Api;
using CorreosVelsat.Email;
using CorreosVelsat.Worker;

bool isSendTest = args.Length > 0 && args[0] == "send-test";

var builder = WebApplication.CreateBuilder(isSendTest ? [] : args);

// Las opciones se resuelven desde DI (no antes de Build) para que incluyan toda la configuración final.
builder.Services.AddSingleton(sp => EmailOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => EmailEndpointOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => SmtpOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);

// Proveedores. Un único HttpClient para todo el proceso (connection pooling).
builder.Services.AddSingleton(sp => new CloudflareEmailService(
    CloudflareEmailService.CreateHttpClient(),
    sp.GetRequiredService<EmailOptions>(),
    sp.GetRequiredService<ILogger<CloudflareEmailService>>()));
builder.Services.AddSingleton(sp => new SmtpEmailService(
    sp.GetRequiredService<SmtpOptions>(),
    sp.GetRequiredService<EmailOptions>(),
    sp.GetRequiredService<ILogger<SmtpEmailService>>()));

// Contador diario por proveedor: en MySQL (sobrevive a reinicios) o en memoria si no hay conexión configurada.
builder.Services.AddSingleton<IEmailUsageStore>(sp =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("MySql") is { Length: > 0 } cs
        ? new MySqlEmailUsageStore(cs)
        : new InMemoryEmailUsageStore());

// Todo el backend envía por IEmailService = rotación Cloudflare → Brevo por cupo diario.
builder.Services.AddSingleton(sp =>
{
    var email = sp.GetRequiredService<EmailOptions>();
    var smtp = sp.GetRequiredService<SmtpOptions>();
    var providers = new List<ProviderLimit> { new(sp.GetRequiredService<CloudflareEmailService>(), email.CloudflareDailyLimit) };
    if (smtp.Enabled)
        providers.Add(new(sp.GetRequiredService<SmtpEmailService>(), smtp.DailyLimit));
    return new RoutingEmailService(
        providers,
        sp.GetRequiredService<IEmailUsageStore>(),
        email,
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<RoutingEmailService>>());
});
builder.Services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<RoutingEmailService>());

builder.Services.AddHostedService<MailQueueWorker>();
builder.Services.AddHostedService(sp => new SmtpKeepAliveService(
    sp.GetRequiredService<SmtpOptions>(),
    sp.GetRequiredService<SmtpEmailService>(),
    sp.GetRequiredService<IEmailUsageStore>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<SmtpKeepAliveService>>()));

// Respuestas JSON con tildes legibles ("inválida" en vez de "inválida").
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddApiDocumentation();

builder.Services.AddRateLimiter(options =>
{
    static bool IsEmailApi(HttpContext ctx) => ctx.Request.Path.StartsWithSegments("/api/email");

    // Por IP y por minuto; además un tope diario global para no agotar la cuota de Cloudflare.
    // Los contadores viven en memoria y se reinician al reiniciar el proceso.
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsEmailApi(ctx)
            ? RateLimitPartition.GetFixedWindowLimiter($"ip:{ctx.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = ctx.RequestServices.GetRequiredService<EmailEndpointOptions>().RateLimitPerMinute,
                Window = TimeSpan.FromMinutes(1),
            })
            : RateLimitPartition.GetNoLimiter("")),
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsEmailApi(ctx)
            ? RateLimitPartition.GetFixedWindowLimiter("daily", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = ctx.RequestServices.GetRequiredService<EmailEndpointOptions>().RateLimitPerDay,
                Window = TimeSpan.FromDays(1),
            })
            : RateLimitPartition.GetNoLimiter("")));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ErrorResponse(false, "Demasiadas solicitudes. Intenta de nuevo más tarde."), cancellationToken);
    };
});

var app = builder.Build();

try
{
    _ = app.Services.GetRequiredService<EmailOptions>();
    _ = app.Services.GetRequiredService<SmtpOptions>();
    var routing = app.Services.GetRequiredService<RoutingEmailService>();
    var usageStore = app.Services.GetRequiredService<IEmailUsageStore>();
    var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CorreosVelsat");
    startupLog.LogInformation("Proveedores de correo (en orden): {Proveedores}. Contador diario en {Almacen}.",
        string.Join(", ", routing.Providers.Select(p => $"{p.Provider.Name} ({p.DailyLimit}/día)")),
        usageStore.IsPersistent ? "MySQL (email_provider_usage)" : "memoria (se pierde al reiniciar)");
    if (!isSendTest)
    {
        _ = app.Services.GetRequiredService<EmailEndpointOptions>();
        MailQueueWorker.ValidateConfiguration(app.Configuration);
    }
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Error de configuración: {ex.Message}");
    return 1;
}

if (isSendTest)
    return await SendTestCommand.RunAsync(app.Services, args[1..]);

// Detrás de nginx bajo un prefijo (p. ej. https://do.velsat.pe:8443/correos/): con PathBase=/correos
// las rutas responden tanto con prefijo (/correos/health) como sin él (/health, usado por el HEALTHCHECK).
if (app.Configuration["PathBase"]?.Trim().TrimEnd('/') is { Length: > 0 } pathBase)
    app.UsePathBase(pathBase.StartsWith('/') ? pathBase : "/" + pathBase);
app.UseRouting();

app.UseRateLimiter();
app.MapApiDocumentation();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithTags("Health")
    .WithSummary("Comprueba que el servicio está vivo");
app.MapEmailEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
