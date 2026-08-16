using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VentoryPrint.Models;
using VentoryPrint.Printing;
using VentoryPrint.Services;

namespace VentoryPrint.Hosting;

/// <summary>
/// Servidor HTTP local en 127.0.0.1:{puerto}. El navegador del POS (ventoryPOS)
/// le manda los tickets directamente:
///   GET  /status              → ¿agente vivo? (lo usa agenteActivo() del frontend)
///   POST /print               → imprime el TicketPayload (valida el token de la caja)
///   POST /print-shift-closure → imprime el reporte de cierre de turno
///   GET  /                    → mini página informativa
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrintServer : BackgroundService
{
    public const string Version = "1.2.0";

    private static readonly JsonSerializerOptions JsonIn = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly SettingsService _settings;
    private readonly TicketRenderer _ticketRenderer;
    private readonly ShiftClosureRenderer _closureRenderer;
    private readonly ILogger<PrintServer> _log;
    private HttpListener? _listener;

    public PrintServer(SettingsService settings, TicketRenderer ticketRenderer, ShiftClosureRenderer closureRenderer, ILogger<PrintServer> log)
    {
        _settings = settings;
        _ticketRenderer = ticketRenderer;
        _closureRenderer = closureRenderer;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = _settings.Load();
        var port = s?.Port ?? 9111;
        var host = string.IsNullOrWhiteSpace(s?.Host) ? "127.0.0.1" : s.Host.Trim();

        _listener = new HttpListener();

        // Si es localhost, registramos ambas formas por compatibilidad.
        if (host is "127.0.0.1" or "localhost")
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add($"http://localhost:{port}/");
        }
        else
        {
            _listener.Prefixes.Add($"http://{host}:{port}/");
        }

        try
        {
            _listener.Start();
            _log.LogInformation("VentoryPrint escuchando en http://{Host}:{Port}/", host, port);
        }
        catch (HttpListenerException ex)
        {
            var url = host is "127.0.0.1" or "localhost"
                ? $"http://127.0.0.1:{port}/"
                : $"http://{host}:{port}/";
            var errorCode = (int)ex.ErrorCode;
            var isAccessDenied = errorCode == 5;
            _log.LogError(ex,
                "No se pudo abrir {Url}. Código {ErrorCode}. " +
                (isAccessDenied
                    ? "Falta permiso de URL ACL para este host. " +
                      "Ejecuta UNA vez como administrador: netsh http add urlacl url={Url} user=\"%USERDOMAIN%\\%USERNAME%\""
                    : "¿Otro programa lo está usando, o falta permiso netsh?"),
                url, ex.ErrorCode);
            throw new InvalidOperationException(
                $"No se pudo abrir {url} (código {ex.ErrorCode}). " +
                (isAccessDenied
                    ? "Ejecuta UNA vez como administrador: netsh http add urlacl url=" + url + " user=\"%USERDOMAIN%\\%USERNAME%\""
                    : "¿Otro programa lo está usando, falta permiso netsh, u otro VentoryPrint ya corre en esta PC?"), ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(() => HandleAsync(ctx), stoppingToken);
        }

        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
        _log.LogInformation("PrintServer detenido.");
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // CORS + Private Network Access: el POS puede estar servido por HTTPS en
        // internet y el fetch va a 127.0.0.1 — Chrome exige estos encabezados.
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Requested-With";
        res.Headers["Access-Control-Allow-Private-Network"] = "true";

        try
        {
            if (req.HttpMethod == "OPTIONS")
            {
                await WriteJsonAsync(res, 200, "{\"ok\":true}");
                return;
            }

            // Aprende la URL del POS desde el header Origin (para las actualizaciones).
            RememberOrigin(req.Headers["Origin"]);

            var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";

            switch (path)
            {
                case "/status" when req.HttpMethod == "GET":
                    await HandleStatusAsync(res);
                    return;

                case "/print" when req.HttpMethod == "POST":
                    await HandlePrintAsync(req, res);
                    return;

                case "/print-shift-closure" when req.HttpMethod == "POST":
                    await HandleShiftClosureAsync(req, res);
                    return;

                case "/" when req.HttpMethod == "GET":
                    await WriteHtmlAsync(res, BuildInfoPage());
                    return;

                default:
                    await WriteJsonAsync(res, 404, "{\"ok\":false,\"error\":\"Ruta no valida\"}");
                    return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error procesando request HTTP local");
            try { res.Abort(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Guarda la URL base del POS que nos manda tickets, para pedirle actualizaciones.
    /// Solo escribe si cambió, así no toca el disco en cada impresión.
    /// </summary>
    private void RememberOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return;
        origin = origin.Trim().TrimEnd('/');
        if (!origin.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var s = _settings.Load();
            if (s is null) return;
            if (string.Equals(s.UpdateBaseUrl, origin, StringComparison.OrdinalIgnoreCase)) return;
            s.UpdateBaseUrl = origin;
            _settings.Save(s); // plainToken null: conserva el token cifrado existente
            _log.LogInformation("Origen del POS aprendido para actualizaciones: {Origin}", origin);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "No se pudo guardar el origen del POS");
        }
    }

    /// <summary>
    /// Valida el token del ticket contra la lista de tokens permitidos.
    /// Si no hay tokens configurados, acepta cualquiera (modo abierto).
    /// </summary>
    private bool EsTokenValido(string? ticketToken)
    {
        var tokens = _settings.GetTokensPlain();
        if (tokens.Count == 0) return true; // sin tokens = acepta cualquiera
        return tokens.Contains(ticketToken ?? "", StringComparer.Ordinal);
    }

    private async Task HandleStatusAsync(HttpListenerResponse res)
    {
        var s = _settings.Load();
        var tokens = _settings.GetTokensPlain(s);
        var body = JsonSerializer.Serialize(new
        {
            ok = true,
            app = "VentoryPrint",
            version = Version,
            impresora = s?.PrinterName ?? "",
            caja = s?.CajaNombre ?? "",
            requiereToken = tokens.Count > 0,
            tokensConfigurados = tokens.Count,
        });
        await WriteJsonAsync(res, 200, body);
    }

    private async Task HandlePrintAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var s = _settings.Load();
        if (s is null || string.IsNullOrWhiteSpace(s.PrinterName))
        {
            await WriteJsonAsync(res, 409, "{\"ok\":false,\"error\":\"El agente no tiene impresora configurada. Abre VentoryPrint y configura.\"}");
            return;
        }

        TicketPayload? ticket;
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var json = await reader.ReadToEndAsync();
            ticket = JsonSerializer.Deserialize<TicketPayload>(json, JsonIn);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "JSON de ticket inválido");
            await WriteJsonAsync(res, 400, "{\"ok\":false,\"error\":\"JSON invalido\"}");
            return;
        }

        if (ticket is null || ticket.Items.Count == 0)
        {
            await WriteJsonAsync(res, 400, "{\"ok\":false,\"error\":\"Ticket vacio\"}");
            return;
        }

        // El payload trae su propio origen: es más fiable que el header.
        RememberOrigin(ticket.Origen);

        // Validación del token de la caja: si el agente tiene tokens configurados,
        // el ticket DEBE traer uno de ellos. Así una PC puede imprimir para varias cajas.
        if (!EsTokenValido(ticket.Token))
        {
            _log.LogWarning("Ticket rechazado: token no coincide (caja equivocada o token desactualizado)");
            await WriteJsonAsync(res, 403,
                "{\"ok\":false,\"error\":\"Token invalido: este ticket no corresponde a las cajas configuradas en esta PC.\"}");
            return;
        }

        try
        {
            var bytes = _ticketRenderer.Render(ticket);
            var copias = Math.Clamp(ticket.Copias, 1, 5);
            var numero = ticket.Documento?.Numero ?? "s/n";

            for (var i = 0; i < copias; i++)
                RawPrinterHelper.SendBytesToPrinter(s.PrinterName, bytes, $"Ventory {numero}");

            _log.LogInformation("Ticket {Numero} impreso en '{Printer}' ({Copias} copia(s), {Items} items)",
                numero, s.PrinterName, copias, ticket.Items.Count);

            await WriteJsonAsync(res, 200, "{\"ok\":true}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fallo al imprimir");
            await WriteJsonAsync(res, 500,
                $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize("Error al imprimir: " + ex.Message)}}}");
        }
    }

    private async Task HandleShiftClosureAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var s = _settings.Load();
        if (s is null || string.IsNullOrWhiteSpace(s.PrinterName))
        {
            await WriteJsonAsync(res, 409, "{\"ok\":false,\"error\":\"El agente no tiene impresora configurada. Abre VentoryPrint y configura.\"}");
            return;
        }

        ShiftClosurePayload? closure;
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var json = await reader.ReadToEndAsync();
            closure = JsonSerializer.Deserialize<ShiftClosurePayload>(json, JsonIn);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "JSON de cierre de turno inválido");
            await WriteJsonAsync(res, 400, "{\"ok\":false,\"error\":\"JSON invalido\"}");
            return;
        }

        if (closure is null || closure.Turno is null)
        {
            await WriteJsonAsync(res, 400, "{\"ok\":false,\"error\":\"Cierre de turno vacio\"}");
            return;
        }

        RememberOrigin(closure.Origen);

        if (!EsTokenValido(closure.Token))
        {
            _log.LogWarning("Cierre de turno rechazado: token no coincide");
            await WriteJsonAsync(res, 403,
                "{\"ok\":false,\"error\":\"Token invalido: este cierre no corresponde a las cajas configuradas en esta PC.\"}");
            return;
        }

        try
        {
            var bytes = _closureRenderer.Render(closure);
            var copias = Math.Clamp(closure.Copias, 1, 5);
            var turnoId = closure.Turno.Id ?? closure.Turno.Nombre ?? "s/n";

            for (var i = 0; i < copias; i++)
                RawPrinterHelper.SendBytesToPrinter(s.PrinterName, bytes, $"Ventory Cierre {turnoId}");

            _log.LogInformation("Cierre de turno {Turno} impreso en '{Printer}' ({Copias} copia(s), {Metodos} metodos de pago)",
                turnoId, s.PrinterName, copias, closure.MetodosPago.Count);

            await WriteJsonAsync(res, 200, "{\"ok\":true}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fallo al imprimir cierre de turno");
            await WriteJsonAsync(res, 500,
                $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize("Error al imprimir: " + ex.Message)}}}");
        }
    }

    private string BuildInfoPage()
    {
        var s = _settings.Load();
        var tokens = _settings.GetTokensPlain(s);
        var tokenText = tokens.Count == 0
            ? "SIN TOKENS (acepta cualquier ticket)"
            : $"{tokens.Count} token(s) configurado(s)";
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8">
            <title>VentoryPrint</title>
            <style>
              body{font-family:Segoe UI,Arial,sans-serif;background:#0f172a;color:#e2e8f0;display:grid;place-items:center;min-height:100vh;margin:0}
              .card{background:#1e293b;border-radius:12px;padding:32px 40px;max-width:460px;box-shadow:0 10px 30px rgba(0,0,0,.4)}
              h1{margin:0 0 4px;font-size:22px;color:#38bdf8}
              .ok{color:#4ade80}.warn{color:#fbbf24}
              td{padding:4px 12px 4px 0;color:#94a3b8}td+td{color:#e2e8f0}
              p{color:#64748b;font-size:13px}
            </style></head><body><div class="card">
            <h1>VentoryPrint <span class="ok">●</span></h1>
            <p>Agente local de impresión de ventoryPOS — v{{Version}}</p>
            <table>
              <tr><td>Estado</td><td class="ok">En ejecución</td></tr>
              <tr><td>Impresora</td><td>{{WebUtility.HtmlEncode(s?.PrinterName ?? "(sin configurar)")}}</td></tr>
              <tr><td>Caja</td><td>{{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(s?.CajaNombre) ? "—" : s!.CajaNombre)}}</td></tr>
              <tr><td>Host</td><td>{{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(s?.Host) ? "127.0.0.1" : s!.Host)}}:{{s?.Port ?? 9111}}</td></tr>
              <tr><td>Tokens</td><td class="{{(tokens.Count > 0 ? "ok" : "warn")}}">{{tokenText}}</td></tr>
            </table>
            <p>Para cambiar la configuración, abre la aplicación <b>VentoryPrint</b> desde el ícono de la bandeja (junto al reloj).</p>
            </div></body></html>
            """;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int status, string body)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=UTF-8";
        var buf = Encoding.UTF8.GetBytes(body);
        res.ContentLength64 = buf.Length;
        await res.OutputStream.WriteAsync(buf);
        res.OutputStream.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse res, string html)
    {
        res.StatusCode = 200;
        res.ContentType = "text/html; charset=UTF-8";
        var buf = Encoding.UTF8.GetBytes(html);
        res.ContentLength64 = buf.Length;
        await res.OutputStream.WriteAsync(buf);
        res.OutputStream.Close();
    }
}
