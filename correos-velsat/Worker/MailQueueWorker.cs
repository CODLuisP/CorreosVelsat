using Dapper;
using CorreosVelsat.Email;
using MySql.Data.MySqlClient;

namespace CorreosVelsat.Worker
{
    /// <summary>
    /// Procesa la tabla cola_correos: misma consulta y misma lógica de intentos que el worker original,
    /// pero el envío pasa por <see cref="IEmailService"/>.
    /// </summary>
    public sealed class MailQueueWorker(IEmailService emailService, IConfiguration config, ILogger<MailQueueWorker> logger) : BackgroundService
    {
        private static readonly char[] RecipientSeparators = [',', ';'];

        public static void ValidateConfiguration(IConfiguration config)
        {
            if (config.GetValue("Worker:Enabled", false) && string.IsNullOrWhiteSpace(config.GetConnectionString("MySql")))
                throw new InvalidOperationException("Falta ConnectionStrings:MySql en appsettings.json.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!config.GetValue("Worker:Enabled", false))
            {
                logger.LogInformation("Worker de la cola de correos deshabilitado. Para activarlo: Worker:Enabled=true en appsettings.json.");
                return;
            }

            string connectionString = config.GetConnectionString("MySql")!;
            int intervalSeconds = config.GetValue("Worker:IntervalSeconds", 60);
            int batchSize = config.GetValue("Worker:BatchSize", 10);
            int maxRetries = config.GetValue("Worker:MaxRetries", 5);

            logger.LogInformation("Correos Velsat iniciado (Cloudflare Email Sending). Revisando la cola cada {IntervalSeconds} s.", intervalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Si el lote llegó lleno y todo se envió, puede haber más pendientes: se sigue sin esperar el intervalo.
                    BatchResult batch;
                    do
                    {
                        batch = await ProcessBatchAsync(connectionString, batchSize, maxRetries, stoppingToken);
                    }
                    while (ShouldContinueImmediately(batch, batchSize) && !stoppingToken.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ERROR GENERAL: {Message}", ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        internal readonly record struct BatchResult(int Fetched, int Failed);

        /// <summary>
        /// Solo se encadena otro lote si este vino completo y sin fallos. Si hubo fallos se espera el intervalo,
        /// para no gastar los intentos de un correo en segundos (p. ej. durante una caída de Cloudflare).
        /// </summary>
        internal static bool ShouldContinueImmediately(BatchResult batch, int batchSize) =>
            batch.Fetched >= batchSize && batch.Failed == 0;

        private async Task<BatchResult> ProcessBatchAsync(string connectionString, int batchSize, int maxRetries, CancellationToken stoppingToken)
        {
            int failed = 0;
            using var conn = new MySqlConnection(connectionString);

            var correos = (await conn.QueryAsync<ColaCorreo>(
                @"SELECT id, destinatario, asunto, cuerpo, enviado, intentos
                  FROM cola_correos
                  WHERE enviado = 0 AND intentos < @MaxRetries
                  ORDER BY id
                  LIMIT @BatchSize",
                new { MaxRetries = maxRetries, BatchSize = batchSize })).AsList();

            foreach (var correo in correos)
            {
                var result = await emailService.SendAsync(new EmailMessage
                {
                    To = correo.Destinatario.Split(RecipientSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    Subject = correo.Asunto,
                    Html = correo.Cuerpo,
                }, stoppingToken);

                if (result.Success)
                {
                    await conn.ExecuteAsync(
                        @"UPDATE cola_correos
                          SET enviado = 1, fecha_envio = NOW()
                          WHERE id = @Id",
                        new { correo.Id });
                }
                else
                {
                    failed++;
                    await conn.ExecuteAsync(
                        @"UPDATE cola_correos
                          SET intentos = intentos + 1
                          WHERE id = @Id",
                        new { correo.Id });
                }
            }

            return new BatchResult(correos.Count, failed);
        }
    }

    public class ColaCorreo
    {
        public int Id { get; set; }
        public string Destinatario { get; set; } = "";
        public string Asunto { get; set; } = "";
        public string Cuerpo { get; set; } = "";
        public int Enviado { get; set; }
        public int Intentos { get; set; }
    }
}
