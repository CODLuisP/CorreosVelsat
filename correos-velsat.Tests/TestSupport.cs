using System.Net;
using System.Text;
using CorreosVelsat.Email;

namespace CorreosVelsat.Tests
{
    internal sealed class FakeHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return await responder(request, Requests.Count, cancellationToken);
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public const string SuccessJson =
            """{"success":true,"errors":[],"messages":[],"result":{"message_id":"msg-123","delivered":["a@b.com"],"permanent_bounces":[],"queued":[]}}""";

        public static string ErrorJson(int code, string message) =>
            $$"""{"success":false,"errors":[{"code":{{code}},"message":"{{message}}"}],"messages":[],"result":null}""";
    }

    internal static class TestOptions
    {
        public static EmailOptions Create(int maxRetries = 3, TimeSpan? timeout = null) => new()
        {
            AccountId = "test-account",
            ApiToken = "test-token",
            DefaultFrom = "alertas@velsat.pe",
            DefaultFromName = "Velsat Alertas",
            AllowedFromDomains = ["velsat.pe"],
            MaxRetries = maxRetries,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            RetryBaseDelay = TimeSpan.FromMilliseconds(100),
        };
    }

    internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public void Set(DateTimeOffset now) => _now = now;
    }

    /// <summary>Proveedor falso: responde con <see cref="Respond"/> y cuenta las llamadas.</summary>
    internal sealed class FakeProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public int Calls { get; private set; }
        public Func<EmailMessage, EmailSendResult> Respond { get; set; } = _ => new EmailSendResult { Success = true, MessageId = "id", Attempts = 1 };

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Respond(message));
        }

        public static EmailSendResult Fail(EmailErrorKind kind) => EmailSendResult.Fail(kind, kind.ToString());
    }

    public sealed class FakeEmailService : IEmailService
    {
        public List<EmailMessage> Sent { get; } = [];
        public EmailSendResult NextResult { get; set; } = new() { Success = true, MessageId = "fake-id", Attempts = 1 };

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult(NextResult);
        }
    }
}
