using System.Text;
using System.Text.RegularExpressions;
using VentoryPrint.Models;
using VentoryPrint.Printing;
using VentoryPrint.Services;
using Xunit;

namespace VentoryPrint.Tests;

/// <summary>
/// Renderiza un comprobante completo y lo convierte a texto plano (sin códigos
/// ESC/POS) para comprobar el diseño entero: ninguna línea supera el ancho y
/// las piezas clave están donde deben. Deja la vista previa en %TEMP% para
/// mirarla a ojo (VENTORYPRINT_PREVIEW_DIR la redirige).
/// </summary>
public class VistaPreviaTests
{
    private static TicketPayload Factura() => new()
    {
        Negocio = new NegocioInfo { Nombre = "MacSoft Importaciones", Ruc = "20614911051", Direccion = "Av. Balta 850", Telefono = "+51 974 123 456", MostrarRuc = false },
        Documento = new DocumentoInfo { Tipo = "FACTURA ELECTRÓNICA", Numero = "F001-00000004", Fecha = "18/09/2026 05:26 PM", Electronico = true },
        Cliente = new ClienteInfo { Nombre = "THE ANKAWA GLOBAL GROUP SOCIEDAD ANONIMA CERRADA", Doc = "RUC 20605105514", Direccion = "MZA. D LOTE 1 URB. LA FLORIDA" },
        Items = new()
        {
            new TicketItem { Cant = 1, Desc = "SERVICIO DE DESARROLLO DE SOFTWARE - MESA DE PARTES", Precio = 2900m, Importe = 2900m, Unidad = "Servicio" },
            new TicketItem { Cant = 12, Desc = "CAFE AMERICANO", Precio = 7m, Importe = 84m, Unidad = "UNID" },
        },
        Totales = new TotalesInfo { Subtotal = 2984m, Igv = 455.19m, Descuento = 0, Total = 2984m, Moneda = "PEN", Gravada = 2528.81m, Exonerada = 0, Inafecta = 0, IgvTasa = 18, EnLetras = "SON: DOS MIL NOVECIENTOS OCHENTA Y CUATRO CON 00/100 SOLES" },
        Pago = new PagoInfo { Metodo = "Transferencia" },
        Pie = "Representación impresa de la Factura Electrónica\nConsulte en www.sunat.gob.pe\nRef. interna: V-0006\nGracias por su preferencia",
    };

    private static TicketPayload NotaDeVenta()
    {
        var p = Factura();
        p.Documento = new DocumentoInfo { Tipo = "NOTA DE VENTA", Numero = "V-0007", Fecha = "27/09/2026 01:27 PM", Vendedor = "Jesús", Caja = "Caja 2 — Mostrador" };
        p.Totales = new TotalesInfo { Subtotal = 2984m, Igv = 455.19m, Total = 2984m, Moneda = "PEN" };
        p.Pie = "Gracias por su preferencia";
        return p;
    }

    [Theory]
    [InlineData(80, 48)]
    [InlineData(58, 32)]
    public void Comprobante_electronico_cabe_y_tiene_el_desglose_sunat(int papel, int ancho)
    {
        var lineas = Render(Factura(), papel, $"factura-{papel}mm.txt");

        Assert.All(lineas, ln => Assert.True(ln.Length <= ancho, $"'{ln}' mide {ln.Length} > {ancho}"));
        Assert.Contains(lineas, ln => ln.StartsWith("RUC: 20614911051"));          // RUC emisor obligatorio en CPE
        Assert.Contains("FACTURA ELECTRONICA", lineas);                              // sin tilde rota
        Assert.Contains(lineas, ln => ln.StartsWith(ancho >= 40 ? "Fecha de emision:" : "Emision:"));
        Assert.Contains(lineas, ln => ln.StartsWith("RUC:        20605105514") || ln.StartsWith("RUC:       20605105514"));
        Assert.Contains(lineas, ln => ln.Contains("1.00 Servicio x 2,900.00") || ln.Contains("1.00 x 2,900.00"));
        Assert.Contains(lineas, ln => ln.EndsWith(" 2,900.00"));
        Assert.Contains(lineas, ln => ln.EndsWith("Op. Gravada: S/  2,528.81"));
        Assert.Contains(lineas, ln => ln.EndsWith("IGV (18%): S/    455.19"));
        Assert.Contains(lineas, ln => ln.EndsWith("TOTAL: S/  2,984.00"));
        Assert.Contains(lineas, ln => ln.StartsWith("SON: DOS MIL"));
        Assert.DoesNotContain(lineas, ln => ln.Contains("Hash"));
        Assert.DoesNotContain(lineas, ln => ln.Contains("12,900") || ln.Contains("12900"));
    }

    [Fact]
    public void Nota_de_venta_conserva_su_tabla_de_cuatro_columnas()
    {
        var lineas = Render(NotaDeVenta(), 80, "nota-80mm.txt");

        Assert.All(lineas, ln => Assert.True(ln.Length <= 48, $"'{ln}' mide {ln.Length} > 48"));
        Assert.Contains(lineas, ln => ln.StartsWith("Producto") && ln.Contains("Cant.") && ln.EndsWith("Impte."));
        Assert.Contains(lineas, ln => ln.StartsWith("SERVICIO DE DESARROLLO") && ln.Contains(" 1 2900.00 ") && ln.EndsWith(" 2900.00"));
        Assert.Contains(lineas, ln => ln.StartsWith("Caja:") && ln.EndsWith("Caja 2 - Mostrador"));
        Assert.Contains(lineas, ln => ln.StartsWith("TOTAL:") && ln.EndsWith("S/ 2984.00"));
        Assert.DoesNotContain(lineas, ln => ln.StartsWith("Op. Gravada"));
    }

    // ------------------------------------------------------------------ util

    private static List<string> Render(TicketPayload p, int papelMm, string archivo)
    {
        p.AnchoPapelMm = papelMm;
        var bytes = new TicketRenderer(new SettingsService()).Render(p);
        var texto = ATexto(bytes);

        var dir = Environment.GetEnvironmentVariable("VENTORYPRINT_PREVIEW_DIR") ?? Path.GetTempPath();
        File.WriteAllText(Path.Combine(dir, "ventoryprint-preview-" + archivo), texto, new UTF8Encoding(false));

        return texto.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
    }

    /// <summary>Quita los comandos ESC/POS que usa el agente y decodifica CP850.</summary>
    private static string ATexto(byte[] bytes)
    {
        var s = Encoding.Latin1.GetString(bytes); // 1 byte = 1 char, para poder usar regex
        // \u001B y no \x1B: en C# "\x1Ba" se lee como el carácter U+01BA, no como ESC + 'a'.
        s = Regex.Replace(s, "\u001B@|\u001Ba.|\u001BE.|\u001Bp...|\u001D!.|\u001DVB.|\u001DV.|\u001Bt.", "", RegexOptions.Singleline);
        s = Regex.Replace(s, "\u001D\\(k[\\s\\S]*?(?=\n)", "[QR]");
        s = Regex.Replace(s, "\u001Dv0[\\s\\S]*?(?=\n)", "[LOGO]");
        var raw = Encoding.Latin1.GetBytes(s);
        return Encoding.GetEncoding(850).GetString(raw);
    }
}
