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

// Un único HttpClient para todo el proceso (connection pooling).
builder.Services.AddSingleton<IEmailService>(sp => new CloudflareEmailService(
    CloudflareEmailService.CreateHttpClient(),
    sp.GetRequiredService<EmailOptions>(),
    sp.GetRequiredService<ILogger<CloudflareEmailService>>()));

builder.Services.AddHostedService<MailQueueWorker>();

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

app.UseRateLimiter();
app.MapApiDocumentation();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithTags("Health")
    .WithSummary("Comprueba que el servicio está vivo");
app.MapEmailEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
