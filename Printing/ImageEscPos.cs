using System.Drawing;
using System.Runtime.Versioning;

namespace VentoryPrint.Printing;

/// <summary>
/// Convierte una imagen (data URI base64 que manda el POS) a datos raster 1bpp
/// listos para <see cref="EscPosBuilder.RasterImage"/> (comando GS v 0).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ImageEscPos
{
    public readonly record struct Raster(byte[] Packed, int WidthBytes, int Height);

    /// <summary>
    /// Decodifica el logo y lo reduce a lo ancho de <paramref name="maxWidthDots"/>
    /// (solo reduce, nunca agranda). <paramref name="escalaPorcentaje"/> permite
    /// forzar un ancho menor que el máximo del papel (50 = mitad de ancho).
    /// Devuelve null si la cadena no es una imagen válida.
    /// Nunca lanza: si algo falla, el ticket se imprime sin logo.
    /// </summary>
    public static Raster? FromDataUri(string? dataUri, int maxWidthDots, int escalaPorcentaje = 100)
    {
        if (string.IsNullOrWhiteSpace(dataUri)) return null;

        try
        {
            var b64 = dataUri.Trim();
            var comma = b64.IndexOf(',');
            if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                b64 = b64[(comma + 1)..];

            var bytes = Convert.FromBase64String(b64);

            using var ms = new MemoryStream(bytes);
            using var src = new Bitmap(ms);

            // Aplicar escala configurada sobre el ancho máximo del papel.
            var pct = Math.Clamp(escalaPorcentaje, 10, 200) / 100.0;
            var maxW = Math.Max(8, (int)Math.Round(maxWidthDots * pct));

            // Ancho objetivo, múltiplo de 8 (cada byte = 8 puntos).
            var targetW = Math.Min(src.Width, maxW);
            targetW -= targetW % 8;
            if (targetW < 8) targetW = 8;

            var scale = targetW / (double)src.Width;
            var targetH = Math.Max(1, (int)Math.Round(src.Height * scale));

            using var bmp = new Bitmap(targetW, targetH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(Color.White); // fondo blanco: los PNG con transparencia salen limpios
                g.DrawImage(src, 0, 0, targetW, targetH);
            }

            var widthBytes = targetW / 8;
            var packed = new byte[widthBytes * targetH];

            // Umbral por luminancia. Suficiente para logos (líneas / texto).
            for (var y = 0; y < targetH; y++)
            {
                for (var x = 0; x < targetW; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    var lum = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                    var black = p.A >= 128 && lum < 165;
                    if (black)
                        packed[y * widthBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }

            return new Raster(packed, widthBytes, targetH);
        }
        catch
        {
            return null;
        }
    }
}
