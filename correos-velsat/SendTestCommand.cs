using CorreosVelsat.Email;

namespace CorreosVelsat
{
    /// <summary>
    /// dotnet run -- send-test destino@ejemplo.com [remitente@velsat.pe]
    /// Envía un correo real usando la configuración de appsettings.json y sale con código 0 si se envió.
    /// </summary>
    internal static class SendTestCommand
    {
        public static async Task<int> RunAsync(IServiceProvider services, string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Uso: dotnet run -- send-test <destinatario> [remitente]");
                return 2;
            }

            var emailService = services.GetRequiredService<IEmailService>();
            var result = await emailService.SendAsync(new EmailMessage
            {
                To = [args[0]],
                From = args.Length > 1 ? args[1] : null,
                Subject = "Prueba de Correos Velsat (Cloudflare Email Sending)",
                Html = $"<p>Correo de prueba enviado desde Correos Velsat el {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}.</p>",
            });

            if (result.Success)
            {
                Console.WriteLine($"OK. message_id: {result.MessageId}");
                return 0;
            }

            Console.Error.WriteLine($"FALLÓ ({result.ErrorKind}): {result.ErrorMessage}");
            return 1;
        }
    }
}
