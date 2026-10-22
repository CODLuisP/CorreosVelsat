using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CorreosVelsat.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CorreosVelsat.Tests
{
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string ApiKey = "test-api-key-0123456789abcdef";

        public FakeEmailService Email { get; } = new();
        public InMemoryEmailUsageStore UsageStore { get; } = new();
        public int RateLimitPerMinute { get; init; } = 1000;
        public string Environment { get; init; } = "Testing";
        public bool SwaggerEnabled { get; init; } = true;
        public string? PathBase { get; init; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cloudflare:AccountId"] = "test-account",
                ["Cloudflare:ApiToken"] = "test-token",
                ["Email:DefaultFrom"] = "alertas@velsat.pe",
                ["Email:AllowedFromDomains:0"] = "velsat.pe",
                ["Api:ApiKey"] = ApiKey,
                ["Api:RateLimitPerMinute"] = RateLimitPerMinute.ToString(),
                ["Worker:Enabled"] = "false",
                ["Api:SwaggerEnabled"] = SwaggerEnabled.ToString(),
                ["PathBase"] = PathBase,
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailService>();
                services.AddSingleton<IEmailService>(Email);
                // Nunca tocar la base real (el appsettings.json local puede tener la conexión de producción).
                services.RemoveAll<IEmailUsageStore>();
                services.AddSingleton<IEmailUsageStore>(UsageStore);
            });
        }
    }

    public class EmailEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
    {
        private const string Route = "/api/email/send";

        private HttpClient CreateClient(string? apiKey = ApiFactory.ApiKey)
        {
            var client = factory.CreateClient();
            if (apiKey is not null)
                client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            return client;
        }

        private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

        private const string ValidBody = """
            { "from": "ventas@velsat.pe", "fromName": "Velsat Ventas", "to": "cliente@gmail.com",
              "cc": ["copia@gmail.com"], "replyTo": "soporte@velsat.pe", "subject": "Hola", "html": "<p>Hola</p>" }
            """;

        [Fact]
        public async Task MissingApiKey_Returns401()
        {
            var response = await CreateClient(apiKey: null).PostAsync(Route, JsonBody(ValidBody));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task WrongApiKey_Returns401_EvenWithInvalidBody()
        {
            var response = await CreateClient(apiKey: "otra-clave-que-no-es-la-correcta").PostAsync(Route, JsonBody("{ no es json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task ValidRequest_Returns200_WithMessageId()
        {
            factory.Email.NextResult = new EmailSendResult { Success = true, MessageId = "msg-abc", Attempts = 1 };
            factory.Email.Sent.Clear();

            var response = await CreateClient().PostAsync(Route, JsonBody(ValidBody));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal("msg-abc", json.GetProperty("messageId").GetString());

            var sent = Assert.Single(factory.Email.Sent);
            Assert.Equal(["cliente@gmail.com"], sent.To);
            Assert.Equal(["copia@gmail.com"], sent.Cc);
            Assert.Equal("ventas@velsat.pe", sent.From);
            Assert.Equal("Velsat Ventas", sent.FromName);
            Assert.Equal("soporte@velsat.pe", sent.ReplyTo);
        }

        [Theory]
        [InlineData("""{ "from": "ceo@gmail.com", "to": "a@gmail.com", "subject": "x", "text": "x" }""", "no está permitido")]
        [InlineData("""{ "to": "a@gmail.com", "text": "x" }""", "asunto")]
        [InlineData("""{ "to": "a@gmail.com", "subject": "x" }""", "'html' o 'text'")]
        [InlineData("""{ "to": "no-es-un-email", "subject": "x", "text": "x" }""", "Email inválido")]
        [InlineData("""{ "to": [], "subject": "x", "text": "x" }""", "al menos un destinatario")]
        [InlineData("""{ "to": 123, "subject": "x", "text": "x" }""", "JSON válido")]
        [InlineData("""{ no es json""", "JSON válido")]
        public async Task InvalidRequests_Return400(string body, string expectedError)
        {
            factory.Email.Sent.Clear();

            var response = await CreateClient().PostAsync(Route, JsonBody(body));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            string content = await response.Content.ReadAsStringAsync();
            Assert.Contains(expectedError, content);
            Assert.Empty(factory.Email.Sent);
        }

        [Fact]
        public async Task ProviderFailure_Returns502_WithoutInternalDetails()
        {
            factory.Email.NextResult = EmailSendResult.Fail(EmailErrorKind.Authentication, "10101: detalle-interno-secreto", 401, 10101);

            var response = await CreateClient().PostAsync(Route, JsonBody(ValidBody));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            string content = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("detalle-interno-secreto", content);
            Assert.DoesNotContain("10101", content);
        }

        [Fact]
        public async Task Usage_RequiresApiKey()
        {
            var response = await CreateClient(apiKey: null).GetAsync("/api/email/usage");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Usage_ReturnsTodaysCountersAndLimits()
        {
            var today = LimaTime.Today(TimeProvider.System);
            await factory.UsageStore.IncrementAsync(today, EmailProviderNames.Cloudflare, 7);

            var response = await CreateClient().GetAsync("/api/email/usage");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(today.ToString("yyyy-MM-dd"), json.GetProperty("date").GetString());
            Assert.Equal("America/Lima", json.GetProperty("timeZone").GetString());
            var cloudflare = json.GetProperty("providers")[0];
            Assert.Equal("Cloudflare", cloudflare.GetProperty("name").GetString());
            Assert.True(cloudflare.GetProperty("sent").GetInt32() >= 7);
            Assert.Equal(100, cloudflare.GetProperty("dailyLimit").GetInt32());
            Assert.False(cloudflare.GetProperty("blocked").GetBoolean());
        }
    }

    public class EmailEndpointRateLimitTests
    {
        [Fact]
        public async Task ExceedingRateLimit_Returns429()
        {
            using var factory = new ApiFactory { RateLimitPerMinute = 2 };
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", ApiFactory.ApiKey);
            const string body = """{ "to": "a@gmail.com", "subject": "x", "text": "x" }""";

            var statuses = new List<HttpStatusCode>();
            for (int i = 0; i < 3; i++)
            {
                var response = await client.PostAsync("/api/email/send", new StringContent(body, Encoding.UTF8, "application/json"));
                statuses.Add(response.StatusCode);
            }

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        }
    }
}
