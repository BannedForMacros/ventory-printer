using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using VentoryPrint.Models;
using VentoryPrint.Services;

namespace VentoryPrint.Printing;

/// <summary>
/// Convierte un ShiftClosurePayload de ventoryPOS a bytes ESC/POS.
/// Imprime: cabecera del negocio, datos del turno, resumen de ventas,
/// consolidado por método de pago y consolidado de caja (efectivo).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShiftClosureRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Encoding _cp850;
    private readonly SettingsService _settings;

    public ShiftClosureRenderer(SettingsService settings)
    {
        _cp850 = Encoding.GetEncoding(850);
        _settings = settings;
    }

    private readonly record struct Layout(int Width)
    {
        public int Label => Math.Min(20, (int)(Width * 0.55));
        public int Value => Width - Label;
    }

    private Layout ReadLayout(int? anchoPapelMm)
    {
        if (anchoPapelMm is 58) return new Layout(32);
        if (anchoPapelMm is 80) return new Layout(48);

        var c = _settings.Load();
        if (c is null) return new Layout(48);
        return new Layout(c.TotalCols);
    }

    public byte[] Render(ShiftClosurePayload data)
    {
        var layout = ReadLayout(data.AnchoPapelMm);

        var b = new EscPosBuilder(_cp850).Init();

        RenderHeader(b, data, layout);
        RenderTurno(b, data, layout);
        RenderResumen(b, data, layout);
        RenderMetodosPago(b, data, layout);
        RenderCaja(b, data, layout);
        RenderFooter(b, data);

        b.FullCut();
        return b.ToArray();
    }

    private static void RenderHeader(EscPosBuilder b, ShiftClosurePayload d, Layout l)
    {
        b.JustifyCenter().TextSize(1, 1);

        if (!string.IsNullOrWhiteSpace(d.Logo))
        {
            try
            {
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

    private static void RenderTurno(EscPosBuilder b, ShiftClosurePayload d, Layout l)
    {
        var t = d.Turno;
        if (t is null) return;

        b.JustifyCenter().Bold(true);
        b.Line("CIERRE DE TURNO");
        b.Bold(false);

        b.JustifyLeft();
        b.Line(new string('-', l.Width));

        var turnoDisplay = string.Join(" - ", new[] { t.Nombre, t.Id }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(turnoDisplay)) b.Line(PadLabel("Turno:", turnoDisplay, l));
        if (!string.IsNullOrWhiteSpace(t.Cajero))     b.Line(PadLabel("Cajero:", t.Cajero, l));
        if (!string.IsNullOrWhiteSpace(t.Caja))       b.Line(PadLabel("Caja:", t.Caja, l));
        if (!string.IsNullOrWhiteSpace(t.FechaCierre))   b.Line(PadLabel("Fecha cierre:", t.FechaCierre, l));
        if (!string.IsNullOrWhiteSpace(t.FechaApertura)) b.Line(PadLabel("Apertura:", t.FechaApertura, l));
    }

    private static void RenderResumen(EscPosBuilder b, ShiftClosurePayload d, Layout l)
    {
        var r = d.Resumen;
        if (r is null) return;

        var sym = MonedaSym(r.Moneda);

        b.JustifyCenter().Bold(true);
        b.Line("RESUMEN DE VENTAS");
        b.Bold(false).JustifyLeft();
        b.Line(new string('-', l.Width));

        if (r.NumeroVentas is > 0)
            b.Line(PadLabel("# Ventas:", r.NumeroVentas.Value.ToString("N0", Inv), l));

        var mostrarIgv = d.Negocio?.MostrarIgv ?? false;

        if (mostrarIgv && r.Subtotal is { } st && st > 0 && st != r.Total)
            b.Line(PadAmount("SUBTOTAL:", sym + Money(st), l.Width));
        if (mostrarIgv && r.Igv is { } igv && igv > 0)
            b.Line(PadAmount("IGV:", sym + Money(igv), l.Width));
        if (r.Descuento is { } desc && desc > 0)
            b.Line(PadAmount("DESCUENTO:", "-" + sym + Money(desc), l.Width));

        b.Bold(true).TextSize(1, 2);
        b.Line(PadAmount("TOTAL:", sym + Money(r.Total), l.Width));
        b.TextSize(1, 1).Bold(false);
    }

    private static void RenderMetodosPago(EscPosBuilder b, ShiftClosurePayload d, Layout l)
    {
        if (d.MetodosPago.Count == 0) return;

        b.JustifyCenter().Bold(true);
        b.Line("CONSOLIDADO POR METODO DE PAGO");
        b.Bold(false).JustifyLeft();
        b.Line(new string('-', l.Width));

        // Columnas: método, cantidad, monto. El monto siempre alineado a la derecha.
        var colMethod = Math.Max(10, l.Width - 14); // 14 para " 99  S/ 9,999.99"
        var colCount  = 4;
        var colAmount = l.Width - colMethod - colCount;
        if (colAmount < 8) { colAmount = 8; colMethod = l.Width - colCount - colAmount; }

        var totalMetodos = 0m;
        foreach (var mp in d.MetodosPago)
        {
            totalMetodos += mp.Monto;
            var nombre = mp.Nombre.ToUpperInvariant();
            if (nombre.Length > colMethod) nombre = nombre[..colMethod];

            var countText = mp.Cantidad is > 0 ? mp.Cantidad.Value.ToString("N0", Inv) : "";
            var amountText = MonedaSym(d.Resumen?.Moneda) + Money(mp.Monto);

            b.Line(
                nombre.PadRight(colMethod) +
                countText.PadLeft(colCount) +
                amountText.PadLeft(colAmount));
        }

        b.Line(new string('-', l.Width));
        b.Bold(true);
        b.Line(PadAmount("TOTAL M.P.:", MonedaSym(d.Resumen?.Moneda) + Money(totalMetodos), l.Width));
        b.Bold(false);
    }

    private static void RenderCaja(EscPosBuilder b, ShiftClosurePayload d, Layout l)
    {
        var c = d.Caja;
        if (c is null) return;

        var sym = MonedaSym(d.Resumen?.Moneda);

        b.JustifyCenter().Bold(true);
        b.Line("CONSOLIDADO DE CAJA");
        b.Bold(false).JustifyLeft();
        b.Line(new string('-', l.Width));

        if (c.MontoApertura is { } aper)
            b.Line(PadAmount("Monto apertura:", sym + Money(aper), l.Width));
        if (c.VentasEfectivo is { } ventas)
            b.Line(PadAmount("Ventas efectivo:", sym + Money(ventas), l.Width));
        if (c.Entradas is { } ent && ent > 0)
            b.Line(PadAmount("Entradas:", sym + Money(ent), l.Width));
        if (c.Salidas is { } sal && sal > 0)
            b.Line(PadAmount("Salidas:", "-" + sym + Money(sal), l.Width));

        if (c.EfectivoEsperado is { } esp)
        {
            b.Line(new string('-', l.Width));
            b.Line(PadAmount("Efectivo esperado:", sym + Money(esp), l.Width));
        }
        if (c.EfectivoDeclarado is { } decl)
            b.Line(PadAmount("Efectivo declarado:", sym + Money(decl), l.Width));
        if (c.Diferencia is { } dif)
        {
            var sign = dif < 0 ? "-" : (dif > 0 ? "+" : "");
            b.Bold(true);
            b.Line(PadAmount("Diferencia:", sign + sym + Money(Math.Abs(dif)), l.Width));
            b.Bold(false);
        }
    }

    private static void RenderFooter(EscPosBuilder b, ShiftClosurePayload d)
    {
        b.Feed();
        b.JustifyCenter();
        b.Line(string.IsNullOrWhiteSpace(d.Pie) ? "Gracias por su preferencia" : d.Pie!);
    }

    // ------------------------------------------------------------- formateo

    private static string Money(decimal v) => v.ToString("0.00", Inv);

    private static string MonedaSym(string? moneda) =>
        string.Equals(moneda, "USD", StringComparison.OrdinalIgnoreCase) ? "$ " : "S/ ";

    private static string PadAmount(string label, string amount, int width)
    {
        if (label.Length + amount.Length >= width) return label + " " + amount;
        return label.PadRight(width - amount.Length) + amount;
    }

    private static string PadLabel(string label, string value, Layout l)
    {
        if (label.Length + 1 + value.Length >= l.Width) return label + " " + value;
        return label.PadRight(l.Label) + " " + value.PadRight(l.Value);
    }
}
