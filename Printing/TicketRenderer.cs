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
///
/// Hay dos diseños:
///  · Nota de venta / cotización / anticipo: tabla de 4 columnas (el de siempre).
///  · Comprobante electrónico SUNAT (boleta/factura): descripción en su línea,
///    "cant x precio" debajo, bloque Op. Gravada / IGV / TOTAL alineado a la
///    derecha e importe en letras. Se activa con documento.electronico.
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

    internal readonly record struct Layout(int Name, int Qty, int Pu, int Sub)
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
        var cpe = EsComprobanteElectronico(data.Documento);

        var b = new EscPosBuilder(_cp850).Init();

        // La gaveta se abre ANTES de imprimir para que la cajera dé el vuelto
        // sin esperar a que salga el papel.
        if (abrirCajon) b.Pulse();

        RenderHeader(b, data, layout, cpe);
        RenderDocumento(b, data, layout, cpe);
        RenderCliente(b, data, layout, cpe);
        if (cpe)
        {
            RenderItemsCpe(b, data, layout);
            RenderTotalesCpe(b, data, layout);
        }
        else
        {
            b.Feed();
            RenderItems(b, data, layout);
            RenderTotales(b, data, layout);
        }
        RenderPago(b, data, layout, cpe);
        RenderFooter(b, data, layout);

        b.FullCut();
        return b.ToArray();
    }

    /// <summary>
    /// El POS marca documento.electronico. Si el POS es anterior a esa clave, se
    /// infiere del nombre ("BOLETA DE VENTA ELECTRÓNICA", "FACTURA ELECTRÓNICA").
    /// </summary>
    internal static bool EsComprobanteElectronico(DocumentoInfo? doc)
    {
        if (doc is null) return false;
        if (doc.Electronico) return true;
        var t = (doc.Tipo ?? "").ToUpperInvariant();
        return t.Contains("ELECTR") && (t.Contains("BOLETA") || t.Contains("FACTURA"));
    }

    private static void RenderHeader(EscPosBuilder b, TicketPayload d, Layout l, bool cpe)
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
            // En un comprobante SUNAT el RUC del emisor es obligatorio, se muestre o no en notas.
            if ((n.MostrarRuc || cpe) && !string.IsNullOrWhiteSpace(n.Ruc)) b.Line("RUC: " + n.Ruc);
            if (!string.IsNullOrWhiteSpace(n.Direccion)) b.Line(n.Direccion);
            if (!string.IsNullOrWhiteSpace(n.Telefono))  b.Line("Telf: " + n.Telefono);
        }
        b.Feed();
    }

    private static void RenderDocumento(EscPosBuilder b, TicketPayload d, Layout l, bool cpe)
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
        // "Fecha de emision:" + fecha con hora no cabe en 58mm (32 cols): ahí, "Emision:".
        var etqFecha = !cpe ? "Fecha:" : (l.Width >= 40 ? "Fecha de emision:" : "Emision:");
        if (!string.IsNullOrWhiteSpace(doc.Fecha))    b.Line(PadLabel(etqFecha, doc.Fecha, l.Width));
        if (!string.IsNullOrWhiteSpace(doc.Vendedor)) b.Line(PadLabel("Cajero:", doc.Vendedor, l.Width));
        if (!string.IsNullOrWhiteSpace(doc.Caja))     b.Line(PadLabel("Caja:", doc.Caja, l.Width));
        RenderExtras(b, doc.Extra);
    }

    private static void RenderCliente(EscPosBuilder b, TicketPayload d, Layout l, bool cpe)
    {
        var c = d.Cliente;
        b.JustifyLeft();

        // Columna de etiquetas: 10 en notas ("Cliente:  "), 11 en comprobantes ("Direccion: ").
        var col = cpe ? 11 : 10;
        var nombre = string.IsNullOrWhiteSpace(c?.Nombre) ? "Cliente Varios" : c!.Nombre;
        foreach (var ln in LineasConEtiqueta("Cliente:", nombre, col, l.Width)) b.Line(ln);

        if (!string.IsNullOrWhiteSpace(c?.Doc))
        {
            // El POS manda "RUC 20605105514" / "DNI 12345678": en comprobante la
            // etiqueta es el tipo de documento, como en cualquier factura.
            var (etq, val) = cpe ? SepararDocumento(c!.Doc) : ("Doc:", c!.Doc);
            foreach (var ln in LineasConEtiqueta(etq, val, col, l.Width)) b.Line(ln);
        }
        if (!string.IsNullOrWhiteSpace(c?.Telefono))
            foreach (var ln in LineasConEtiqueta("Celular:", c!.Telefono, col, l.Width)) b.Line(ln);
        if (!string.IsNullOrWhiteSpace(c?.Direccion))
            foreach (var ln in LineasConEtiqueta(cpe ? "Direccion:" : "Direc:", c!.Direccion, col, l.Width)) b.Line(ln);
        RenderExtras(b, c?.Extra);
    }

    /// <summary>"RUC 20605105514" → ("RUC:", "20605105514"). Si no trae tipo, "Doc:".</summary>
    internal static (string etiqueta, string valor) SepararDocumento(string doc)
    {
        var s = doc.Trim();
        var i = s.IndexOf(' ');
        if (i is > 0 and <= 4 && !char.IsDigit(s[0]))
            return (s[..i].ToUpperInvariant() + ":", s[(i + 1)..].Trim());
        return ("Doc:", s);
    }

    /// <summary>Líneas libres que el POS ya formateó; el agente las imprime tal cual.</summary>
    private static void RenderExtras(EscPosBuilder b, List<string>? extras)
    {
        if (extras is null) return;
        foreach (var linea in extras)
            if (!string.IsNullOrWhiteSpace(linea)) b.Line(linea);
    }

    // ------------------------------------------------------- nota de venta

    private static void RenderItems(EscPosBuilder b, TicketPayload d, Layout l)
    {
        b.JustifyLeft();
        b.Line(new string('-', l.Width));
        b.Line(FilaTabla("Producto", "Cant.", "P.U.", "Impte.", l));
        b.Line(new string('-', l.Width));

        foreach (var it in d.Items)
        {
            // Producto + unidad ("Cemento Sol x BOLSA")
            var nombre = string.IsNullOrWhiteSpace(it.Unidad) ? it.Desc : $"{it.Desc} x {it.Unidad}";
            var fila = FilaTabla(nombre, Cant(it.Cant), Money(it.Precio), Money(it.Importe), l, out var resto);
            b.Line(fila);
            foreach (var extra in resto) b.Line(extra);
        }

        b.Line(new string('-', l.Width));
    }

    internal static string FilaTabla(string nombre, string cant, string pu, string sub, Layout l)
        => FilaTabla(nombre, cant, pu, sub, l, out _);

    /// <summary>
    /// Fila de la tabla con un espacio garantizado entre columnas numéricas. Antes
    /// cada número se rellenaba justo a su ancho y, si lo llenaba ("2900.00" en 7),
    /// pegaba con el vecino: "1" + "2900.00" se leía "12900.00". Si un número no
    /// cabe en su columna, cede espacio la columna del nombre, nunca la línea.
    /// </summary>
    internal static string FilaTabla(string nombre, string cant, string pu, string sub, Layout l, out List<string> resto)
    {
        var numeros = " " + cant.PadLeft(l.Qty - 1) + " " + pu.PadLeft(l.Pu - 1) + " " + sub.PadLeft(l.Sub - 1);
        var anchoNombre = Math.Max(1, l.Width - numeros.Length);

        var lineas = WordWrap(nombre, anchoNombre);
        resto = lineas.Skip(1).Select(x => x.PadRight(anchoNombre)).ToList();
        return lineas[0].PadRight(anchoNombre) + numeros;
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

    // ------------------------------------------------ comprobante electrónico

    /// <summary>
    /// Ítems del comprobante, como en una factura: la descripción en su línea y
    /// debajo los números bajo sus títulos, así "1" y "2900.00" nunca se leen juntos.
    /// <code>
    /// Descripcion
    ///    Cant. Unid.          P.Unit.        Importe
    /// ------------------------------------------------
    /// SERVICIO DE DESARROLLO DE SOFTWARE
    ///     1.00 Servicio       2,900.00       2,900.00
    /// </code>
    /// </summary>
    private static void RenderItemsCpe(EscPosBuilder b, TicketPayload d, Layout l)
    {
        b.JustifyLeft();
        b.Line(new string('-', l.Width));
        b.Line("Descripcion");
        b.Line(FilaItemCpe("Cant.", "Unid.", "P.Unit.", "Importe", l.Width));
        b.Line(new string('-', l.Width));

        foreach (var it in d.Items)
        {
            var unidad = (it.Unidad ?? "").Trim();
            // En 58mm no hay columna de unidad: va junto a la descripción.
            var desc = l.Width < 40 && unidad.Length > 0 ? $"{it.Desc} x {unidad}" : it.Desc;
            foreach (var ln in WordWrap(desc, l.Width)) b.Line(ln);
            b.Line(FilaItemCpe(it.Cant.ToString("0.00", Inv), unidad, MoneyMiles(it.Precio), MoneyMiles(it.Importe), l.Width));
        }

        b.Line(new string('-', l.Width));
    }

    /// <summary>
    /// Fila numérica del comprobante. 80mm: Cant.(8) Unid.(9) P.Unit.(12) Importe(resto).
    /// 58mm: Cant.(7) P.Unit.(11) Importe(resto), sin unidad. Si un número desborda su
    /// columna, la fila cae a "detalle ... importe" antes que partirse en el papel.
    /// </summary>
    internal static string FilaItemCpe(string cant, string unidad, string pu, string importe, int width)
    {
        string fila;
        if (width >= 40)
        {
            var u = unidad.Length > 9 ? unidad[..9] : unidad;
            fila = cant.PadLeft(8) + " " + u.PadRight(9) + pu.PadLeft(12) + importe.PadLeft(width - 30);
        }
        else
        {
            fila = cant.PadLeft(7) + pu.PadLeft(11) + importe.PadLeft(width - 18);
        }
        if (fila.Length <= width) return fila;

        var detalle = "  " + cant + (unidad.Length > 0 && width >= 40 ? " " + unidad : "") + " x " + pu;
        return PadAmount(detalle, importe, width);
    }

    private static void RenderTotalesCpe(EscPosBuilder b, TicketPayload d, Layout l)
    {
        var t = d.Totales;
        if (t is null) return;

        var sym = MonedaSym(t.Moneda).Trim(); // "S/"
        b.JustifyLeft();

        var igv = t.Igv ?? 0m;
        // POS anterior al desglose: la base gravada se deduce del total.
        var gravada = t.Gravada ?? (igv > 0 ? t.Total - igv : (decimal?)null);

        if (t.Descuento is { } desc && desc > 0)
            b.Line(FilaDerecha("Descuento:", sym, "-" + MoneyMiles(desc), l.Width));
        if (gravada is { } g && g > 0)
            b.Line(FilaDerecha("Op. Gravada:", sym, MoneyMiles(g), l.Width));
        if (t.Exonerada is { } ex && ex > 0)
            b.Line(FilaDerecha("Op. Exonerada:", sym, MoneyMiles(ex), l.Width));
        if (t.Inafecta is { } ina && ina > 0)
            b.Line(FilaDerecha("Op. Inafecta:", sym, MoneyMiles(ina), l.Width));
        if (igv > 0)
        {
            var tasa = (t.IgvTasa ?? 18m).ToString("0.##", Inv);
            b.Line(FilaDerecha($"IGV ({tasa}%):", sym, MoneyMiles(igv), l.Width));
        }

        b.Bold(true);
        b.Line(FilaDerecha("TOTAL:", sym, MoneyMiles(t.Total), l.Width));
        b.Bold(false);

        if (!string.IsNullOrWhiteSpace(t.EnLetras))
            foreach (var ln in WordWrap(t.EnLetras, l.Width)) b.Line(ln);
    }

    /// <summary>
    /// Bloque de totales estilo comprobante SUNAT: etiqueta alineada a la
    /// derecha, el símbolo de moneda en una columna fija y el importe alineado
    /// a la derecha en 10 caracteres ("999,999.99"):
    /// <code>
    ///                        Op. Gravada: S/   2,528.81
    ///                          IGV (18%): S/     455.19
    ///                              TOTAL: S/   2,984.00
    /// </code>
    /// </summary>
    internal static string FilaDerecha(string label, string sym, string amount, int width)
    {
        const int colImporte = 10;
        var colEtiqueta = width - 1 - sym.Length - colImporte;
        if (label.Length > colEtiqueta || amount.Length > colImporte)
            return PadAmount(label, sym + " " + amount, width);
        return label.PadLeft(colEtiqueta) + " " + sym + amount.PadLeft(colImporte);
    }

    // ------------------------------------------------------------- comunes

    private static void RenderPago(EscPosBuilder b, TicketPayload d, Layout l, bool cpe)
    {
        var p = d.Pago;
        if (p is null) return;

        var sym = MonedaSym(d.Totales?.Moneda);
        if (!string.IsNullOrWhiteSpace(p.Metodo))
            b.Line(cpe
                ? PadAmount("Forma de pago:", p.Metodo, l.Width)
                : PadAmount("PAGO:", p.Metodo.ToUpperInvariant(), l.Width));
        if (p.Recibido is { } rec && rec > 0)
            b.Line(PadAmount(cpe ? "Recibido:" : "RECIBIDO:", sym + Money(rec), l.Width));
        if (p.Vuelto is { } vto && vto > 0)
            b.Line(PadAmount(cpe ? "Vuelto:" : "VUELTO:", sym + Money(vto), l.Width));
    }

    private static void RenderFooter(EscPosBuilder b, TicketPayload d, Layout l)
    {
        b.Feed();
        b.JustifyCenter();

        if (!string.IsNullOrWhiteSpace(d.Qr))
        {
            b.QrCode(d.Qr!);
            b.Feed();
        }

        // Cada línea del pie se parte por palabras: la impresora cortaba a lo
        // bruto ("comprobante ele / ctrónico").
        var pie = string.IsNullOrWhiteSpace(d.Pie) ? "Gracias por su preferencia" : d.Pie!;
        foreach (var parrafo in pie.Split('\n'))
            foreach (var ln in WordWrap(parrafo.TrimEnd('\r'), l.Width))
                b.Line(ln);
    }

    // ------------------------------------------------------------- formateo

    private static string Money(decimal v) => v.ToString("0.00", Inv);

    /// <summary>Con separador de miles, para el comprobante: 2,900.00.</summary>
    private static string MoneyMiles(decimal v) => v.ToString("#,##0.00", Inv);

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

    /// <summary>
    /// "Fecha:      27/09/2026". El valor NO se rellena a la derecha: antes se
    /// rellenaba hasta width-labelWidth y, con el espacio separador, la línea
    /// quedaba en width+1 caracteres; ese carácter sobrante pasaba al siguiente
    /// renglón y dejaba una línea en blanco debajo de Fecha, Cajero y Caja.
    /// </summary>
    internal static string PadLabel(string label, string value, int width)
    {
        if (label.Length + 1 + value.Length >= width) return label + " " + value;
        // Columna de etiqueta fija (20 en 80mm, 17 en 58mm), pero si el valor es
        // largo se acorta lo justo para que la línea completa quepa en el papel.
        var labelWidth = Math.Min(20, (int)(width * 0.55));
        labelWidth = Math.Max(label.Length, Math.Min(labelWidth, width - 1 - value.Length));
        return label.PadRight(labelWidth) + " " + value;
    }

    /// <summary>
    /// "Cliente:  THE ANKAWA GLOBAL GROUP SOCIEDAD" y las siguientes líneas con
    /// sangría bajo el valor, en vez de dejar que la impresora corte donde caiga.
    /// </summary>
    internal static List<string> LineasConEtiqueta(string label, string value, int labelCol, int width)
    {
        var col = Math.Max(labelCol, label.Length + 1);
        var ancho = width - col;
        if (ancho < 8) return new List<string> { label + " " + value };

        var partes = WordWrap(value, ancho);
        var result = new List<string> { label.PadRight(col) + partes[0] };
        for (var i = 1; i < partes.Count; i++) result.Add(new string(' ', col) + partes[i]);
        return result;
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
