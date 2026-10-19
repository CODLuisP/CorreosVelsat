using System.Net;
using System.Text;
using System.Text.Json;

namespace CorreosVelsat.Tests
{
    /// <summary>El servicio corre detrás de nginx bajo /correos/ (nginx no quita el prefijo).</summary>
    public class PathBaseTests
    {
        private static ApiFactory CreateFactory() => new() { PathBase = "/correos" };

        [Theory]
        [InlineData("/correos/health")]
        [InlineData("/health")] // el HEALTHCHECK de Docker llama sin prefijo
        public async Task Health_RespondsWithAndWithoutPrefix(string url)
        {
            using var factory = CreateFactory();

            var response = await factory.CreateClient().GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task SendEndpoint_WorksUnderPrefix()
        {
            using var factory = CreateFactory();
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", ApiFactory.ApiKey);

            var response = await client.PostAsync("/correos/api/email/send",
                new StringContent("""{ "to": "a@gmail.com", "subject": "x", "text": "x" }""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task OpenApi_UsesPrefixAsServer_AndSwaggerUsesRelativeDocumentUrl()
        {
            using var factory = CreateFactory();
            var client = factory.CreateClient();

            using var doc = JsonDocument.Parse(await client.GetStringAsync("/correos/openapi/v1.json"));
            Assert.Equal("/correos", doc.RootElement.GetProperty("servers")[0].GetProperty("url").GetString());

            var initializer = await client.GetStringAsync("/correos/swagger/index.js");
            Assert.Contains("../openapi/v1.json", initializer);
        }

        [Fact]
        public async Task Root_RedirectsToSwagger_KeepingPrefix()
        {
            using var factory = CreateFactory();
            var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var response = await client.GetAsync("/correos/");

            Assert.Equal("/correos/swagger", response.Headers.Location?.ToString());
        }
    }
}
