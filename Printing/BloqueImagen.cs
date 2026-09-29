using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VentoryPrint.Models;

namespace VentoryPrint.Printing;

/// <summary>
/// Dibuja un recuadro o una banda (blanco sobre negro) como imagen y la entrega
/// lista para GS v 0, el mismo comando del logo. Así el fondo negro, el borde y
/// las letras grandes salen iguales en cualquier ticketera, sin depender de su
/// tabla de caracteres ni de que soporte impresión invertida.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BloqueImagen
{
    private const int Borde = 3;
    private const int MargenX = 16;
    private const int MargenY = 10;
    private const int AltoSeparador = 14;

    /// <summary>Devuelve null si algo falla: el bloque cae entonces al modo texto.</summary>
    public static ImageEscPos.Raster? Raster(List<LineaBloque> lineas, string? alinear, int puntos, bool banda)
    {
        try
        {
            using var bmp = Dibujar(lineas, alinear, puntos, banda);
            return Empaquetar(bmp);
        }
        catch
        {
            return null;
        }
    }

    internal static Bitmap Dibujar(List<LineaBloque> lineas, string? alinear, int puntos, bool banda)
    {
        var ancho = Math.Max(64, puntos - puntos % 8);
        var interior = ancho - 2 * (MargenX + (banda ? 0 : Borde));
        var familia = Familia();

        // 1) Medir: cada línea del bloque se parte en renglones que caben en el interior.
        var renglones = new List<Renglon>();
        using (var lienzo = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(lienzo))
        {
            Preparar(g);
            foreach (var l in lineas)
            {
                if (l.Separador) { renglones.Add(new Renglon(null, null, AltoSeparador, "izq")); continue; }

                var fuente = new Font(familia, Pixeles(l.Tamano), l.Negrita ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                var alto = (int)Math.Ceiling(fuente.GetHeight(g)) + 2;
                foreach (var texto in Partir(g, l.Texto ?? "", fuente, interior))
                    renglones.Add(new Renglon(texto, fuente, alto, l.Alinear ?? alinear ?? (banda ? "centro" : "izq")));
            }
        }

        // 2) Dibujar.
        var altoTotal = renglones.Sum(r => r.Alto) + 2 * (MargenY + (banda ? 0 : Borde));
        var bmp = new Bitmap(ancho, altoTotal, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            Preparar(g);
            g.Clear(banda ? Color.Black : Color.White);

            using var tinta = new SolidBrush(banda ? Color.White : Color.Black);
            using var raya = new Pen(banda ? Color.White : Color.Black, 2) { DashPattern = new[] { 3f, 3f } };

            if (!banda)
            {
                using var marco = new Pen(Color.Black, Borde) { Alignment = PenAlignment.Inset };
                g.DrawRectangle(marco, 0, 0, ancho - 1, altoTotal - 1);
            }

            var x0 = MargenX + (banda ? 0 : Borde);
            var y = MargenY + (banda ? 0 : Borde);
            foreach (var r in renglones)
            {
                if (r.Texto is null || r.Fuente is null)
                {
                    g.DrawLine(raya, x0, y + r.Alto / 2, x0 + interior, y + r.Alto / 2);
                }
                else
                {
                    var w = Medir(g, r.Texto, r.Fuente);
                    var x = r.Alinear.StartsWith('c') ? x0 + (interior - w) / 2
                        : r.Alinear.StartsWith('d') ? x0 + interior - w
                        : x0;
                    g.DrawString(r.Texto, r.Fuente, tinta, Math.Max(x0, x), y, Formato);
                }
                y += r.Alto;
            }
        }

        foreach (var f in renglones.Select(r => r.Fuente).Where(f => f is not null).Distinct())
            f!.Dispose();

        return bmp;
    }

    private sealed record Renglon(string? Texto, Font? Fuente, int Alto, string Alinear);

    private static readonly StringFormat Formato = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap,
    };

    private static void Preparar(Graphics g)
    {
        // Sin suavizado: la ticketera solo tiene punto negro o blanco.
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        g.SmoothingMode = SmoothingMode.None;
    }

    /// <summary>Alto de letra en puntos de impresora (la letra normal de la ticketera mide 24).</summary>
    private static int Pixeles(string? tamano) => (tamano ?? "").Trim().ToLowerInvariant() switch
    {
        "grande" => 48,
        "alto" or "ancho" => 36,
        _ => 26,
    };

    private static FontFamily Familia()
    {
        foreach (var nombre in new[] { "Consolas", "Lucida Console", "Courier New" })
        {
            try { return new FontFamily(nombre); } catch (ArgumentException) { /* no instalada */ }
        }
        return FontFamily.GenericMonospace;
    }

    private static int Medir(Graphics g, string texto, Font fuente) =>
        (int)Math.Ceiling(g.MeasureString(texto, fuente, int.MaxValue, Formato).Width);

    /// <summary>Parte por palabras; una palabra más ancha que el interior se corta en trozos.</summary>
    private static List<string> Partir(Graphics g, string texto, Font fuente, int interior)
    {
        var res = new List<string>();
        foreach (var parrafo in texto.Replace("\r", "").Split('\n'))
        {
            var actual = "";
            foreach (var palabra in parrafo.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var trozo = palabra;
                while (Medir(g, trozo, fuente) > interior && trozo.Length > 1)
                {
                    var corte = trozo.Length - 1;
                    while (corte > 1 && Medir(g, trozo[..corte], fuente) > interior) corte--;
                    if (actual.Length > 0) { res.Add(actual); actual = ""; }
                    res.Add(trozo[..corte]);
                    trozo = trozo[corte..];
                }

                var candidato = actual.Length == 0 ? trozo : actual + " " + trozo;
                if (Medir(g, candidato, fuente) <= interior) actual = candidato;
                else { res.Add(actual); actual = trozo; }
            }
            if (actual.Length > 0) res.Add(actual);
        }
        return res;
    }

    /// <summary>32 bpp → 1 bit por punto, MSB primero, 1 = negro.</summary>
    internal static ImageEscPos.Raster Empaquetar(Bitmap bmp)
    {
        var w = bmp.Width - bmp.Width % 8;
        var h = bmp.Height;
        var bytesFila = w / 8;
        var packed = new byte[bytesFila * h];

        var datos = bmp.LockBits(new Rectangle(0, 0, bmp.Width, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var fila = new byte[datos.Stride];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(datos.Scan0 + y * datos.Stride, fila, 0, datos.Stride);
                for (var x = 0; x < w; x++)
                {
                    var i = x * 4; // B, G, R, A
                    var lum = 0.114 * fila[i] + 0.587 * fila[i + 1] + 0.299 * fila[i + 2];
                    if (lum < 128) packed[y * bytesFila + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
        finally
        {
            bmp.UnlockBits(datos);
        }

        return new ImageEscPos.Raster(packed, bytesFila, h);
    }
}
