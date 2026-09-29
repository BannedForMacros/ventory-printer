using System.Runtime.Versioning;
using System.Text.Json;
using VentoryPrint.Models;
using VentoryPrint.Printing;
using VentoryPrint.Services;

namespace VentoryPrint.Cli;

/// <summary>
/// Imprime un ticket por bloques en la impresora configurada, sin pasar por el
/// POS: <c>VentoryPrint.exe --test-print-bloques [ticket.json]</c>. Sin archivo
/// imprime una muestra con todos los bloques, y la banda y el recuadro en sus
/// dos modos (imagen y texto) para compararlos en el papel de esa ticketera.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TestBloquesCli
{
    public static int Run(string? archivo)
    {
        var settings = new SettingsService();
        var s = settings.Load();
        if (s is null || string.IsNullOrWhiteSpace(s.PrinterName))
        {
            Console.WriteLine("No hay impresora configurada. Abre VentoryPrint.exe y configura primero.");
            return 1;
        }

        try
        {
            var json = string.IsNullOrWhiteSpace(archivo) ? Muestra : File.ReadAllText(archivo);
            var ticket = JsonSerializer.Deserialize<TicketPayload>(json, TicketJson.Options);
            if (ticket?.Bloques is not { Count: > 0 })
            {
                Console.WriteLine("El JSON no trae bloques.");
                return 1;
            }

            var bytes = new TicketRenderer(settings).Render(ticket);
            RawPrinterHelper.SendBytesToPrinter(s.PrinterName, bytes, "Ventory Test bloques");
            Console.WriteLine($"Ticket por bloques enviado a '{s.PrinterName}' ({ticket.Bloques.Count} bloques).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error al imprimir: " + ex.Message);
            return 1;
        }
    }

    public const string Muestra = """
    {
      "bloques": [
        { "tipo": "texto", "texto": "PRUEBA DE BLOQUES", "alinear": "centro", "tamano": "ancho", "negrita": true },
        { "tipo": "texto", "texto": "VentoryPrint: ticket por plantilla", "alinear": "centro" },
        { "tipo": "linea" },
        { "tipo": "pares", "items": [
          { "etiqueta": "Cajero:", "valor": "CAJERA PRUEBA" },
          { "etiqueta": "Cel. cajero:", "valor": "974 123 456" },
          { "etiqueta": "Entrega programada:", "valor": "Lun 28/09/2026 - 9:00 a. m." }
        ] },
        { "tipo": "linea" },
        { "tipo": "texto", "texto": "1) Recuadro y banda como IMAGEN", "negrita": true },
        { "tipo": "recuadro", "lineas": [
          { "texto": "TELÉFONO" },
          { "texto": "979 555 012", "tamano": "alto", "negrita": true },
          { "separador": true },
          { "texto": "DIRECCIÓN" },
          { "texto": "Calle Los Cedros 245, Pomalca", "tamano": "alto", "negrita": true }
        ] },
        { "tipo": "recuadro", "alinear": "centro", "lineas": [ { "texto": "ENVÍO A OBRA", "tamano": "grande", "negrita": true } ] },
        { "tipo": "banda", "lineas": [
          { "texto": "POR CANCELAR", "tamano": "alto", "negrita": true },
          { "texto": "COBRAR EN OBRA", "negrita": true },
          { "texto": "S/ 1,110.10", "tamano": "grande", "negrita": true }
        ] },
        { "tipo": "espacio" },
        { "tipo": "texto", "texto": "2) Los mismos como TEXTO", "negrita": true },
        { "tipo": "recuadro", "modo": "texto", "lineas": [
          { "texto": "TELEFONO" },
          { "texto": "979 555 012", "tamano": "alto", "negrita": true },
          { "separador": true },
          { "texto": "DIRECCION" },
          { "texto": "Calle Los Cedros 245, Pomalca", "tamano": "alto", "negrita": true }
        ] },
        { "tipo": "recuadro", "modo": "texto", "alinear": "centro", "lineas": [ { "texto": "ENVIO A OBRA", "tamano": "grande", "negrita": true } ] },
        { "tipo": "banda", "modo": "texto", "lineas": [
          { "texto": "POR CANCELAR", "tamano": "alto", "negrita": true },
          { "texto": "COBRAR EN OBRA", "negrita": true },
          { "texto": "S/ 1,110.10", "tamano": "grande", "negrita": true }
        ] },
        { "tipo": "linea" },
        { "tipo": "tabla",
          "columnas": [ { "titulo": "Producto", "flexible": true }, { "titulo": "Vendida" }, { "titulo": "Entregado" }, { "titulo": "Pendiente" } ],
          "filas": [ [ "Cemento Sol", "100", "40", "60" ], [ "Fierro 1/2", "5", "5", "0" ] ] },
        { "tipo": "linea" },
        { "tipo": "tabla", "estilo": "dosLineas",
          "columnas": [ { "titulo": "", "flexible": true }, { "titulo": "Cant" }, { "titulo": "P.U." }, { "titulo": "Importe" } ],
          "filas": [ [ "Cemento Azul Pacasmayo Antisalitre x Und", "20", "35.00", "700.00" ] ] },
        { "tipo": "linea" },
        { "tipo": "pares", "estilo": "extremos", "items": [
          { "etiqueta": "IGV (18%):", "valor": "S/ 106.78" },
          { "etiqueta": "TOTAL:", "valor": "S/ 700.00", "negrita": true, "tamano": "alto" }
        ] },
        { "tipo": "espacio" },
        { "tipo": "qr", "datos": "https://ventorypos.macsoftperu.com", "escala": 5 },
        { "tipo": "texto", "texto": "Fin de la prueba", "alinear": "centro" }
      ]
    }
    """;
}
