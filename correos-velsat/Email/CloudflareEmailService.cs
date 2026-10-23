using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CorreosVelsat.Email
{
    /// <summary>
    /// Envío de correos por la API REST de Cloudflare Email Sending (HTTPS, sin SMTP).
    /// https://developers.cloudflare.com/api/resources/email_sending/methods/send
    /// </summary>
    public sealed class CloudflareEmailService : IEmailService, IEmailProvider
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>email.sending.error.throttled</summary>
        private const int ThrottledErrorCode = 10004;

        // Retry-After solo se usa para decidir cuánto bloquear el proveedor (mínimo 15 min en RoutingEmailService).
        private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

        public string Name => EmailProviderNames.Cloudflare;

        private readonly HttpClient _http;
        private readonly EmailOptions _options;
        private readonly ILogger<CloudflareEmailService> _logger;
        private readonly Uri _endpoint;

        /// <summary>Espera entre reintentos; reemplazable en tests.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        public CloudflareEmailService(HttpClient http, EmailOptions options, ILogger<CloudflareEmailService> logger)
        {
            _http = http;
            _options = options;
            _logger = logger;
            _endpoint = new Uri($"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(options.AccountId)}/email/sending/send");
        }

        /// <summary>
        /// HttpClient compartido para toda la vida del proceso: reutiliza conexiones y las recicla
        /// cada pocos minutos para respetar cambios de DNS. El timeout se aplica por intento.
        /// </summary>
        public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            EmailMessage resolved = message;
            EmailSendResult result;
            try
            {
                resolved = EmailValidator.ApplyDefaults(message, _options);
                var errors = EmailValidator.Validate(resolved, _options);
                result = errors.Count > 0
                    ? EmailSendResult.Invalid(errors)
                    : await SendWithRetriesAsync(resolved, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inesperado enviando correo a {To}", string.Join(", ", resolved.To));
                result = EmailSendResult.Fail(EmailErrorKind.Unexpected, "Error inesperado al enviar el correo.");
            }

            LogResult(resolved, result, stopwatch.Elapsed);
            return result;
        }

        private async Task<EmailSendResult> SendWithRetriesAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(BuildPayload(message), JsonOptions);

            for (int attempt = 1; ; attempt++)
            {
                var (result, transient, retryAfter) = await SendOnceAsync(body, cancellationToken);
                result = result with { Attempts = attempt, Provider = Name };

                if (result.Success || !transient || attempt > _options.MaxRetries)
                    return result;

                TimeSpan delay = retryAfter ?? Backoff(attempt);
                _logger.LogWarning(
                    "Fallo transitorio enviando correo (intento {Attempt}/{MaxAttempts}): {Error}. Reintento en {DelayMs} ms",
                    attempt, _options.MaxRetries + 1, result.ErrorMessage, (int)delay.TotalMilliseconds);
                await Delay(delay, cancellationToken);
            }
        }

        private async Task<(EmailSendResult Result, bool Transient, TimeSpan? RetryAfter)> SendOnceAsync(byte[] body, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new ByteArrayContent(body),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

                using var response = await _http.SendAsync(request, timeout.Token);
                var parsed = await TryReadResponseAsync(response, timeout.Token);
                int status = (int)response.StatusCode;

                if (response.IsSuccessStatusCode && parsed?.Success == true)
                {
                    return (new EmailSendResult
                    {
                        Success = true,
                        MessageId = parsed.Result?.MessageId,
                        HttpStatus = status,
                        PermanentBounces = parsed.Result?.PermanentBounces ?? [],
                        SuppressedRecipients = parsed.Result?.SuppressedRecipients ?? [],
                    }, false, null);
                }

                var error = parsed?.Errors?.FirstOrDefault();
                string message = error is not null ? $"{error.Code}: {error.Message}" : $"HTTP {status}";
                // 429 / 10004 (email.sending.error.throttled): no se reintenta aquí; RoutingEmailService
                // bloquea Cloudflare y pasa a Brevo en el mismo correo.
                bool throttled = status == (int)HttpStatusCode.TooManyRequests || error?.Code == ThrottledErrorCode;
                bool transient = !throttled && status >= 500;
                var kind = throttled ? EmailErrorKind.RateLimited : status switch
                {
                    401 or 403 => EmailErrorKind.Authentication,
                    >= 500 => EmailErrorKind.ProviderError,
                    _ => EmailErrorKind.Rejected,
                };
                var failure = EmailSendResult.Fail(kind, message, status, error?.Code) with
                {
                    RetryAfter = throttled ? GetRetryAfter(response) : null,
                };
                return (failure, transient, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (EmailSendResult.Fail(EmailErrorKind.Timeout, $"Timeout de {(int)_options.Timeout.TotalMilliseconds} ms"), true, null);
            }
            catch (HttpRequestException ex)
            {
                return (EmailSendResult.Fail(EmailErrorKind.Network, $"Error de red: {ex.Message}"), true, null);
            }
        }

        private static async Task<CloudflareResponse?> TryReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                return await response.Content.ReadFromJsonAsync<CloudflareResponse>(JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Respuestas no JSON (p. ej. un 502 HTML del edge): se tratan por código HTTP.
                return null;
            }
        }

        private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            TimeSpan? delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            if (delay is null)
                return null;
            if (delay < TimeSpan.Zero)
                return TimeSpan.Zero;
            return delay > MaxRetryAfter ? MaxRetryAfter : delay;
        }

        /// <summary>Backoff exponencial con jitter: entre 50% y 100% de base·2^(n-1), con tope.</summary>
        private TimeSpan Backoff(int attempt)
        {
            double exponential = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
            double capped = Math.Min(exponential, _options.MaxRetryDelay.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(capped / 2 + Random.Shared.NextDouble() * capped / 2);
        }

        private static Dictionary<string, object> BuildPayload(EmailMessage message)
        {
            var payload = new Dictionary<string, object>
            {
                ["from"] = message.FromName is null
                    ? message.From!
                    : new CloudflareAddress(message.From!, message.FromName),
                ["to"] = message.To,
                ["subject"] = message.Subject,
            };
            if (message.Cc.Count > 0)
                payload["cc"] = message.Cc;
            if (message.Bcc.Count > 0)
                payload["bcc"] = message.Bcc;
            if (message.ReplyTo is not null)
                payload["reply_to"] = message.ReplyTo;
            if (message.Html is not null)
                payload["html"] = message.Html;
            if (message.Text is not null)
                payload["text"] = message.Text;
            if (message.Attachments.Count > 0)
                payload["attachments"] = message.Attachments
                    .Select(a => new CloudflareAttachment(Convert.ToBase64String(a.Content), a.FileName, a.ContentType))
                    .ToArray();
            return payload;
        }

        private void LogResult(EmailMessage message, EmailSendResult result, TimeSpan elapsed)
        {
            string to = string.Join(", ", message.To);
            long durationMs = (long)elapsed.TotalMilliseconds;

            if (result.Success)
            {
                _logger.LogInformation(
                    "Correo enviado. To={To} Cc={CcCount} Bcc={BccCount} From={From} Subject={Subject} MessageId={MessageId} Intentos={Attempts} DuracionMs={DurationMs}",
                    to, message.Cc.Count, message.Bcc.Count, message.From, message.Subject, result.MessageId, result.Attempts, durationMs);

                if (result.PermanentBounces.Count > 0 || result.SuppressedRecipients.Count > 0)
                {
                    _logger.LogWarning(
                        "Correo {MessageId}: rebotes permanentes={Bounces}; suprimidos={Suppressed}",
                        result.MessageId, string.Join(", ", result.PermanentBounces), string.Join(", ", result.SuppressedRecipients));
                }
                return;
            }

            _logger.LogWarning(
                "Correo NO enviado. To={To} From={From} Subject={Subject} Error={ErrorKind} Detalle={Error} HttpStatus={HttpStatus} Intentos={Attempts} DuracionMs={DurationMs}",
                to, message.From, message.Subject, result.ErrorKind, result.ErrorMessage, result.HttpStatus, result.Attempts, durationMs);
        }

        private sealed record CloudflareAddress(
            [property: JsonPropertyName("address")] string Address,
            [property: JsonPropertyName("name")] string Name);

        private sealed record CloudflareAttachment(
            [property: JsonPropertyName("content")] string Content,
            [property: JsonPropertyName("filename")] string FileName,
            [property: JsonPropertyName("type")] string Type);

        private sealed class CloudflareResponse
        {
            [JsonPropertyName("success")] public bool Success { get; set; }
            [JsonPropertyName("errors")] public List<CloudflareError>? Errors { get; set; }
            [JsonPropertyName("result")] public CloudflareResult? Result { get; set; }
        }

        private sealed class CloudflareError
        {
            [JsonPropertyName("code")] public int Code { get; set; }
            [JsonPropertyName("message")] public string Message { get; set; } = "";
        }

        private sealed class CloudflareResult
        {
            [JsonPropertyName("message_id")] public string? MessageId { get; set; }
            [JsonPropertyName("permanent_bounces")] public List<string>? PermanentBounces { get; set; }
            [JsonPropertyName("suppressed_recipients")] public List<string>? SuppressedRecipients { get; set; }
        }
    }
}
