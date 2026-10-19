using CorreosVelsat.Email;
using Microsoft.Extensions.Configuration;

namespace CorreosVelsat.Tests
{
    public class EmailOptionsTests
    {
        private static IConfiguration Config(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        [Fact]
        public void ReadsSectionsFromAppSettings()
        {
            var options = EmailOptions.FromConfiguration(Config(new()
            {
                ["Cloudflare:AccountId"] = "acc",
                ["Cloudflare:ApiToken"] = "tok",
                ["Email:DefaultFrom"] = "facturas@velsat.pe",
                ["Email:DefaultFromName"] = "Velsat Facturas",
                ["Email:AllowedFromDomains:0"] = "velsat.pe",
                ["Email:AllowedFromDomains:1"] = "Otro.PE",
                ["Email:TimeoutMs"] = "5000",
                ["Email:MaxRetries"] = "2",
            }));

            Assert.Equal("acc", options.AccountId);
            Assert.Equal("tok", options.ApiToken);
            Assert.Equal("facturas@velsat.pe", options.DefaultFrom);
            Assert.Equal("Velsat Facturas", options.DefaultFromName);
            Assert.Equal(["velsat.pe", "otro.pe"], options.AllowedFromDomains);
            Assert.Equal(TimeSpan.FromSeconds(5), options.Timeout);
            Assert.Equal(2, options.MaxRetries);
        }

        [Fact]
        public void UsesDefaults_WhenOptionalValuesAreMissing()
        {
            var options = EmailOptions.FromConfiguration(Config(new()
            {
                ["Cloudflare:AccountId"] = "acc",
                ["Cloudflare:ApiToken"] = "tok",
            }));

            Assert.Equal("alertas@velsat.pe", options.DefaultFrom);
            Assert.Equal("Velsat Alertas", options.DefaultFromName);
            Assert.Equal(["velsat.pe"], options.AllowedFromDomains);
        }

        [Fact]
        public void MissingCloudflareValues_FailWithClearMessage()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => EmailOptions.FromConfiguration(Config(new())));

            Assert.Contains("Cloudflare:AccountId", ex.Message);
            Assert.Contains("Cloudflare:ApiToken", ex.Message);
            Assert.Contains("appsettings.json", ex.Message);
        }

        [Fact]
        public void ApiKey_IsRequired_AndMustBeLongEnough()
        {
            var missing = Assert.Throws<InvalidOperationException>(() => EmailEndpointOptions.FromConfiguration(Config(new())));
            Assert.Contains("Api:ApiKey", missing.Message);

            var shortKey = Assert.Throws<InvalidOperationException>(() =>
                EmailEndpointOptions.FromConfiguration(Config(new() { ["Api:ApiKey"] = "corta" })));
            Assert.Contains("24", shortKey.Message);
        }
    }
}
