using System.Collections.Concurrent;
using Dapper;
using MySql.Data.MySqlClient;

namespace CorreosVelsat.Email
{
    /// <summary>Contador de destinatarios enviados por proveedor y por día (hora de Lima).</summary>
    public interface IEmailUsageStore
    {
        /// <summary>true si sobrevive a reinicios (MySQL); false en memoria.</summary>
        bool IsPersistent { get; }

        Task<IReadOnlyDictionary<string, int>> GetCountsAsync(DateOnly day, CancellationToken cancellationToken = default);
        Task IncrementAsync(DateOnly day, string provider, int amount, CancellationToken cancellationToken = default);

        /// <summary>Último día en que el proveedor envió algo, o null si nunca.</summary>
        Task<DateOnly?> GetLastSendDayAsync(string provider, CancellationToken cancellationToken = default);
    }

    public static class LimaTime
    {
        public const string TimeZoneId = "America/Lima";

        // Lima no tiene horario de verano (UTC-5 todo el año); se usa como respaldo si falta la base de zonas horarias.
        private static readonly TimeZoneInfo Zone = TryFind() ?? TimeZoneInfo.CreateCustomTimeZone(TimeZoneId, TimeSpan.FromHours(-5), TimeZoneId, TimeZoneId);

        public static DateOnly Today(TimeProvider time) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime);

        private static TimeZoneInfo? TryFind()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
        }
    }

    /// <summary>
    /// Tabla email_provider_usage en la misma base MySQL de la cola. Se crea sola la primera vez
    /// (el usuario de MySQL necesita permiso CREATE; si no, créala a mano con el SQL del README).
    /// </summary>
    public sealed class MySqlEmailUsageStore(string connectionString) : IEmailUsageStore
    {
        public const string CreateTableSql = @"
CREATE TABLE IF NOT EXISTS email_provider_usage (
    fecha       DATE        NOT NULL,
    proveedor   VARCHAR(32) NOT NULL,
    enviados    INT         NOT NULL DEFAULT 0,
    actualizado DATETIME    NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (fecha, proveedor)
)";

        private readonly SemaphoreSlim _initLock = new(1, 1);
        private volatile bool _initialized;

        public bool IsPersistent => true;

        public async Task<IReadOnlyDictionary<string, int>> GetCountsAsync(DateOnly day, CancellationToken cancellationToken = default)
        {
            await using var conn = await OpenAsync(cancellationToken);
            var rows = await conn.QueryAsync<(string Proveedor, int Enviados)>(new CommandDefinition(
                "SELECT proveedor, enviados FROM email_provider_usage WHERE fecha = @Fecha",
                new { Fecha = day.ToDateTime(TimeOnly.MinValue) }, cancellationToken: cancellationToken));
            return rows.ToDictionary(r => r.Proveedor, r => r.Enviados, StringComparer.OrdinalIgnoreCase);
        }

        public async Task IncrementAsync(DateOnly day, string provider, int amount, CancellationToken cancellationToken = default)
        {
            await using var conn = await OpenAsync(cancellationToken);
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO email_provider_usage (fecha, proveedor, enviados) VALUES (@Fecha, @Proveedor, @Cantidad)
                  ON DUPLICATE KEY UPDATE enviados = enviados + @Cantidad",
                new { Fecha = day.ToDateTime(TimeOnly.MinValue), Proveedor = provider, Cantidad = amount }, cancellationToken: cancellationToken));
        }

        public async Task<DateOnly?> GetLastSendDayAsync(string provider, CancellationToken cancellationToken = default)
        {
            await using var conn = await OpenAsync(cancellationToken);
            var last = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MAX(fecha) FROM email_provider_usage WHERE proveedor = @Proveedor AND enviados > 0",
                new { Proveedor = provider }, cancellationToken: cancellationToken));
            return last is { } d ? DateOnly.FromDateTime(d) : null;
        }

        private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            if (!_initialized)
            {
                await _initLock.WaitAsync(cancellationToken);
                try
                {
                    if (!_initialized)
                    {
                        await conn.ExecuteAsync(new CommandDefinition(CreateTableSql, cancellationToken: cancellationToken));
                        _initialized = true;
                    }
                }
                finally
                {
                    _initLock.Release();
                }
            }
            return conn;
        }
    }

    /// <summary>Respaldo sin base de datos (tests o sin ConnectionStrings:MySql): se reinicia con el proceso.</summary>
    public sealed class InMemoryEmailUsageStore : IEmailUsageStore
    {
        private readonly ConcurrentDictionary<(DateOnly Day, string Provider), int> _counts = new();

        public bool IsPersistent => false;

        public Task<IReadOnlyDictionary<string, int>> GetCountsAsync(DateOnly day, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(_counts
                .Where(kv => kv.Key.Day == day)
                .ToDictionary(kv => kv.Key.Provider, kv => kv.Value, StringComparer.OrdinalIgnoreCase));

        public Task IncrementAsync(DateOnly day, string provider, int amount, CancellationToken cancellationToken = default)
        {
            _counts.AddOrUpdate((day, provider), amount, (_, current) => current + amount);
            return Task.CompletedTask;
        }

        public Task<DateOnly?> GetLastSendDayAsync(string provider, CancellationToken cancellationToken = default)
        {
            var days = _counts.Where(kv => kv.Key.Provider == provider && kv.Value > 0).Select(kv => kv.Key.Day).ToList();
            return Task.FromResult<DateOnly?>(days.Count > 0 ? days.Max() : null);
        }
    }
}
