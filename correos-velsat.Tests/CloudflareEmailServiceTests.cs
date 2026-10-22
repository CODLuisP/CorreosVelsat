using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CorreosVelsat.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace CorreosVelsat.Tests
{
    public class CloudflareEmailServiceTests
    {
        private readonly List<TimeSpan> _delays = [];

        private CloudflareEmailService CreateService(FakeHandler handler, EmailOptions? options = null) =>
            new(new HttpClient(handler), options ?? TestOptions.Create(), NullLogger<CloudflareEmailService>.Instance)
            {
                Delay = (delay, _) =>
                {
                    _delays.Add(delay);
                    return Task.CompletedTask;
                },
            };

        private static EmailMessage SimpleMessage(string? from = null) => new()
        {
            To = ["cliente@gmail.com"],
            From = from,
            Subject = "Alerta",
            Html = "<p>Hola <b>mundo</b></p>",
        };

        [Fact]
        public async Task Success_ReturnsMessageId_AndSendsExpectedPayload()
        {
            var handler = new FakeHandler((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, FakeHandler.SuccessJson)));
            var service = CreateService(handler);

            var result = await service.SendAsync(new EmailMessage
            {
                To = ["a@gmail.com", "b@gmail.com"],
                Cc = ["c@gmail.com"],
                ReplyTo = "soporte@velsat.pe",
                Subject = "Asunto",
                Html = "<p>Hola</p>",
            });

            Assert.True(result.Success);
            Assert.Equal("msg-123", result.MessageId);
            Assert.Equal(1, result.Attempts);

            var (request, body) = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.cloudflare.com/client/v4/accounts/test-account/email/sending/send", request.RequestUri!.ToString());
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-token"), request.Headers.Authorization);

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal("alertas@velsat.pe", root.GetProperty("from").GetProperty("address").GetString());
            Assert.Equal("Velsat Alertas", root.GetProperty("from").GetProperty("name").GetString());
            Assert.Equal(2, root.GetProperty("to").GetArrayLength());
            Assert.Equal("c@gmail.com", root.GetProperty("cc")[0].GetString());
            Assert.Equal("soporte@velsat.pe", root.GetProperty("reply_to").GetString());
            Assert.False(root.TryGetProperty("bcc", out _));
            Assert.Equal("<p>Hola</p>", root.GetProperty("html").GetString());
            Assert.Equal("Hola", root.GetProperty("text").GetString());
        }

        [Fact]
        public async Task CustomFromWithoutName_IsSentAsPlainString()
        {
            var handler = new FakeHandler((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, FakeHandler.SuccessJson)));

            await CreateService(handler).SendAsync(SimpleMessage(from: "ventas@velsat.pe"));

            using var json = JsonDocument.Parse(handler.Requests[0].Body);
            Assert.Equal("ventas@velsat.pe", json.RootElement.GetProperty("from").GetString());
        }

        [Theory]
        [InlineData(HttpStatusCode.BadRequest, 10001, EmailErrorKind.Rejected)]
        [InlineData(HttpStatusCode.Unauthorized, 10101, EmailErrorKind.Authentication)]
        [InlineData(HttpStatusCode.Forbidden, 10102, EmailErrorKind.Authentication)]
        public async Task ClientErrors_AreNotRetried(HttpStatusCode status, int code, EmailErrorKind expectedKind)
        {
            var handler = new FakeHandler((_, _, _) => Task.FromResult(FakeHandler.Json(status, FakeHandler.ErrorJson(code, "error"))));

            var result = await CreateService(handler).SendAsync(SimpleMessage());

            Assert.False(result.Success);
            Assert.Equal(expectedKind, result.ErrorKind);
            Assert.Equal(code, result.ProviderErrorCode);
            Assert.Single(handler.Requests);
            Assert.Empty(_delays);
        }

        [Fact]
        public async Task TooManyRequests_IsNotRetried_ReturnsRateLimitedWithRetryAfter()
        {
            var handler = new FakeHandler((_, _, _) =>
            {
                var throttled = FakeHandler.Json(HttpStatusCode.TooManyRequests, FakeHandler.ErrorJson(10004, "email.sending.error.throttled"));
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
                return Task.FromResult(throttled);
            });

            var result = await CreateService(handler).SendAsync(SimpleMessage());

            // El router pasa al otro proveedor: no se gastan reintentos en Cloudflare.
            Assert.False(result.Success);
            Assert.Equal(EmailErrorKind.RateLimited, result.ErrorKind);
            Assert.Equal(TimeSpan.FromMinutes(30), result.RetryAfter);
            Assert.Single(handler.Requests);
            Assert.Empty(_delays);
        }

        [Fact]
        public async Task ThrottledErrorCode_IsRateLimited_EvenWithoutHttp429()
        {
            var handler = new FakeHandler((_, _, _) =>
                Task.FromResult(FakeHandler.Json(HttpStatusCode.BadRequest, FakeHandler.ErrorJson(10004, "email.sending.error.throttled"))));

            var result = await CreateService(handler).SendAsync(SimpleMessage());

            Assert.Equal(EmailErrorKind.RateLimited, result.ErrorKind);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task ServerErrors_AreRetriedWithBackoff_UntilMaxRetries()
        {
            var handler = new FakeHandler((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.ServiceUnavailable, FakeHandler.ErrorJson(10100, "upstream"))));

            var result = await CreateService(handler, TestOptions.Create(maxRetries: 3)).SendAsync(SimpleMessage());

            Assert.False(result.Success);
            Assert.Equal(EmailErrorKind.ProviderError, result.ErrorKind);
            Assert.Equal(4, handler.Requests.Count);
            Assert.Equal(4, result.Attempts);
            Assert.Equal(3, _delays.Count);
            // Backoff exponencial con jitter (50%-100%): base 100 ms → 50-100, 100-200, 200-400.
            Assert.InRange(_delays[0].TotalMilliseconds, 50, 100);
            Assert.InRange(_delays[1].TotalMilliseconds, 100, 200);
            Assert.InRange(_delays[2].TotalMilliseconds, 200, 400);
        }

        [Fact]
        public async Task NonJsonServerError_IsHandledAsProviderError()
        {
            var handler = new FakeHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html>bad gateway</html>"),
            }));

            var result = await CreateService(handler, TestOptions.Create(maxRetries: 0)).SendAsync(SimpleMessage());

            Assert.Equal(EmailErrorKind.ProviderError, result.ErrorKind);
            Assert.Equal(502, result.HttpStatus);
        }

        [Fact]
        public async Task Timeout_IsRetried_AndReportedAsTimeout()
        {
            var handler = new FakeHandler(async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("no debería llegar aquí");
            });
            var options = TestOptions.Create(maxRetries: 1, timeout: TimeSpan.FromMilliseconds(100));

            var result = await CreateService(handler, options).SendAsync(SimpleMessage());

            Assert.False(result.Success);
            Assert.Equal(EmailErrorKind.Timeout, result.ErrorKind);
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public async Task NetworkError_IsRetried_ThenSucceeds()
        {
            var handler = new FakeHandler((_, call, _) => call == 1
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, FakeHandler.SuccessJson)));

            var result = await CreateService(handler).SendAsync(SimpleMessage());

            Assert.True(result.Success);
            Assert.Equal(2, result.Attempts);
        }

        [Theory]
        [InlineData("ventas@gmail.com")]
        [InlineData("x@velsat.pe.evil.com")]
        [InlineData("x@evilvelsat.pe")]
        [InlineData("x@sub.velsat.pe")]
        public async Task FromOutsideAllowedDomains_IsRejectedWithoutCallingCloudflare(string from)
        {
            var handler = new FakeHandler((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, FakeHandler.SuccessJson)));

            var result = await CreateService(handler).SendAsync(SimpleMessage(from));

            Assert.False(result.Success);
            Assert.Equal(EmailErrorKind.Validation, result.ErrorKind);
            Assert.Contains(result.ValidationErrors, e => e.Contains("no está permitido"));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task FromAllowedDomain_IsCaseInsensitive()
        {
            var handler = new FakeHandler((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, FakeHandler.SuccessJson)));

            var result = await CreateService(handler).SendAsync(SimpleMessage("Facturas@VELSAT.PE"));

            Assert.True(result.Success);
        }

        [Fact]
        public async Task TooManyRecipients_IsRejected()
        {
            var handler = new FakeHandler((_, _, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, FakeHandler.SuccessJson)));
            var message = SimpleMessage() with
            {
                To = Enumerable.Range(0, 40).Select(i => $"u{i}@gmail.com").ToArray(),
                Bcc = Enumerable.Range(0, 11).Select(i => $"b{i}@gmail.com").ToArray(),
            };

            var result = await CreateService(handler).SendAsync(message);

            Assert.Equal(EmailErrorKind.Validation, result.ErrorKind);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void HtmlToText_ProducesReadableText()
        {
            string text = HtmlToText.Convert(
                "<style>p{}</style><h1>Alerta</h1><p>Unidad&nbsp;ABC-123 &amp; más</p><ul><li>Uno</li><li>Dos</li></ul><a href=\"https://velsat.pe\">Ver</a>");

            Assert.Equal("Alerta\nUnidad ABC-123 & más\n\n- Uno\n- Dos\nVer (https://velsat.pe)", text);
        }
    }
}
