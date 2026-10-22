using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CorreosVelsat.Email
{
    /// <summary>
    /// Proveedor de respaldo por SMTP (Brevo, smtp-relay.brevo.com:2525 con STARTTLS).
    /// Mismo remitente, destinatarios, asunto, HTML y texto que Cloudflare.
    /// </summary>
    public sealed partial class SmtpEmailService : IEmailProvider
    {
        private readonly SmtpOptions _smtp;
        private readonly EmailOptions _email;
        private readonly ILogger<SmtpEmailService> _logger;

        /// <summary>Crea el cliente SMTP; reemplazable en tests.</summary>
        internal Func<ISmtpClient> ClientFactory { get; set; } = () => new SmtpClient();

        public SmtpEmailService(SmtpOptions smtp, EmailOptions email, ILogger<SmtpEmailService> logger)
        {
            _smtp = smtp;
            _email = email;
            _logger = logger;
        }

        public string Name => EmailProviderNames.Brevo;

        public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var resolved = EmailValidator.ApplyDefaults(message, _email);
            var errors = EmailValidator.Validate(resolved, _email);
            if (errors.Count > 0)
                return EmailSendResult.Invalid(errors) with { Provider = Name };

            EmailSendResult result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_email.Timeout);
            try
            {
                var mime = BuildMimeMessage(resolved);
                using var client = ClientFactory();
                client.Timeout = (int)_email.Timeout.TotalMilliseconds;
                await client.ConnectAsync(_smtp.Host, _smtp.Port, SecureSocketOptions.StartTls, timeout.Token);
                await client.AuthenticateAsync(_smtp.User, _smtp.Password, timeout.Token);
                await client.SendAsync(mime, timeout.Token);
                await client.DisconnectAsync(true, timeout.Token);
                result = new EmailSendResult { Success = true, MessageId = mime.MessageId, Attempts = 1, Provider = Name };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = EmailSendResult.Fail(EmailErrorKind.Timeout, $"Timeout SMTP de {(int)_email.Timeout.TotalMilliseconds} ms") with { Attempts = 1, Provider = Name };
            }
            catch (Exception ex)
            {
                result = Classify(ex) with { Attempts = 1, Provider = Name };
            }

            long durationMs = (long)stopwatch.Elapsed.TotalMilliseconds;
            if (result.Success)
            {
                _logger.LogInformation(
                    "Correo enviado por SMTP. To={To} From={From} Subject={Subject} MessageId={MessageId} DuracionMs={DurationMs}",
                    string.Join(", ", resolved.To), resolved.From, resolved.Subject, result.MessageId, durationMs);
            }
            else
            {
                _logger.LogWarning(
                    "Correo NO enviado por SMTP. To={To} From={From} Subject={Subject} Error={ErrorKind} Detalle={Error} DuracionMs={DurationMs}",
                    string.Join(", ", resolved.To), resolved.From, resolved.Subject, result.ErrorKind, result.ErrorMessage, durationMs);
            }
            return result;
        }

        internal static MimeMessage BuildMimeMessage(EmailMessage message)
        {
            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress(message.FromName ?? "", message.From!));
            mime.To.AddRange(message.To.Select(a => MailboxAddress.Parse(a)));
            mime.Cc.AddRange(message.Cc.Select(a => MailboxAddress.Parse(a)));
            mime.Bcc.AddRange(message.Bcc.Select(a => MailboxAddress.Parse(a)));
            if (message.ReplyTo is not null)
                mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
            mime.Subject = message.Subject;
            mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();
            return mime;
        }

        [GeneratedRegex(@"limit|quota|exceed|too many|rate|throttl", RegexOptions.IgnoreCase)]
        private static partial Regex LimitPattern();

        /// <summary>Traduce las excepciones de MailKit al mismo modelo de errores que Cloudflare.</summary>
        internal static EmailSendResult Classify(Exception ex)
        {
            switch (ex)
            {
                case SmtpCommandException cmd:
                {
                    int code = (int)cmd.StatusCode;
                    string detail = $"SMTP {code}: {cmd.Message}";
                    if (LimitPattern().IsMatch(cmd.Message))
                        return EmailSendResult.Fail(EmailErrorKind.RateLimited, detail, providerErrorCode: code);
                    if (code is 530 or 534 or 535)
                        return EmailSendResult.Fail(EmailErrorKind.Authentication, detail, providerErrorCode: code);
                    if (code is >= 400 and < 500)
                        return EmailSendResult.Fail(EmailErrorKind.ProviderError, detail, providerErrorCode: code);
                    return EmailSendResult.Fail(EmailErrorKind.Rejected, detail, providerErrorCode: code);
                }
                case AuthenticationException auth:
                    return EmailSendResult.Fail(EmailErrorKind.Authentication, $"SMTP autenticación: {auth.Message}");
                case SmtpProtocolException or ServiceNotConnectedException or SocketException or IOException or SslHandshakeException:
                    return EmailSendResult.Fail(EmailErrorKind.Network, $"SMTP red: {ex.Message}");
                case ParseException parse:
                    return EmailSendResult.Fail(EmailErrorKind.Validation, $"Dirección inválida: {parse.Message}");
                default:
                    return EmailSendResult.Fail(EmailErrorKind.Unexpected, $"SMTP error inesperado: {ex.Message}");
            }
        }
    }
}
