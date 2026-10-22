using CorreosVelsat.Email;
using CorreosVelsat.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace CorreosVelsat.Tests
{
    public class SmtpKeepAliveServiceTests
    {
        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero));
        private readonly InMemoryEmailUsageStore _store = new();
        private readonly FakeProvider _brevo = new(EmailProviderNames.Brevo);
        private static readonly DateOnly Today = new(2026, 10, 22);

        private SmtpKeepAliveService Create() => new(
            new SmtpOptions { Enabled = true, User = "u", Password = "p", KeepAliveTo = "respaldo@velsat.pe" },
            _brevo, _store, _time, NullLogger<SmtpKeepAliveService>.Instance);

        [Fact]
        public async Task SendsTestEmail_WhenBrevoIdleFor25DaysOrMore_OnlyOnce()
        {
            await _store.IncrementAsync(Today.AddDays(-25), EmailProviderNames.Brevo, 3);
            EmailMessage? sent = null;
            _brevo.Respond = m => { sent = m; return new EmailSendResult { Success = true }; };
            var service = Create();

            Assert.True(await service.RunOnceAsync(default));
            Assert.Equal(["respaldo@velsat.pe"], sent!.To);
            Assert.Equal("Prueba mensual respaldo SMTP", sent.Subject);

            // Queda registrado hoy: la siguiente revisión no vuelve a enviar.
            Assert.False(await service.RunOnceAsync(default));
            Assert.Equal(1, _brevo.Calls);
        }

        [Fact]
        public async Task DoesNotSend_WhenBrevoSentRecently()
        {
            await _store.IncrementAsync(Today.AddDays(-10), EmailProviderNames.Brevo, 1);

            Assert.False(await Create().RunOnceAsync(default));
            Assert.Equal(0, _brevo.Calls);
        }

        [Fact]
        public async Task Sends_WhenBrevoNeverSent()
        {
            Assert.True(await Create().RunOnceAsync(default));
            Assert.Equal(1, _brevo.Calls);
        }
    }
}
