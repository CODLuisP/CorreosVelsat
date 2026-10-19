# Correos Velsat (correos-velsat)

Servicio en .NET 10 (ASP.NET Core) que:

- **Procesa la cola `cola_correos`** de MySQL cada `Worker:IntervalSeconds` y envía cada correo pendiente.
- **Expone `POST /api/email/send`** para enviar correos personalizados.

Todo el envío pasa por `IEmailService` (`CloudflareEmailService`), que usa la **API REST de Cloudflare Email Sending** por HTTPS. No usa SMTP: en el droplet de DigitalOcean los puertos 25, 465 y 587 están bloqueados.

## Configuración

La misma configuración se puede dar de dos formas:

- **Docker / servidor:** variables de entorno en un archivo `.env` junto a `docker-compose.yml`. La plantilla es [`.env.example`](.env.example).
- **Desarrollo local:** `correos-velsat/appsettings.json`, con la plantilla [`correos-velsat/appsettings.example.json`](correos-velsat/appsettings.example.json).

Ninguno de los dos se sube a git. Si existen ambos, la variable de entorno gana. La variable `Seccion__Clave` equivale a `"Seccion": { "Clave": ... }`.

| Variable de entorno | Obligatoria | Default | Descripción |
|---|---|---|---|
| `ConnectionStrings__MySql` | si el worker está activo | | Conexión a la base de la cola |
| `Cloudflare__AccountId` | sí | | ID de la cuenta de Cloudflare |
| `Cloudflare__ApiToken` | sí | | Token con permiso *Email Sending: Edit* |
| `Email__DefaultFrom` | | `alertas@velsat.pe` | Remitente por defecto (alertas de la cola) |
| `Email__DefaultFromName` | | `Velsat Alertas` | Nombre del remitente por defecto |
| `Email__AllowedFromDomains` | | `velsat.pe` | Dominios permitidos en `from`, separados por comas |
| `Email__TimeoutMs` | | `15000` | Timeout por intento |
| `Email__MaxRetries` | | `3` | Reintentos solo para 429, 5xx, timeouts y errores de red |
| `Api__ApiKey` | sí | | Clave del header `X-API-Key` (mínimo 24 caracteres) |
| `Api__RateLimitPerMinute` | | `30` | Solicitudes por minuto por IP al endpoint |
| `Api__RateLimitPerDay` | | `2000` | Tope diario global del endpoint |
| `Api__SwaggerEnabled` | | `false` | Publica Swagger UI fuera de Development |
| `Worker__Enabled` | | `false` | `true` activa el envío automático de `cola_correos`. Actívalo solo cuando el MailWorker anterior esté detenido, para no duplicar envíos |
| `Worker__IntervalSeconds` | | `60` | Cada cuánto se revisa la cola |
| `Worker__BatchSize` | | `10` | Correos por lote |
| `Worker__MaxRetries` | | `5` | Intentos máximos por correo de la cola |
| `PathBase` | | | Prefijo de ruta detrás de nginx, por ejemplo `/correos` |

Si falta un valor obligatorio, el proceso no arranca y dice cuál falta.

## Despliegue con Docker

El contenedor escucha en `0.0.0.0:8080`. Docker Compose lo publica **solo en loopback** como `127.0.0.1:3009`, y nginx del host lo expone en `https://do.velsat.pe:8443/correos/`. No necesita volúmenes: no guarda archivos y los logs van a stdout, con un límite de 3 × 10 MB.

### Primera vez

```bash
git clone https://github.com/CODLuisP/CorreosVelsat.git correos-velsat
cd correos-velsat
cp .env.example .env
nano .env                      # completar token, API key, MySQL...
docker compose up -d --build
docker compose ps              # debe pasar a (healthy)
curl http://127.0.0.1:3009/health
```

### Actualizar

```bash
cd correos-velsat
git pull
docker compose up -d --build
docker image prune -f          # borra las imágenes viejas sin uso
```

### Logs y operación

```bash
docker compose logs -f --tail 100     # logs en vivo
docker compose restart                # reiniciar (p. ej. después de editar .env)
docker compose down                   # detener y eliminar el contenedor
```

Después de editar `.env`, aplica los cambios con `docker compose up -d`, que recrea el contenedor. `restart` no vuelve a leer `.env`.

### nginx (host)

Dentro del bloque `server` que escucha en 8443 para `do.velsat.pe`:

```nginx
location /correos/ {
    proxy_pass http://127.0.0.1:3009;   # SIN barra final: conserva /correos/ (la app usa PathBase=/correos)
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    client_max_body_size 12m;           # el default de nginx (1 MB) rechazaría correos grandes con 413
    proxy_read_timeout 120s;            # un envío con reintentos puede tardar más de 60 s
}
```

Prueba: `curl https://do.velsat.pe:8443/correos/health`.

### Activar el envío automático de la cola

1. Detén el MailWorker anterior del servidor Windows.
2. En `.env` pon `Worker__Enabled=true`.
3. Ejecuta `docker compose up -d`.

Mantén **una sola instancia**: dos workers sobre la misma tabla pueden duplicar correos.

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
- **En producción:** está apagado por defecto. Se activa con `Api__SwaggerEnabled=true` y queda en `https://do.velsat.pe:8443/correos/swagger`.
- **Para probar el envío desde Swagger:** pulsa **Authorize** y pega el valor de `Api__ApiKey`.

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

Para enviar un correo real con la configuración local (`appsettings.json`):

```bash
cd correos-velsat
dotnet run -- send-test tu-correo@gmail.com
dotnet run -- send-test tu-correo@gmail.com facturas@velsat.pe
```
