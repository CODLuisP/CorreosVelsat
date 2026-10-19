using System.Text;
using System.Text.RegularExpressions;

namespace CorreosVelsat.Email
{
    public static partial class EmailValidator
    {
        // Formato pragmático (RFC 5322 sin comillas ni comentarios): local@dominio.tld
        [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$")]
        private static partial Regex AddressRegex();

        public static bool IsValidAddress(string? address) =>
            address is not null
            && address.Length <= 254
            && address.IndexOf('@') is > 0 and <= 64
            && AddressRegex().IsMatch(address);

        /// <summary>
        /// Dominio exacto (sin subdominios): "x@velsat.pe" es válido para "velsat.pe";
        /// "x@velsat.pe.evil.com" o "x@sub.velsat.pe" no, salvo que se listen explícitamente.
        /// </summary>
        public static bool IsAllowedFrom(string from, IReadOnlyList<string> allowedDomains)
        {
            int at = from.LastIndexOf('@');
            if (at < 0)
                return false;
            string domain = from[(at + 1)..];
            return allowedDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Aplica remitente por defecto, recorta espacios y genera la versión texto si solo hay HTML.
        /// </summary>
        public static EmailMessage ApplyDefaults(EmailMessage message, EmailOptions options)
        {
            static IReadOnlyList<string> Clean(IReadOnlyList<string>? list) =>
                list is null ? [] : list.Select(a => a?.Trim() ?? "").Where(a => a.Length > 0).ToArray();

            static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

            bool usesDefaultFrom = Blank(message.From) is null;
            string? html = string.IsNullOrWhiteSpace(message.Html) ? null : message.Html;
            string? text = string.IsNullOrWhiteSpace(message.Text) ? null : message.Text;

            return message with
            {
                To = Clean(message.To),
                Cc = Clean(message.Cc),
                Bcc = Clean(message.Bcc),
                From = usesDefaultFrom ? options.DefaultFrom : Blank(message.From),
                FromName = Blank(message.FromName) ?? (usesDefaultFrom ? options.DefaultFromName : null),
                ReplyTo = Blank(message.ReplyTo),
                Subject = message.Subject?.Trim() ?? "",
                Html = html,
                Text = text ?? (html is null ? null : HtmlToText.Convert(html)),
            };
        }

        /// <summary>Valida un mensaje ya pasado por <see cref="ApplyDefaults"/>. Devuelve la lista de errores (vacía si es válido).</summary>
        public static IReadOnlyList<string> Validate(EmailMessage message, EmailOptions options)
        {
            var errors = new List<string>();

            if (!IsValidAddress(message.From))
                errors.Add($"El remitente '{message.From}' no es un email válido.");
            else if (!IsAllowedFrom(message.From!, options.AllowedFromDomains))
                errors.Add($"El remitente '{message.From}' no está permitido. Dominios permitidos: {string.Join(", ", options.AllowedFromDomains)}.");

            if (message.FromName is { } fromName && (fromName.Length > 200 || HasLineBreak(fromName)))
                errors.Add("El nombre del remitente no puede tener saltos de línea ni más de 200 caracteres.");

            if (message.To.Count == 0)
                errors.Add("Se requiere al menos un destinatario en 'to'.");

            int totalRecipients = message.To.Count + message.Cc.Count + message.Bcc.Count;
            if (totalRecipients > EmailOptions.MaxRecipients)
                errors.Add($"Máximo {EmailOptions.MaxRecipients} destinatarios en total (to + cc + bcc); se recibieron {totalRecipients}.");

            foreach (var (field, list) in new[] { ("to", message.To), ("cc", message.Cc), ("bcc", message.Bcc) })
            {
                foreach (var address in list.Where(a => !IsValidAddress(a)))
                    errors.Add($"Email inválido en '{field}': '{address}'.");
            }

            if (message.ReplyTo is not null && !IsValidAddress(message.ReplyTo))
                errors.Add($"Email inválido en 'replyTo': '{message.ReplyTo}'.");

            if (string.IsNullOrWhiteSpace(message.Subject))
                errors.Add("El asunto (subject) es obligatorio.");
            else if (message.Subject.Length > EmailOptions.MaxSubjectLength)
                errors.Add($"El asunto no puede superar {EmailOptions.MaxSubjectLength} caracteres.");
            else if (HasLineBreak(message.Subject))
                errors.Add("El asunto no puede contener saltos de línea.");

            if (message.Html is null && message.Text is null)
                errors.Add("Se requiere 'html' o 'text' (al menos uno).");

            long size = Encoding.UTF8.GetByteCount(message.Subject ?? "")
                + Encoding.UTF8.GetByteCount(message.Html ?? "")
                + Encoding.UTF8.GetByteCount(message.Text ?? "");
            if (size > EmailOptions.MaxMessageBytes)
                errors.Add($"El mensaje supera el tamaño máximo de {EmailOptions.MaxMessageBytes / (1024 * 1024)} MiB.");

            return errors;
        }

        private static bool HasLineBreak(string value) => value.Contains('\r') || value.Contains('\n');
    }
}
