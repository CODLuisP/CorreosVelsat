using System.Collections.Concurrent;

namespace CorreosVelsat.Email
{
    public sealed record ProviderLimit(IEmailProvider Provider, int DailyLimit);

    /// <summary>Estado de un proveedor hoy.</summary>
    /// <param name="Name">Cloudflare o Brevo.</param>
    /// <param name="Sent">Destinatarios enviados hoy (to + cc + bcc).</param>
    /// <param name="DailyLimit">Límite diario antes de rotar al siguiente proveedor.</param>
    /// <param name="Blocked">true si respondió con límite/cuota y está en pausa.</param>
    /// <param name="BlockedUntil">Hasta cuándo está en pausa (UTC).</param>
    public sealed record ProviderUsage(string Name, int Sent, int DailyLimit, bool Blocked, DateTimeOffset? BlockedUntil);

    /// <summary>Cupo diario de los proveedores de correo.</summary>
    /// <param name="Date">Día contabilizado, en hora de Lima.</param>
    /// <param name="TimeZone">Zona horaria del contador.</param>
    /// <param name="Providers">Proveedores en orden de prioridad.</param>
    public sealed record EmailUsageReport(DateOnly Date, string TimeZone, IReadOnlyList<ProviderUsage> Providers);

    /// <summary>
    /// Implementación de <see cref="IEmailService"/> que usa todo el backend (worker y endpoint).
    /// Elige proveedor por cupo diario (destinatarios por día en hora de Lima, persistido en MySQL):
    ///   1. El primero, en orden (Cloudflare, Brevo), que esté bajo su límite y no bloqueado.
    ///   2. Si todos alcanzaron su cupo: Cloudflare igual (puede tener cupo extra pagado), si no está bloqueado.
    /// Un RateLimited bloquea al proveedor 15 min (o el Retry-After si es mayor) y se prueba el siguiente
    /// en el mismo correo. Un 5xx, timeout, error de red o de autenticación también pasa al siguiente.
    /// Si no queda ninguno, devuelve RateLimited y el worker deja el correo pendiente sin sumar intentos.
    /// </summary>
    public sealed class RoutingEmailService : IEmailService
    {
        public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);

        private readonly IReadOnlyList<ProviderLimit> _providers;
        private readonly IEmailUsageStore _usage;
        private readonly EmailOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<RoutingEmailService> _logger;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.OrdinalIgnoreCase);

        public RoutingEmailService(
            IReadOnlyList<ProviderLimit> providers,
            IEmailUsageStore usage,
            EmailOptions options,
            TimeProvider time,
            ILogger<RoutingEmailService> logger)
        {
            if (providers.Count == 0)
                throw new ArgumentException("Se necesita al menos un proveedor.", nameof(providers));
            _providers = providers;
            _usage = usage;
            _options = options;
            _time = time;
            _logger = logger;
        }

        /// <summary>Proveedores en orden de prioridad con su límite diario.</summary>
        public IReadOnlyList<ProviderLimit> Providers => _providers;

        public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var resolved = EmailValidator.ApplyDefaults(message, _options);
            var errors = EmailValidator.Validate(resolved, _options);
            if (errors.Count > 0)
                return EmailSendResult.Invalid(errors);

            DateOnly today = LimaTime.Today(_time);
            var counts = await GetCountsSafeAsync(today, cancellationToken);
            var candidates = ChooseCandidates(counts);

            if (candidates.Count == 0)
            {
                _logger.LogWarning("Correo pendiente: ningún proveedor disponible (cupo agotado o bloqueados). To={To} {Contadores}",
                    string.Join(", ", resolved.To), Describe(counts));
                return EmailSendResult.Fail(EmailErrorKind.RateLimited, "Ningún proveedor de correo disponible (cupo diario agotado o bloqueados).");
            }

            EmailSendResult? lastRateLimited = null;
            EmailSendResult? lastFailure = null;
            foreach (var candidate in candidates)
            {
                var provider = candidate.Provider;
                var result = await provider.SendAsync(resolved, cancellationToken) with { Provider = provider.Name };

                if (result.Success)
                {
                    int recipients = resolved.To.Count + resolved.Cc.Count + resolved.Bcc.Count;
                    await IncrementSafeAsync(today, provider.Name, recipients, cancellationToken);
                    var updated = new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
                    updated[provider.Name] = updated.GetValueOrDefault(provider.Name) + recipients;
                    _logger.LogInformation("Correo enviado. Proveedor={Proveedor} MessageId={MessageId} Hoy: {Contadores}",
                        provider.Name, result.MessageId, Describe(updated));
                    return result;
                }

                switch (result.ErrorKind)
                {
                    case EmailErrorKind.RateLimited:
                        Block(provider.Name, result.RetryAfter);
                        lastRateLimited = result;
                        continue;
                    case EmailErrorKind.ProviderError or EmailErrorKind.Timeout or EmailErrorKind.Network
                        or EmailErrorKind.Authentication or EmailErrorKind.Unexpected:
                        _logger.LogWarning("Proveedor={Proveedor} falló ({Error}: {Detalle}); se prueba el siguiente.",
                            provider.Name, result.ErrorKind, result.ErrorMessage);
                        lastFailure = result;
                        continue;
                    default:
                        // Rechazo permanente o validación: otro proveedor no lo arreglaría.
                        return result;
                }
            }

            // Un error real (5xx, timeout...) cuenta como fallo; si solo hubo límites, el correo queda pendiente.
            return lastFailure ?? lastRateLimited!;
        }

        public async Task<EmailUsageReport> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            DateOnly today = LimaTime.Today(_time);
            var counts = await GetCountsSafeAsync(today, cancellationToken);
            var providers = _providers.Select(p =>
            {
                var until = BlockedUntil(p.Provider.Name);
                return new ProviderUsage(p.Provider.Name, counts.GetValueOrDefault(p.Provider.Name), p.DailyLimit, until is not null, until);
            }).ToList();
            return new EmailUsageReport(today, LimaTime.TimeZoneId, providers);
        }

        internal IReadOnlyList<ProviderLimit> ChooseCandidates(IReadOnlyDictionary<string, int> counts)
        {
            var available = _providers.Where(p => !IsBlocked(p.Provider.Name)).ToList();
            var underLimit = available.Where(p => counts.GetValueOrDefault(p.Provider.Name) < p.DailyLimit).ToList();
            if (underLimit.Count > 0)
                return underLimit;

            // Todos con el cupo del día agotado: Cloudflare igual (puede tener cupo extra pagado).
            var cloudflare = available.FirstOrDefault(p => p.Provider.Name == EmailProviderNames.Cloudflare);
            return cloudflare is null ? [] : [cloudflare];
        }

        private void Block(string provider, TimeSpan? retryAfter)
        {
            var duration = retryAfter is { } ra && ra > BlockDuration ? ra : BlockDuration;
            var until = _time.GetUtcNow() + duration;
            _blockedUntil[provider] = until;
            _logger.LogWarning("Proveedor={Proveedor} sin cupo/limitado: bloqueado hasta {Hasta:HH:mm} UTC.", provider, until);
        }

        private bool IsBlocked(string provider) => BlockedUntil(provider) is not null;

        private DateTimeOffset? BlockedUntil(string provider) =>
            _blockedUntil.TryGetValue(provider, out var until) && until > _time.GetUtcNow() ? until : null;

        private string Describe(IReadOnlyDictionary<string, int> counts) =>
            string.Join(", ", _providers.Select(p =>
                $"{p.Provider.Name}={counts.GetValueOrDefault(p.Provider.Name)}/{p.DailyLimit}{(IsBlocked(p.Provider.Name) ? " (bloqueado)" : "")}"));

        // Si MySQL falla, se envía igual: mejor gastar algo de cupo que dejar de enviar alertas.
        private async Task<IReadOnlyDictionary<string, int>> GetCountsSafeAsync(DateOnly day, CancellationToken cancellationToken)
        {
            try
            {
                return await _usage.GetCountsAsync(day, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "No se pudo leer el contador diario de proveedores; se asume 0.");
                return new Dictionary<string, int>();
            }
        }

        private async Task IncrementSafeAsync(DateOnly day, string provider, int amount, CancellationToken cancellationToken)
        {
            try
            {
                await _usage.IncrementAsync(day, provider, amount, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "No se pudo actualizar el contador diario de {Proveedor}.", provider);
            }
        }
    }
}
