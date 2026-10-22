using CorreosVelsat.Email;

namespace CorreosVelsat.Worker
{
    /// <summary>
    /// Brevo desactiva la clave SMTP tras 90 días sin uso. Si Smtp:KeepAliveTo está definido y Brevo no envió
    /// nada en los últimos 25 días (según email_provider_usage), manda un correo corto de prueba a esa dirección.
    /// Solo corre con el contador en MySQL, para no repetir el envío en cada reinicio.
    /// </summary>
    public sealed class SmtpKeepAliveService(
        SmtpOptions smtp,
        IEmailProvider smtpProvider,
        IEmailUsageStore usage,
        TimeProvider time,
        ILogger<SmtpKeepAliveService> logger) : BackgroundService
    {
        public const int MaxIdleDays = 25;
        public const string Subject = "Prueba mensual respaldo SMTP";
        private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!smtp.Enabled || smtp.KeepAliveTo is null)
                return;
            if (!usage.IsPersistent)
            {
                logger.LogWarning("Smtp:KeepAliveTo está definido pero el contador no está en MySQL (falta ConnectionStrings:MySql); el keep-alive SMTP queda desactivado.");
                return;
            }

            try
            {
                await Task.Delay(InitialDelay, time, stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await RunOnceAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Error en el keep-alive SMTP.");
                    }
                    await Task.Delay(CheckInterval, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>Devuelve true si envió el correo de prueba.</summary>
        internal async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
        {
            DateOnly today = LimaTime.Today(time);
            DateOnly? lastSend = await usage.GetLastSendDayAsync(EmailProviderNames.Brevo, cancellationToken);
            if (lastSend is { } last && today.DayNumber - last.DayNumber < MaxIdleDays)
                return false;

            var result = await smtpProvider.SendAsync(new EmailMessage
            {
                To = [smtp.KeepAliveTo!],
                Subject = Subject,
                Text = $"Correo automático para mantener activa la clave SMTP de Brevo (respaldo de Correos Velsat). Último envío por Brevo: {(lastSend?.ToString("yyyy-MM-dd") ?? "ninguno registrado")}.",
            }, cancellationToken);

            if (!result.Success)
            {
                logger.LogWarning("Keep-alive SMTP falló ({Error}: {Detalle}); se reintenta en la próxima revisión.", result.ErrorKind, result.ErrorMessage);
                return false;
            }

            await usage.IncrementAsync(today, EmailProviderNames.Brevo, 1, cancellationToken);
            logger.LogInformation("Keep-alive SMTP enviado a {To} (último envío por Brevo: {Ultimo}).", smtp.KeepAliveTo, lastSend?.ToString("yyyy-MM-dd") ?? "ninguno");
            return true;
        }
    }
}
