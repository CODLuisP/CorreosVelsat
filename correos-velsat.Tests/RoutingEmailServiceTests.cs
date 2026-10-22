using CorreosVelsat.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace CorreosVelsat.Tests
{
    public class RoutingEmailServiceTests
    {
        // 15:00 en Lima (UTC-5)
        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero));
        private readonly InMemoryEmailUsageStore _store = new();
        private readonly FakeProvider _cloudflare = new(EmailProviderNames.Cloudflare);
        private readonly FakeProvider _brevo = new(EmailProviderNames.Brevo);
        private static readonly DateOnly Today = new(2026, 10, 22);

        private RoutingEmailService CreateRouter(int cloudflareLimit = 100, int brevoLimit = 300) => new(
            [new ProviderLimit(_cloudflare, cloudflareLimit), new ProviderLimit(_brevo, brevoLimit)],
            _store, TestOptions.Create(), _time, NullLogger<RoutingEmailService>.Instance);

        private static EmailMessage Message(int recipients = 1) => new()
        {
            To = Enumerable.Range(0, recipients).Select(i => $"u{i}@gmail.com").ToArray(),
            Subject = "Alerta",
            Html = "<p>Hola</p>",
        };

        private async Task<int> Count(string provider, DateOnly? day = null) =>
            (await _store.GetCountsAsync(day ?? Today)).GetValueOrDefault(provider);

        [Fact]
        public async Task UsesCloudflare_UntilDailyLimit_ThenRotatesToBrevo()
        {
            var router = CreateRouter(cloudflareLimit: 2);

            var results = new List<string?>();
            for (int i = 0; i < 3; i++)
                results.Add((await router.SendAsync(Message())).Provider);

            Assert.Equal([EmailProviderNames.Cloudflare, EmailProviderNames.Cloudflare, EmailProviderNames.Brevo], results);
            Assert.Equal(2, await Count(EmailProviderNames.Cloudflare));
            Assert.Equal(1, await Count(EmailProviderNames.Brevo));
        }

        [Fact]
        public async Task RateLimitedCloudflare_UsesBrevoInSameSend_AndIsBlockedFor15Minutes()
        {
            var router = CreateRouter();
            _cloudflare.Respond = _ => FakeProvider.Fail(EmailErrorKind.RateLimited);

            var first = await router.SendAsync(Message());

            Assert.True(first.Success);
            Assert.Equal(EmailProviderNames.Brevo, first.Provider);
            Assert.Equal(1, _cloudflare.Calls);

            // Bloqueado: el siguiente correo va directo a Brevo sin llamar a Cloudflare.
            _cloudflare.Respond = _ => new EmailSendResult { Success = true, MessageId = "cf" };
            await router.SendAsync(Message());
            Assert.Equal(1, _cloudflare.Calls);
            Assert.True((await router.GetUsageAsync()).Providers.Single(p => p.Name == EmailProviderNames.Cloudflare).Blocked);

            // Pasados 15 minutos vuelve a Cloudflare.
            _time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
            var later = await router.SendAsync(Message());
            Assert.Equal(EmailProviderNames.Cloudflare, later.Provider);
            Assert.Equal(2, _cloudflare.Calls);
        }

        [Fact]
        public async Task RetryAfterLongerThan15Minutes_ExtendsTheBlock()
        {
            var router = CreateRouter();
            _cloudflare.Respond = _ => FakeProvider.Fail(EmailErrorKind.RateLimited) with { RetryAfter = TimeSpan.FromHours(2) };

            await router.SendAsync(Message());
            _time.Advance(TimeSpan.FromMinutes(30));

            Assert.True((await router.GetUsageAsync()).Providers.Single(p => p.Name == EmailProviderNames.Cloudflare).Blocked);
        }

        [Theory]
        [InlineData(EmailErrorKind.ProviderError)]
        [InlineData(EmailErrorKind.Timeout)]
        [InlineData(EmailErrorKind.Network)]
        public async Task TransientFailureOnCloudflare_UsesBrevo_WithoutBlocking(EmailErrorKind kind)
        {
            var router = CreateRouter();
            _cloudflare.Respond = _ => FakeProvider.Fail(kind);

            var result = await router.SendAsync(Message());

            Assert.True(result.Success);
            Assert.Equal(EmailProviderNames.Brevo, result.Provider);
            Assert.False((await router.GetUsageAsync()).Providers.Single(p => p.Name == EmailProviderNames.Cloudflare).Blocked);
        }

        [Fact]
        public async Task BothOverQuota_TriesCloudflareAnyway_AndLeavesPendingIfThrottled()
        {
            await _store.IncrementAsync(Today, EmailProviderNames.Cloudflare, 100);
            await _store.IncrementAsync(Today, EmailProviderNames.Brevo, 300);
            var router = CreateRouter();
            _cloudflare.Respond = _ => FakeProvider.Fail(EmailErrorKind.RateLimited);

            var result = await router.SendAsync(Message());

            Assert.False(result.Success);
            Assert.Equal(EmailErrorKind.RateLimited, result.ErrorKind);
            Assert.Equal(1, _cloudflare.Calls);
            Assert.Equal(0, _brevo.Calls);
        }

        [Fact]
        public async Task BothOverQuota_CloudflareWithPaidExtra_StillSends()
        {
            await _store.IncrementAsync(Today, EmailProviderNames.Cloudflare, 100);
            await _store.IncrementAsync(Today, EmailProviderNames.Brevo, 300);
            var router = CreateRouter();

            var result = await router.SendAsync(Message());

            Assert.True(result.Success);
            Assert.Equal(EmailProviderNames.Cloudflare, result.Provider);
            Assert.Equal(101, await Count(EmailProviderNames.Cloudflare));
        }

        [Fact]
        public async Task CloudflareBlocked_AndBrevoOverQuota_NoProviderIsCalled()
        {
            await _store.IncrementAsync(Today, EmailProviderNames.Brevo, 300);
            var router = CreateRouter();
            _cloudflare.Respond = _ => FakeProvider.Fail(EmailErrorKind.RateLimited);
            await router.SendAsync(Message()); // bloquea Cloudflare

            var result = await router.SendAsync(Message());

            Assert.Equal(EmailErrorKind.RateLimited, result.ErrorKind);
            Assert.Equal(1, _cloudflare.Calls);
            Assert.Equal(0, _brevo.Calls);
        }

        [Fact]
        public async Task PermanentRejection_DoesNotTryTheOtherProvider()
        {
            var router = CreateRouter();
            _cloudflare.Respond = _ => FakeProvider.Fail(EmailErrorKind.Rejected);

            var result = await router.SendAsync(Message());

            Assert.Equal(EmailErrorKind.Rejected, result.ErrorKind);
            Assert.Equal(0, _brevo.Calls);
        }

        [Fact]
        public async Task CountsRecipients_PerDayInLimaTime()
        {
            var router = CreateRouter();

            // 2026-10-22 04:30 UTC = 2026-10-21 23:30 en Lima
            _time.Set(new DateTimeOffset(2026, 10, 22, 4, 30, 0, TimeSpan.Zero));
            await router.SendAsync(Message(recipients: 2) with { Cc = ["copia@gmail.com"] });

            // 2026-10-22 05:30 UTC = 2026-10-22 00:30 en Lima: nuevo día, contador en cero
            _time.Set(new DateTimeOffset(2026, 10, 22, 5, 30, 0, TimeSpan.Zero));
            var usage = await router.GetUsageAsync();
            await router.SendAsync(Message());

            Assert.Equal(3, await Count(EmailProviderNames.Cloudflare, new DateOnly(2026, 10, 21)));
            Assert.Equal(new DateOnly(2026, 10, 22), usage.Date);
            Assert.Equal(0, usage.Providers[0].Sent);
            Assert.Equal(1, await Count(EmailProviderNames.Cloudflare, new DateOnly(2026, 10, 22)));
        }

        [Fact]
        public async Task InvalidMessage_IsRejectedWithoutCallingProviders()
        {
            var router = CreateRouter();

            var result = await router.SendAsync(Message() with { From = "ceo@gmail.com" });

            Assert.Equal(EmailErrorKind.Validation, result.ErrorKind);
            Assert.Equal(0, _cloudflare.Calls + _brevo.Calls);
        }
    }
}
