using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CorreosVelsat.Email;
using Microsoft.AspNetCore.Mvc;

namespace CorreosVelsat.Api
{
    public static class EmailEndpoints
    {
        public const string SendRoute = "/api/email/send";
        public const string UsageRoute = "/api/email/usage";

        // JSON escapado + html y text pueden pesar más que el límite de 5 MiB del mensaje final.
        private const long MaxRequestBodyBytes = 12 * 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public static IEndpointRouteBuilder MapEmailEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapPost(SendRoute, SendAsync)
                .RequireApiKey()
                .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
                .WithName("SendEmail")
                .WithTags("Email")
                .WithSummary("Envía un correo personalizado")
                .WithDescription("""
                    Envía un correo por Cloudflare Email Sending o, al agotarse su cupo diario, por Brevo (SMTP).

                    - `from` es opcional. Si no se envía, se usa el remitente por defecto (`Email:DefaultFrom`). Solo se aceptan dominios de `Email:AllowedFromDomains`.
                    - `to`, `cc` y `bcc` aceptan un string o un array de strings. Máximo 50 destinatarios en total.
                    - Se requiere `html` o `text`. Si solo se envía `html`, la versión de texto se genera automáticamente.
                    - `attachments` es opcional: hasta 10 archivos, cada uno con `fileName`, `contentType` y `content` (base64).
                    - Tamaño máximo del mensaje: 5 MiB (incluye html + text + adjuntos). Asunto: hasta 998 caracteres.
                    - Si un proveedor responde con límite, 5xx o timeout, se usa el otro. Si ninguno puede, responde 502.
                    """)
                .Accepts<SendEmailRequest>("application/json")
                .Produces<SendEmailResponse>(StatusCodes.Status200OK)
                .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
                .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
                .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
                .Produces<ErrorResponse>(StatusCodes.Status502BadGateway);

            app.MapGet(UsageRoute, async (RoutingEmailService routing, CancellationToken cancellationToken) =>
                    Results.Ok(await routing.GetUsageAsync(cancellationToken)))
                .RequireApiKey()
                .WithName("GetEmailUsage")
                .WithTags("Email")
                .WithSummary("Cupo diario por proveedor")
                .WithDescription("Destinatarios enviados hoy (día en hora de Lima) por cada proveedor, su límite diario y si está bloqueado temporalmente por límite.")
                .Produces<EmailUsageReport>(StatusCodes.Status200OK)
                .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
                .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests);
            return app;
        }

        /// <summary>Exige X-API-Key y lo marca en la documentación OpenAPI.</summary>
        public static RouteHandlerBuilder RequireApiKey(this RouteHandlerBuilder builder) =>
            builder.AddEndpointFilter<ApiKeyEndpointFilter>().WithMetadata(new ApiKeyRequiredMetadata());

        private static async Task<IResult> SendAsync(HttpContext http, IEmailService emailService, EmailOptions options, CancellationToken cancellationToken)
        {
            if (!http.Request.HasJsonContentType())
                return BadRequest(["El Content-Type debe ser application/json."]);

            SendEmailRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<SendEmailRequest>(JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                return BadRequest(["El cuerpo de la solicitud no es un JSON válido o tiene campos con tipo incorrecto."]);
            }

            if (request is null)
                return BadRequest(["El cuerpo de la solicitud está vacío."]);

            var message = request.ToMessage(out var attachmentErrors);
            var errors = EmailValidator.Validate(EmailValidator.ApplyDefaults(message, options), options);
            if (attachmentErrors.Count > 0 || errors.Count > 0)
                return BadRequest([.. attachmentErrors, .. errors]);

            var result = await emailService.SendAsync(message, cancellationToken);
            if (result.Success)
                return Results.Ok(new SendEmailResponse(true, result.MessageId));
            if (result.ErrorKind == EmailErrorKind.Validation)
                return BadRequest(result.ValidationErrors);

            // El detalle (código de Cloudflare, mensaje) ya quedó en el log; al cliente no se le exponen detalles internos.
            return Results.Json(
                new ErrorResponse(false, "No se pudo enviar el correo a través del proveedor. Intenta de nuevo más tarde."),
                statusCode: StatusCodes.Status502BadGateway);
        }

        private static IResult BadRequest(IReadOnlyList<string> errors) =>
            Results.BadRequest(new ErrorResponse(false, "Solicitud inválida.", errors));
    }

    /// <summary>Correo enviado correctamente.</summary>
    /// <param name="Success">Siempre <c>true</c>.</param>
    /// <param name="MessageId">ID del mensaje asignado por Cloudflare.</param>
    public sealed record SendEmailResponse(bool Success, string? MessageId);

    /// <summary>Respuesta de error.</summary>
    /// <param name="Success">Siempre <c>false</c>.</param>
    /// <param name="Error">Descripción general del error.</param>
    /// <param name="Details">Lista de errores de validación (solo en 400).</param>
    public sealed record ErrorResponse(bool Success, string Error, IReadOnlyList<string>? Details = null);

    /// <summary>Marca un endpoint que requiere el header X-API-Key (usado por la documentación OpenAPI).</summary>
    public sealed class ApiKeyRequiredMetadata;

    /// <summary>Correo a enviar.</summary>
    public sealed class SendEmailRequest
    {
        /// <summary>Remitente. Opcional; por defecto Email:DefaultFrom. Debe ser de un dominio permitido.</summary>
        /// <example>ventas@velsat.pe</example>
        public string? From { get; set; }

        /// <summary>Nombre visible del remitente. Opcional.</summary>
        /// <example>Velsat Ventas</example>
        public string? FromName { get; set; }

        /// <summary>Destinatario(s): un string o un array. Obligatorio.</summary>
        /// <example>["cliente@gmail.com"]</example>
        [JsonConverter(typeof(StringOrArrayConverter))]
        public List<string>? To { get; set; }

        /// <summary>Copia. Un string o un array. Opcional.</summary>
        /// <example>[]</example>
        [JsonConverter(typeof(StringOrArrayConverter))]
        public List<string>? Cc { get; set; }

        /// <summary>Copia oculta. Un string o un array. Opcional.</summary>
        /// <example>[]</example>
        [JsonConverter(typeof(StringOrArrayConverter))]
        public List<string>? Bcc { get; set; }

        /// <summary>Dirección de respuesta. Opcional; puede ser de cualquier dominio.</summary>
        /// <example>soporte@velsat.pe</example>
        public string? ReplyTo { get; set; }

        /// <summary>Asunto. Obligatorio, hasta 998 caracteres, sin saltos de línea.</summary>
        /// <example>Cotización solicitada</example>
        public string? Subject { get; set; }

        /// <summary>Cuerpo HTML. Se requiere html o text.</summary>
        // El ejemplo se define en ApiDocumentation: el generador XML escaparía las etiquetas HTML.
        public string? Html { get; set; }

        /// <summary>Cuerpo en texto plano. Si se omite y hay html, se genera automáticamente.</summary>
        /// <example>Hola, adjuntamos la información solicitada.</example>
        public string? Text { get; set; }

        /// <summary>Adjuntos (PDF, XML, etc.). Opcional. El tamaño total del mensaje (html + text + adjuntos) no puede superar 5 MiB.</summary>
        public List<AttachmentDto>? Attachments { get; set; }

        /// <summary>
        /// Decodifica los adjuntos en base64. Los que tengan algún campo vacío o un base64 inválido
        /// se devuelven como errores en <paramref name="errors"/> en lugar de incluirse en el mensaje.
        /// </summary>
        public EmailMessage ToMessage(out IReadOnlyList<string> errors)
        {
            var attachments = new List<EmailAttachment>();
            var attachmentErrors = new List<string>();
            var dtos = Attachments ?? [];
            for (int i = 0; i < dtos.Count; i++)
            {
                var dto = dtos[i];
                if (string.IsNullOrWhiteSpace(dto.FileName) || string.IsNullOrWhiteSpace(dto.ContentType) || string.IsNullOrWhiteSpace(dto.Content))
                {
                    attachmentErrors.Add($"El adjunto #{i + 1} requiere fileName, contentType y content.");
                    continue;
                }
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(dto.Content);
                }
                catch (FormatException)
                {
                    attachmentErrors.Add($"El adjunto #{i + 1} ('{dto.FileName}') no tiene un contenido base64 válido.");
                    continue;
                }
                attachments.Add(new EmailAttachment { FileName = dto.FileName.Trim(), ContentType = dto.ContentType.Trim(), Content = bytes });
            }

            errors = attachmentErrors;
            return new EmailMessage
            {
                From = From,
                FromName = FromName,
                To = To ?? [],
                Cc = Cc ?? [],
                Bcc = Bcc ?? [],
                ReplyTo = ReplyTo,
                Subject = Subject ?? "",
                Html = Html,
                Text = Text,
                Attachments = attachments,
            };
        }
    }

    /// <summary>Adjunto enviado en base64.</summary>
    public sealed class AttachmentDto
    {
        /// <summary>Nombre del archivo. Obligatorio.</summary>
        /// <example>factura-F001-123.pdf</example>
        public string? FileName { get; set; }

        /// <summary>Tipo MIME. Obligatorio.</summary>
        /// <example>application/pdf</example>
        public string? ContentType { get; set; }

        /// <summary>Contenido del archivo en base64. Obligatorio.</summary>
        public string? Content { get; set; }
    }

    /// <summary>Acepta "a@x.com" o ["a@x.com", "b@x.com"].</summary>
    public sealed class StringOrArrayConverter : JsonConverter<List<string>>
    {
        public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return [reader.GetString()!];

            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Se esperaba un string o un array de strings.");

            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("Se esperaba un array de strings.");
                list.Add(reader.GetString()!);
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, options);
    }

    /// <summary>Exige el header X-API-Key igual a Api:ApiKey (comparación en tiempo constante).</summary>
    public sealed class ApiKeyEndpointFilter(EmailEndpointOptions options) : IEndpointFilter
    {
        public const string HeaderName = "X-API-Key";

        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            string provided = context.HttpContext.Request.Headers[HeaderName].ToString();
            if (!Matches(provided, options.ApiKey))
            {
                return ValueTask.FromResult<object?>(Results.Json(
                    new ErrorResponse(false, $"No autorizado. Envía un header {HeaderName} válido."),
                    statusCode: StatusCodes.Status401Unauthorized));
            }
            return next(context);
        }

        internal static bool Matches(string provided, string expected)
        {
            if (string.IsNullOrEmpty(provided))
                return false;
            // Hash previo para que ambos tengan la misma longitud y la comparación no filtre el largo de la clave.
            byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
            byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
    }
}
