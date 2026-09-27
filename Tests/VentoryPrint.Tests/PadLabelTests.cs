using VentoryPrint.Printing;
using Xunit;

namespace VentoryPrint.Tests;

/// <summary>
/// Una línea "Etiqueta: valor" nunca debe superar el ancho del papel. Si lo
/// supera aunque sea en un carácter, la impresora lo pasa al siguiente renglón y
/// el ticket sale con una línea en blanco debajo (bug de Fecha/Cajero/Caja en 1.2.2).
/// </summary>
public class PadLabelTests
{
    // 32 = papel de 58 mm, 48 = papel de 80 mm.
    public static IEnumerable<object[]> Casos()
    {
        var etiquetas = new[] { "Fecha:", "Cajero:", "Caja:", "Turno:", "Fecha cierre:", "# Ventas:" };
        var valores   = new[] { "1", "Caja 1", "27/09/2026 03:45 PM", "Maria Fernanda Quispe Huaman" };
        foreach (var w in new[] { 32, 48 })
            foreach (var e in etiquetas)
                foreach (var v in valores)
                    yield return new object[] { e, v, w };
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void Ticket_no_supera_el_ancho_del_papel(string label, string value, int width)
    {
        var linea = TicketRenderer.PadLabel(label, value, width);
        AssertCabe(linea, label, value, width);
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public void CierreTurno_no_supera_el_ancho_del_papel(string label, string value, int width)
    {
        var linea = ShiftClosureRenderer.PadLabel(label, value, width);
        AssertCabe(linea, label, value, width);
    }

    [Fact]
    public void Etiqueta_queda_alineada_en_columna_fija()
    {
        // En 80 mm la columna de etiqueta mide 20; el valor arranca en la 21.
        Assert.Equal("Fecha:               27/09/2026", TicketRenderer.PadLabel("Fecha:", "27/09/2026", 48));
        Assert.Equal("Cajero:              Ana", TicketRenderer.PadLabel("Cajero:", "Ana", 48));
    }

    private static void AssertCabe(string linea, string label, string value, int width)
    {
        Assert.StartsWith(label, linea);
        Assert.EndsWith(value, linea);

        // Si etiqueta + valor caben, la línea entera debe caber. Si no caben, el
        // wrap es del valor largo (inevitable) y no debe agregarse relleno extra.
        if (label.Length + 1 + value.Length < width)
            Assert.True(linea.Length <= width, $"'{linea}' mide {linea.Length} > {width}");
        else
            Assert.Equal(label + " " + value, linea);
    }
}
