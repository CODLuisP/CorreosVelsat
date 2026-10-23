using System.Net.Sockets;
using CorreosVelsat.Email;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;

namespace CorreosVelsat.Tests
{
    public class SmtpEmailServiceTests
    {
        [Theory]
        [InlineData(SmtpStatusCode.ServiceNotAvailable, "4.7.0 Rate limit exceeded, try again later", EmailErrorKind.RateLimited)]
        [InlineData(SmtpStatusCode.TransactionFailed, "5.7.1 Daily sending quota exceeded", EmailErrorKind.RateLimited)]
        [InlineData(SmtpStatusCode.MailboxUnavailable, "Too many emails sent", EmailErrorKind.RateLimited)]
        [InlineData(SmtpStatusCode.AuthenticationInvalidCredentials, "Authentication failed", EmailErrorKind.Authentication)]
        [InlineData(SmtpStatusCode.ErrorInProcessing, "Local error in processing", EmailErrorKind.ProviderError)]
        [InlineData(SmtpStatusCode.MailboxUnavailable, "Recipient address rejected", EmailErrorKind.Rejected)]
        public void Classify_SmtpCommandErrors(SmtpStatusCode status, string message, EmailErrorKind expected)
        {
            var ex = new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, status, message);

            Assert.Equal(expected, SmtpEmailService.Classify(ex).ErrorKind);
        }

        [Fact]
        public void Classify_NetworkErrors_AreTransient()
        {
            Assert.Equal(EmailErrorKind.Network, SmtpEmailService.Classify(new SocketException()).ErrorKind);
            Assert.Equal(EmailErrorKind.Network, SmtpEmailService.Classify(new IOException("reset")).ErrorKind);
        }

        [Fact]
        public void BuildMimeMessage_UsesSameFromRecipientsAndBodies()
        {
            var resolved = EmailValidator.ApplyDefaults(new EmailMessage
            {
                To = ["a@gmail.com"],
                Cc = ["c@gmail.com"],
                Bcc = ["b@gmail.com"],
                ReplyTo = "soporte@velsat.pe",
                Subject = "Alerta",
                Html = "<p>Hola</p>",
            }, TestOptions.Create());

            var mime = SmtpEmailService.BuildMimeMessage(resolved);

            Assert.Equal("\"Velsat Alertas\" <alertas@velsat.pe>", mime.From.ToString());
            Assert.Equal("a@gmail.com", mime.To.ToString());
            Assert.Equal("c@gmail.com", mime.Cc.ToString());
            Assert.Equal("b@gmail.com", mime.Bcc.ToString());
            Assert.Equal("soporte@velsat.pe", mime.ReplyTo.ToString());
            Assert.Equal("Alerta", mime.Subject);
            Assert.Equal("<p>Hola</p>", mime.HtmlBody);
            Assert.Equal("Hola", mime.TextBody);
        }

        [Fact]
        public void BuildMimeMessage_IncludesAttachments()
        {
            byte[] pdfBytes = [1, 2, 3, 4];
            var resolved = EmailValidator.ApplyDefaults(new EmailMessage
            {
                To = ["a@gmail.com"],
                Subject = "Factura",
                Html = "<p>Hola</p>",
                Attachments = [new EmailAttachment { FileName = "factura.pdf", ContentType = "application/pdf", Content = pdfBytes }],
            }, TestOptions.Create());

            var mime = SmtpEmailService.BuildMimeMessage(resolved);

            var attachment = Assert.Single(mime.Attachments);
            var part = Assert.IsType<MimeKit.MimePart>(attachment);
            Assert.Equal("factura.pdf", part.FileName);
            Assert.Equal("application/pdf", part.ContentType.MimeType);
            using var ms = new MemoryStream();
            Assert.NotNull(part.Content);
            part.Content.DecodeTo(ms);
            Assert.Equal(pdfBytes, ms.ToArray());
        }

        [Fact]
        public void SmtpOptions_Defaults_AndRequiredCredentialsWhenEnabled()
        {
            var disabled = SmtpOptions.FromConfiguration(new ConfigurationBuilder().Build());
            Assert.False(disabled.Enabled);
            Assert.Equal("smtp-relay.brevo.com", disabled.Host);
            Assert.Equal(2525, disabled.Port);
            Assert.Equal(300, disabled.DailyLimit);

            var ex = Assert.Throws<InvalidOperationException>(() => SmtpOptions.FromConfiguration(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Smtp:Enabled"] = "true" }).Build()));
            Assert.Contains("Smtp__User", ex.Message);
            Assert.Contains("Smtp__Password", ex.Message);
        }
    }
}
