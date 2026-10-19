namespace CorreosVelsat.Email
{
    /// <summary>
    /// Configuración del servicio de email. Se lee de las secciones Cloudflare y Email de appsettings.json.
    /// </summary>
    public sealed class EmailOptions
    {
        // Límites documentados de Cloudflare Email Sending:
        // https://developers.cloudflare.com/email-service/platform/limits/
        public const int MaxRecipients = 50;
        public const int MaxSubjectLength = 998;
        public const long MaxMessageBytes = 5 * 1024 * 1024;

        public required string AccountId { get; init; }
        public required string ApiToken { get; init; }
        public required string DefaultFrom { get; init; }
        public string? DefaultFromName { get; init; }
        public required IReadOnlyList<string> AllowedFromDomains { get; init; }
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
        public int MaxRetries { get; init; } = 3;
        public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

        public static EmailOptions FromConfiguration(IConfiguration config)
        {
            var missing = new List<string>();
            string Required(string key)
            {
                var value = config[key]?.Trim();
                if (string.IsNullOrEmpty(value))
                    missing.Add(key);
                return value ?? "";
            }

            string accountId = Required("Cloudflare:AccountId");
            string apiToken = Required("Cloudflare:ApiToken");
            if (missing.Count > 0)
                throw new InvalidOperationException($"Faltan valores obligatorios en appsettings.json: {string.Join(", ", missing)}");

            // "AllowedFromDomains": ["velsat.pe", "otro.pe"]  (también acepta "velsat.pe,otro.pe")
            var domainsSection = config.GetSection("Email:AllowedFromDomains");
            var allowedDomains = (domainsSection.Get<string[]>() ?? (domainsSection.Value ?? "velsat.pe").Split(','))
                .Select(d => d.Trim().ToLowerInvariant())
                .Where(d => d.Length > 0)
                .Distinct()
                .ToArray();
            if (allowedDomains.Length == 0)
                throw new InvalidOperationException("Email:AllowedFromDomains no contiene ningún dominio.");

            string defaultFrom = config["Email:DefaultFrom"]?.Trim() is { Length: > 0 } from ? from : "alertas@velsat.pe";
            if (!EmailValidator.IsValidAddress(defaultFrom))
                throw new InvalidOperationException($"Email:DefaultFrom no es un email válido: {defaultFrom}");
            if (!EmailValidator.IsAllowedFrom(defaultFrom, allowedDomains))
                throw new InvalidOperationException($"Email:DefaultFrom ({defaultFrom}) no pertenece a Email:AllowedFromDomains.");

            return new EmailOptions
            {
                AccountId = accountId,
                ApiToken = apiToken,
                DefaultFrom = defaultFrom,
                DefaultFromName = config["Email:DefaultFromName"]?.Trim() is { Length: > 0 } name ? name : "Velsat Alertas",
                AllowedFromDomains = allowedDomains,
                Timeout = TimeSpan.FromMilliseconds(ConfigInt.Read(config, "Email:TimeoutMs", 15000, min: 1000, max: 120000)),
                MaxRetries = ConfigInt.Read(config, "Email:MaxRetries", 3, min: 0, max: 10),
            };
        }
    }

    /// <summary>
    /// Configuración del endpoint POST /api/email/send.
    /// </summary>
    public sealed class EmailEndpointOptions
    {
        public const int MinApiKeyLength = 24;

        public required string ApiKey { get; init; }
        public int RateLimitPerMinute { get; init; } = 30;
        public int RateLimitPerDay { get; init; } = 2000;

        public static EmailEndpointOptions FromConfiguration(IConfiguration config)
        {
            string apiKey = config["Api:ApiKey"]?.Trim() ?? "";
            if (apiKey.Length == 0)
                throw new InvalidOperationException("Falta el valor obligatorio en appsettings.json: Api:ApiKey");
            if (apiKey.Length < MinApiKeyLength)
                throw new InvalidOperationException($"Api:ApiKey debe tener al menos {MinApiKeyLength} caracteres.");

            return new EmailEndpointOptions
            {
                ApiKey = apiKey,
                RateLimitPerMinute = ConfigInt.Read(config, "Api:RateLimitPerMinute", 30, min: 1, max: 10000),
                RateLimitPerDay = ConfigInt.Read(config, "Api:RateLimitPerDay", 2000, min: 1, max: 1000000),
            };
        }
    }

    internal static class ConfigInt
    {
        public static int Read(IConfiguration config, string key, int defaultValue, int min, int max)
        {
            var raw = config[key]?.Trim();
            if (string.IsNullOrEmpty(raw))
                return defaultValue;
            if (!int.TryParse(raw, out int value) || value < min || value > max)
                throw new InvalidOperationException($"{key} debe ser un entero entre {min} y {max} (valor actual: '{raw}').");
            return value;
        }
    }
}
