using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using VentoryPrint.Models;
using VentoryPrint.Services;

namespace VentoryPrint.Printing;

/// <summary>
/// Convierte un TicketPayload de ventoryPOS a bytes ESC/POS.
/// Las 4 columnas (Producto, Cantidad, P.U., Importe) se leen desde
/// SettingsService en cada Render para que la UI pueda cambiarlas en caliente.
/// 80mm típico = 26+5+8+9 = 48 cols. 58mm típico = 14+4+6+8 = 32 cols.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TicketRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Encoding _cp850;
    private readonly SettingsService _settings;

    public TicketRenderer(SettingsService settings)
    {
        _cp850 = Encoding.GetEncoding(850);
        _settings = settings;
    }

    private readonly record struct Layout(int Name, int Qty, int Pu, int Sub)
    {
        public int Width => Name + Qty + Pu + Sub;
    }

    private Layout ReadLayout(int? anchoPapelMm)
    {
        // El payload puede pedir un ancho explícito (58/80); si no, manda la config local.
        if (anchoPapelMm is 58) return new Layout(14, 4, 6, 8);
        if (anchoPapelMm is 80) return new Layout(26, 5, 8, 9);

        var c = _settings.Load();
        if (c is null) return new Layout(26, 5, 8, 9);
        return new Layout(c.ColName, c.ColQty, c.ColPu, c.ColSub);
    }

    public byte[] Render(TicketPayload data)
    {
        var layout = ReadLayout(data.AnchoPapelMm);
        var abrirCajon = data.AbrirCajon || (_settings.Load()?.AbrirCajonSiempre ?? false);

        var b = new EscPosBuilder(_cp850).Init();

        // La gaveta se abre ANTES de imprimir para que la cajera dé el vuelto
        // sin esperar a que salga el papel.
        if (abrirCajon) b.Pulse();

        RenderHeader(b, data, layout);
        RenderDocumento(b, data, layout);
        RenderCliente(b, data);
        b.Feed();
        RenderItems(b, data, layout);
        RenderTotales(b, data, layout);
        RenderPago(b, data, layout);
        RenderFooter(b, data);

        b.FullCut();
        return b.ToArray();
    }

    private static void RenderHeader(EscPosBuilder b, TicketPayload d, Layout l)
    {
        b.JustifyCenter().TextSize(1, 1);

        // Logo del negocio (opcional). Un fallo decodificando nunca rompe la impresión.
        if (!string.IsNullOrWhiteSpace(d.Logo))
        {
            try
            {
                // Ancho de impresión típico: 58mm ≈ 384 puntos, 80mm ≈ 576 puntos.
                var dots = l.Width <= 33 ? 384 : 576;
                var raster = ImageEscPos.FromDataUri(d.Logo, dots, d.Negocio?.LogoEscala ?? 100);
                if (raster is { } r)
                {
                    b.RasterImage(r.Packed, r.WidthBytes, r.Height);
                    b.Feed();
                }
            }
            catch { /* sin logo si algo falla */ }
        }

        var n = d.Negocio;
        if (n is not null)
        {
            if (!string.IsNullOrWhiteSpace(n.Nombre))
                b.Bold(true).TextSize(2, 1).Line(n.Nombre.ToUpperInvariant()).TextSize(1, 1).Bold(false);
            if (n.MostrarRuc && !string.IsNullOrWhiteSpace(n.Ruc)) b.Line("RUC: " + n.Ruc);
            if (!string.IsNullOrWhiteSpace(n.Direccion)) b.Line(n.Direccion);
            if (!string.IsNullOrWhiteSpace(n.Telefono))  b.Line("Telf: " + n.Telefono);
        }
        b.Feed();
    }

    private static void RenderDocumento(EscPosBuilder b, TicketPayload d, Layout l)
    {
        var doc = d.Documento;
        if (doc is null) return;

        b.JustifyCenter().Bold(true);
        var tipo = string.IsNullOrWhiteSpace(doc.Tipo) ? "NOTA DE VENTA" : doc.Tipo.ToUpperInvariant();
        b.Line(tipo);
        var numero = string.Join("-", new[] { doc.Serie, doc.Numero }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(numero)) b.Line(numero);
        b.Bold(false);

        b.JustifyLeft();
        b.Line(new string('-', l.Width));
        if (!string.IsNullOrWhiteSpace(doc.Fecha))    b.Line(PadLabel("Fecha:", doc.Fecha, l.Width));
        if (!string.IsNullOrWhiteSpace(doc.Vendedor)) b.Line(PadLabel("Cajero:", doc.Vendedor, l.Width));
        if (!string.IsNullOrWhiteSpace(doc.Caja))     b.Line(PadLabel("Caja:", doc.Caja, l.Width));
        RenderExtras(b, doc.Extra);
    }

    private static void RenderCliente(EscPosBuilder b, TicketPayload d)
    {
        var c = d.Cliente;
        b.JustifyLeft();
        b.Line("Cliente:  " + (string.IsNullOrWhiteSpace(c?.Nombre) ? "Cliente Varios" : c!.Nombre));
        if (!string.IsNullOrWhiteSpace(c?.Doc))       b.Line("Doc:      " + c!.Doc);
        if (!string.IsNullOrWhiteSpace(c?.Telefono))  b.Line("Celular:  " + c!.Telefono);
        if (!string.IsNullOrWhiteSpace(c?.Direccion)) b.Line("Direc:    " + c!.Direccion);
        RenderExtras(b, c?.Extra);
    }

    /// <summary>Líneas libres que el POS ya formateó; el agente las imprime tal cual.</summary>
    private static void RenderExtras(EscPosBuilder b, List<string>? extras)
    {
        if (extras is null) return;
        foreach (var linea in extras)
            if (!string.IsNullOrWhiteSpace(linea)) b.Line(linea);
    }

    private static void RenderItems(EscPosBuilder b, TicketPayload d, Layout l)
    {
        b.JustifyLeft();
        b.Line(new string('-', l.Width));
        b.Line(
            "Producto".PadRight(l.Name) +
            "Cant.".PadLeft(l.Qty) +
            "P.U.".PadLeft(l.Pu) +
            "Impte.".PadLeft(l.Sub));
        b.Line(new string('-', l.Width));

        foreach (var it in d.Items)
        {
            // Producto + unidad ("Cemento Sol x BOLSA")
            var nombre = string.IsNullOrWhiteSpace(it.Unidad) ? it.Desc : $"{it.Desc} x {it.Unidad}";
            var lines = WordWrap(nombre, l.Name);
            var first = lines.Count > 0 ? lines[0] : "";

            b.Line(
                first.PadRight(l.Name) +
                Cant(it.Cant).PadLeft(l.Qty) +
                Money(it.Precio).PadLeft(l.Pu) +
                Money(it.Importe).PadLeft(l.Sub));

            for (var i = 1; i < lines.Count; i++)
                b.Line(lines[i].PadRight(l.Name));
        }

        b.Line(new string('-', l.Width));
    }

    private static void RenderTotales(EscPosBuilder b, TicketPayload d, Layout l)
    {
        var t = d.Totales;
        if (t is null) return;

        var sym = MonedaSym(t.Moneda);
        b.JustifyLeft();

        var conIgv = (d.Negocio?.MostrarIgv ?? false) && DocumentoLlevaIgv(d.Documento?.Tipo);

        if (conIgv && t.Subtotal is { } st && st > 0 && st != t.Total)
            b.Line(PadAmount("SUBTOTAL:", sym + Money(st), l.Width));
        if (conIgv && t.Igv is { } igv && igv > 0)
            b.Line(PadAmount("IGV:", sym + Money(igv), l.Width));
        if (t.Descuento is { } desc && desc > 0)
            b.Line(PadAmount("DESCUENTO:", "-" + sym + Money(desc), l.Width));

        b.Bold(true).TextSize(1, 2);
        b.Line(PadAmount("TOTAL:", sym + Money(t.Total), l.Width));
        b.TextSize(1, 1).Bold(false);
    }

    private static void RenderPago(EscPosBuilder b, TicketPayload d, Layout l)
    {
        var p = d.Pago;
        if (p is null) return;

        var sym = MonedaSym(d.Totales?.Moneda);
        if (!string.IsNullOrWhiteSpace(p.Metodo))
            b.Line(PadAmount("PAGO:", p.Metodo.ToUpperInvariant(), l.Width));
        if (p.Recibido is { } rec && rec > 0)
            b.Line(PadAmount("RECIBIDO:", sym + Money(rec), l.Width));
        if (p.Vuelto is { } vto && vto > 0)
            b.Line(PadAmount("VUELTO:", sym + Money(vto), l.Width));
    }

    private static void RenderFooter(EscPosBuilder b, TicketPayload d)
    {
        b.Feed();
        b.JustifyCenter();

        if (!string.IsNullOrWhiteSpace(d.Qr))
        {
            b.QrCode(d.Qr!);
            b.Feed();
        }

        b.Line(string.IsNullOrWhiteSpace(d.Pie) ? "Gracias por su preferencia" : d.Pie!);
    }

    // ------------------------------------------------------------- formateo

    private static string Money(decimal v) => v.ToString("0.00", Inv);

    /// <summary>Cantidad sin ceros muertos: 1 → "1", 0.5 → "0.5", 2.25 → "2.25".</summary>
    private static string Cant(decimal v) => v.ToString("0.##", Inv);

    /// <summary>
    /// Solo boletas y facturas (con o sin CPE) deben mostrar desglose de IGV.
    /// Nota de venta, cotización, entrega de anticipo, etc. no lo llevan.
    /// </summary>
    private static bool DocumentoLlevaIgv(string? tipo)
    {
        if (string.IsNullOrWhiteSpace(tipo)) return false;
        var t = tipo.ToUpperInvariant();
        return t.Contains("BOLETA") || t.Contains("FACTURA");
    }

    private static string MonedaSym(string? moneda) =>
        string.Equals(moneda, "USD", StringComparison.OrdinalIgnoreCase) ? "$ " : "S/ ";

    private static string PadAmount(string label, string amount, int width)
    {
        if (label.Length + amount.Length >= width) return label + " " + amount;
        return label.PadRight(width - amount.Length) + amount;
    }

    private static string PadLabel(string label, string value, int width)
    {
        var labelWidth = Math.Min(20, (int)(width * 0.55));
        var valueWidth = width - labelWidth;
        if (label.Length + 1 + value.Length >= width) return label + " " + value;
        return label.PadRight(labelWidth) + " " + value.PadRight(valueWidth);
    }

    /// <summary>
    /// Respeta palabras; si una palabra es más larga que el ancho, la corta en trozos
    /// de ese ancho para que nunca invada las columnas numéricas.
    /// </summary>
    internal static List<string> WordWrap(string text, int width)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(""); return result; }

        var words = new List<string>();
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.Length <= width) { words.Add(w); continue; }
            for (var i = 0; i < w.Length; i += width)
                words.Add(w.Substring(i, Math.Min(width, w.Length - i)));
        }

        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length == 0)
                current.Append(word);
            else if (current.Length + 1 + word.Length <= width)
                current.Append(' ').Append(word);
            else
            {
                result.Add(current.ToString());
                current.Clear().Append(word);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        if (result.Count == 0) result.Add("");
        return result;
    }
}
