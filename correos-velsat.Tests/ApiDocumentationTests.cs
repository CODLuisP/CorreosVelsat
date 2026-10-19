using System.Net;
using System.Text.Json;

namespace CorreosVelsat.Tests
{
    public class ApiDocumentationTests
    {
        [Fact]
        public async Task OpenApiDocument_DescribesSendEndpoint_WithApiKeySecurity()
        {
            using var factory = new ApiFactory();
            var client = factory.CreateClient();

            var response = await client.GetAsync("/openapi/v1.json");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var post = root.GetProperty("paths").GetProperty("/api/email/send").GetProperty("post");
            Assert.True(post.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
            foreach (var status in new[] { "200", "400", "401", "429", "502" })
                Assert.True(post.GetProperty("responses").TryGetProperty(status, out _), $"Falta la respuesta {status}");
            Assert.True(post.GetProperty("security")[0].TryGetProperty("ApiKey", out _));

            var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
            Assert.Equal("X-API-Key", scheme.GetProperty("name").GetString());

            // /health no requiere API key.
            var health = root.GetProperty("paths").GetProperty("/health").GetProperty("get");
            Assert.False(health.TryGetProperty("security", out _));
        }

        [Fact]
        public async Task SwaggerUi_IsServed()
        {
            using var factory = new ApiFactory();
            var client = factory.CreateClient();

            var response = await client.GetAsync("/swagger/index.html");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("swagger", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Documentation_IsDisabled_InProductionByDefault()
        {
            using var factory = new ApiFactory { Environment = "Production", SwaggerEnabled = false };
            var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        }
    }
}
