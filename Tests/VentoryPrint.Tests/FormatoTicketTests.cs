using VentoryPrint.Models;
using VentoryPrint.Printing;
using Xunit;

namespace VentoryPrint.Tests;

/// <summary>
/// Formato de las líneas del ticket: lo que salió mal en papel el 27/09/2026
/// (columnas pegadas, "ELECTRaNICA", pie cortado a lo bruto) y el diseño de
/// comprobante electrónico.
/// </summary>
public class FormatoTicketTests
{
    // El ancho personalizado de 41 columnas con el que "1" + "2900.00" se leía "12900.00".
    private static readonly TicketRenderer.Layout L41 = new(22, 4, 7, 8);
    private static readonly TicketRenderer.Layout L48 = new(26, 5, 8, 9);
    private static readonly TicketRenderer.Layout L32 = new(14, 4, 6, 8);

    [Fact]
    public void Tabla_siempre_deja_un_espacio_entre_cantidad_precio_e_importe()
    {
        var fila = TicketRenderer.FilaTabla("SERVICIO DE DESARROLLO DE SOFTWARE", "1", "2900.00", "2900.00", L41);

        Assert.Equal(41, fila.Length);
        Assert.EndsWith(" 1 2900.00 2900.00", fila);
        Assert.DoesNotContain("12900.00", fila);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(41)]
    [InlineData(32)]
    public void Tabla_nunca_supera_el_ancho_del_papel(int ancho)
    {
        var l = ancho switch { 48 => L48, 41 => L41, _ => L32 };
        foreach (var (c, p, s) in new[] { ("1", "0.50", "0.50"), ("1500", "2900.00", "99999.00"), ("0.25", "12.5", "3.13") })
        {
            var fila = TicketRenderer.FilaTabla("Cemento Sol x BOLSA", c, p, s, l, out var resto);
            Assert.True(fila.Length <= ancho, $"'{fila}' mide {fila.Length} > {ancho}");
            Assert.All(resto, r => Assert.True(r.Length <= ancho));
            Assert.Contains($" {c} ", fila + " ");
        }
    }

    [Fact]
    public void Comprobante_pone_cantidad_por_precio_en_su_propia_linea()
    {
        var item = new TicketItem { Cant = 1, Desc = "SERVICIO DE DESARROLLO", Precio = 2900m, Importe = 2900m, Unidad = "Servicio" };
        Assert.Equal("  1.00 Servicio x 2,900.00", TicketRenderer.DetalleItemCpe(item));
    }

    [Fact]
    public void Bloque_de_totales_alinea_importes_en_columna_fija()
    {
        var a = TicketRenderer.FilaDerecha("Op. Gravada:", "S/", "2,457.63", 48);
        var b = TicketRenderer.FilaDerecha("IGV (18%):", "S/", "442.37", 48);
        var c = TicketRenderer.FilaDerecha("TOTAL:", "S/", "2,900.00", 48);

        Assert.Equal(48, a.Length);
        Assert.Equal(48, b.Length);
        Assert.Equal(48, c.Length);
        // El "S/" queda en la misma columna en todas las filas; las etiquetas
        // terminan pegadas a él y los importes se alinean por la derecha.
        Assert.Equal(a.IndexOf("S/"), b.IndexOf("S/"));
        Assert.Equal(a.IndexOf("S/"), c.IndexOf("S/"));
        Assert.EndsWith("Op. Gravada: S/  2,457.63", a);
        Assert.EndsWith(  "IGV (18%): S/    442.37", b);
        Assert.EndsWith(      "TOTAL: S/  2,900.00", c);
    }

    [Fact]
    public void Bloque_de_totales_cabe_en_58mm()
    {
        foreach (var etq in new[] { "Op. Gravada:", "Op. Exonerada:", "IGV (18%):", "TOTAL:" })
        {
            var fila = TicketRenderer.FilaDerecha(etq, "S/", "12,345.67", 32);
            Assert.True(fila.Length <= 32, $"'{fila}' mide {fila.Length}");
        }
    }

    [Fact]
    public void Documento_del_cliente_se_rotula_con_su_tipo_en_comprobante()
    {
        Assert.Equal(("RUC:", "20605105514"), TicketRenderer.SepararDocumento("RUC 20605105514"));
        Assert.Equal(("DNI:", "12345678"), TicketRenderer.SepararDocumento("DNI 12345678"));
        Assert.Equal(("Doc:", "12345678"), TicketRenderer.SepararDocumento("12345678"));
    }

    [Fact]
    public void Nombre_largo_del_cliente_se_parte_con_sangria()
    {
        var lineas = TicketRenderer.LineasConEtiqueta("Cliente:", "THE ANKAWA GLOBAL GROUP SOCIEDAD ANONIMA CERRADA", 11, 48);

        Assert.Equal(2, lineas.Count);
        Assert.StartsWith("Cliente:   THE ANKAWA", lineas[0]);
        Assert.StartsWith("           ", lineas[1]); // sangría bajo el valor
        Assert.All(lineas, ln => Assert.True(ln.Length <= 48));
        Assert.Equal("THE ANKAWA GLOBAL GROUP SOCIEDAD ANONIMA CERRADA", string.Join(" ", lineas.Select(x => x.Substring(11).Trim())));
    }

    [Fact]
    public void Mayusculas_acentuadas_se_imprimen_sin_tilde_porque_CP437_no_las_tiene()
    {
        Assert.Equal("FACTURA ELECTRONICA", EscPosBuilder.Sanear("FACTURA ELECTRÓNICA"));
        Assert.Equal("BOLETA DE VENTA ELECTRONICA", EscPosBuilder.Sanear("BOLETA DE VENTA ELECTRÓNICA"));
        Assert.Equal("Caja 2 - Mostrador", EscPosBuilder.Sanear("Caja 2 — Mostrador"));
        // Las minúsculas y la eñe sí existen en la tabla de la impresora: se conservan.
        Assert.Equal("Representación impresa · Señor ¿qué?", EscPosBuilder.Sanear("Representación impresa · Señor ¿qué?"));
    }

    [Fact]
    public void Se_reconoce_el_comprobante_aunque_el_POS_no_mande_la_bandera()
    {
        Assert.True(TicketRenderer.EsComprobanteElectronico(new DocumentoInfo { Tipo = "FACTURA ELECTRÓNICA" }));
        Assert.True(TicketRenderer.EsComprobanteElectronico(new DocumentoInfo { Tipo = "Nota de venta", Electronico = true }));
        Assert.False(TicketRenderer.EsComprobanteElectronico(new DocumentoInfo { Tipo = "NOTA DE VENTA" }));
        Assert.False(TicketRenderer.EsComprobanteElectronico(new DocumentoInfo { Tipo = "COTIZACIÓN / PROFORMA" }));
    }
}
