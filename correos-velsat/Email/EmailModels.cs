namespace CorreosVelsat.Email
{
    public interface IEmailService
    {
        /// <summary>
        /// Envía un correo. Nunca lanza excepciones por fallos de envío: el resultado indica éxito o error.
        /// Solo propaga OperationCanceledException si se cancela <paramref name="cancellationToken"/>.
        /// </summary>
        Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
    }

    /// <summary>Un proveedor concreto de envío (Cloudflare, Brevo). <see cref="RoutingEmailService"/> elige entre ellos.</summary>
    public interface IEmailProvider
    {
        /// <summary>Nombre del proveedor usado en logs y en el contador diario: "Cloudflare" o "Brevo".</summary>
        string Name { get; }

        Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
    }

    public static class EmailProviderNames
    {
        public const string Cloudflare = "Cloudflare";
        public const string Brevo = "Brevo";
    }

    public sealed record EmailMessage
    {
        public IReadOnlyList<string> To { get; init; } = [];
        public IReadOnlyList<string> Cc { get; init; } = [];
        public IReadOnlyList<string> Bcc { get; init; } = [];

        /// <summary>Si es null se usa Email:DefaultFrom (y Email:DefaultFromName).</summary>
        public string? From { get; init; }
        public string? FromName { get; init; }
        public string? ReplyTo { get; init; }
        public string Subject { get; init; } = "";

        /// <summary>Al menos uno de Html o Text. Si solo viene Html se genera Text automáticamente.</summary>
        public string? Html { get; init; }
        public string? Text { get; init; }
    }

    public enum EmailErrorKind
    {
        /// <summary>El mensaje no pasó la validación local; no se llamó a Cloudflare.</summary>
        Validation,
        /// <summary>Cloudflare respondió 401/403 (token inválido o sin permisos).</summary>
        Authentication,
        /// <summary>Cloudflare rechazó el mensaje (otro 4xx).</summary>
        Rejected,
        /// <summary>
        /// Límite/cuota del proveedor (Cloudflare 429 / 10004, SMTP con error de límite), o ningún proveedor con cupo.
        /// No se reintenta: el router pasa al otro proveedor y el worker deja el correo pendiente.
        /// </summary>
        RateLimited,
        /// <summary>5xx persistente tras agotar los reintentos.</summary>
        ProviderError,
        Timeout,
        Network,
        Unexpected,
    }

    public sealed record EmailSendResult
    {
        public bool Success { get; init; }
        public string? MessageId { get; init; }
        public EmailErrorKind? ErrorKind { get; init; }
        public string? ErrorMessage { get; init; }
        public int? HttpStatus { get; init; }
        public int? ProviderErrorCode { get; init; }
        public IReadOnlyList<string> ValidationErrors { get; init; } = [];
        public IReadOnlyList<string> PermanentBounces { get; init; } = [];
        public IReadOnlyList<string> SuppressedRecipients { get; init; } = [];
        public int Attempts { get; init; }

        /// <summary>Proveedor que envió (o el último que se intentó).</summary>
        public string? Provider { get; init; }

        /// <summary>Retry-After indicado por el proveedor en un RateLimited, si lo dio.</summary>
        public TimeSpan? RetryAfter { get; init; }

        public static EmailSendResult Invalid(IReadOnlyList<string> errors) => new()
        {
            ErrorKind = EmailErrorKind.Validation,
            ErrorMessage = string.Join(" ", errors),
            ValidationErrors = errors,
        };

        public static EmailSendResult Fail(EmailErrorKind kind, string message, int? httpStatus = null, int? providerErrorCode = null) => new()
        {
            ErrorKind = kind,
            ErrorMessage = message,
            HttpStatus = httpStatus,
            ProviderErrorCode = providerErrorCode,
        };
    }
}
