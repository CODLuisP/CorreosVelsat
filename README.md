# Correos Velsat (correos-velsat)

Servicio en .NET 10 (ASP.NET Core) que:

- **Procesa la cola `cola_correos`** de MySQL cada `Worker:IntervalSeconds` y envía cada correo pendiente.
- **Expone `POST /api/email/send`** para enviar correos personalizados.

Todo el envío pasa por `IEmailService` (`CloudflareEmailService`), que usa la **API REST de Cloudflare Email Sending** por HTTPS. No usa SMTP: en el droplet de DigitalOcean los puertos 25, 465 y 587 están bloqueados.

## Configuración (`appsettings.json`)

Toda la configuración vive en **`correos-velsat/appsettings.json`**, que **no se sube a git** porque tiene secretos. La plantilla está en [`correos-velsat/appsettings.example.json`](correos-velsat/appsettings.example.json): cópiala como `appsettings.json` y completa los valores.

| Sección : clave | Obligatoria | Default | Descripción |
|---|---|---|---|
| `ConnectionStrings:MySql` | si el worker está activo | | Conexión a la base de la cola |
| `Cloudflare:AccountId` | sí | | ID de la cuenta de Cloudflare |
| `Cloudflare:ApiToken` | sí | | Token con permiso *Email Sending: Edit* |
| `Email:DefaultFrom` | | `alertas@velsat.pe` | Remitente por defecto (alertas de la cola) |
| `Email:DefaultFromName` | | `Velsat Alertas` | Nombre del remitente por defecto |
| `Email:AllowedFromDomains` | | `["velsat.pe"]` | Dominios permitidos en `from` |
| `Email:TimeoutMs` | | `15000` | Timeout por intento |
| `Email:MaxRetries` | | `3` | Reintentos solo para 429, 5xx, timeouts y errores de red |
| `Api:ApiKey` | sí | | Clave del header `X-API-Key` (mínimo 24 caracteres) |
| `Api:RateLimitPerMinute` | | `30` | Solicitudes por minuto por IP al endpoint |
| `Api:RateLimitPerDay` | | `2000` | Tope diario global del endpoint |
| `Api:SwaggerEnabled` | | `false` | Publica Swagger UI fuera de Development |
| `Worker:Enabled` | | `false` | `true` activa el envío automático de `cola_correos`. Actívalo solo cuando el MailWorker anterior esté detenido, para no duplicar envíos |
| `Worker:IntervalSeconds` | | `60` | Cada cuánto se revisa la cola |
| `Worker:BatchSize` | | `10` | Correos por lote |
| `Worker:MaxRetries` | | `5` | Intentos máximos por correo de la cola |

Si falta un valor obligatorio, el proceso no arranca y dice cuál falta.

Cualquier clave también se puede sobrescribir con una variable de entorno usando `__` en lugar de `:` (por ejemplo `Worker__Enabled=true`), aunque no es necesario.

### En el servidor Linux

1. Publica con `dotnet publish correos-velsat -c Release -o publish`. El `appsettings.json` local **no se incluye** en la publicación, para no pisar el del servidor.
2. En la carpeta del servidor (por ejemplo `/opt/correos-velsat`), crea **una sola vez** `appsettings.json` a partir de la plantilla, con las claves de producción.
3. En el servicio de systemd define `WorkingDirectory=/opt/correos-velsat`, porque el `appsettings.json` se busca en el directorio de trabajo. Arranca con `ExecStart=/usr/bin/dotnet /opt/correos-velsat/correos-velsat.dll`.
4. Para que escuche detrás de nginx, agrega `Environment=ASPNETCORE_URLS=http://127.0.0.1:8080`.

## Uso desde el código

```csharp
public class AlertasService(IEmailService email)
{
    public async Task NotificarAsync(string destino)
    {
        var result = await email.SendAsync(new EmailMessage
        {
            To = [destino],
            Subject = "Alerta de exceso de velocidad",
            Html = "<p>La unidad ABC-123 superó 90 km/h.</p>",
            // Opcionales: From = "soporte@velsat.pe", FromName, Cc, Bcc, ReplyTo, Text
        });

        if (!result.Success)
        {
            // result.ErrorKind / result.ErrorMessage: SendAsync nunca lanza por fallos de envío.
        }
    }
}
```

## Documentación interactiva (Swagger UI)

| Ruta | Contenido |
|---|---|
| `/swagger` | Swagger UI: rutas, modelos, ejemplos y botón *Try it out* (`/` redirige aquí) |
| `/openapi/v1.json` | Documento OpenAPI 3.1, importable en Postman o Insomnia |

- **En desarrollo:** `dotnet run --project correos-velsat` usa el perfil Development y abre `http://localhost:5000/swagger`.
- **En producción:** está apagado por defecto. Se activa con `"Api": { "SwaggerEnabled": true }`.
- **Para probar el envío desde Swagger:** pulsa **Authorize** y pega el valor de `Api:ApiKey`.

## Endpoint

```bash
curl -X POST http://localhost:5000/api/email/send \
  -H "Content-Type: application/json" \
  -H "X-API-Key: TU_API_KEY" \
  -d '{
    "from": "ventas@velsat.pe",
    "fromName": "Velsat Ventas",
    "to": ["cliente@gmail.com"],
    "replyTo": "soporte@velsat.pe",
    "subject": "Cotización",
    "html": "<p>Hola, adjuntamos la información.</p>"
  }'
```

Respuestas:

| Código | Cuándo | Cuerpo |
|---|---|---|
| 200 | Enviado | `{ "success": true, "messageId": "..." }` |
| 400 | Validación (email inválido, `from` de otro dominio, sin `subject`, sin `html`/`text`, más de 50 destinatarios, más de 5 MiB) | `{ "success": false, "error": "...", "details": [...] }` |
| 401 | Falta `X-API-Key` o es incorrecta | |
| 429 | Rate limit | |
| 502 | Cloudflare falló tras los reintentos | |

`to`, `cc` y `bcc` aceptan un string o un array. Si solo envías `html`, la versión `text` se genera automáticamente.

`GET /health` responde `{ "status": "ok" }`.

## Pruebas

```bash
dotnet test
```

Para enviar un correo real con la configuración de `appsettings.json`:

```bash
cd correos-velsat
dotnet run -- send-test tu-correo@gmail.com
dotnet run -- send-test tu-correo@gmail.com facturas@velsat.pe
```
