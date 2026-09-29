using System.Runtime.Versioning;
using VentoryPrint.Models;

namespace VentoryPrint.Printing;

/// <summary>
/// Dibuja un ticket armado por bloques (ver <see cref="Bloque"/>). No sabe qué
/// es una venta ni un despacho: solo texto, pares, tablas, recuadros y bandas.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BloqueRenderer
{
    /// <summary>Versión del contrato de bloques. Sale en /status para que el POS sepa qué puede mandar.</summary>
    public const int Version = 1;

    public static readonly string[] Tipos =
        { "texto", "pares", "tabla", "recuadro", "banda", "linea", "espacio", "qr", "logo" };

    public static void Render(EscPosBuilder b, TicketPayload d, int width)
    {
        foreach (var bl in d.Bloques ?? new())
        {
            if (bl is null) continue;
            try
            {
                switch ((bl.Tipo ?? "").Trim().ToLowerInvariant())
                {
                    case "texto":    Texto(b, bl, width); break;
                    case "pares":    Pares(b, bl, width); break;
                    case "tabla":    Tabla(b, bl, width); break;
                    case "recuadro": Caja(b, bl, width, banda: false); break;
                    case "banda":    Caja(b, bl, width, banda: true); break;
                    case "linea":    b.JustifyLeft().Line(new string(Caracter(bl.Caracter), width)); break;
                    case "espacio":  b.Feed(Math.Clamp(bl.N ?? 1, 1, 10)); break;
                    case "qr":       Qr(b, bl); break;
                    case "logo":     Logo(b, d, bl, width); break;
                    // Tipo desconocido: lo manda un POS más nuevo. Se salta.
                }
            }
            catch
            {
                // Un bloque mal formado no tumba el ticket: se imprime el resto.
            }

            b.TextSize(1, 1).Bold(false).Reverse(false).JustifyLeft();
        }
    }

    // ---------------------------------------------------------------- texto

    private static void Texto(EscPosBuilder b, Bloque bl, int width)
    {
        if (string.IsNullOrWhiteSpace(bl.Texto)) return;

        var (w, h) = Tam(bl.Tamano);
        var lineas = Parrafos(bl.Texto, Math.Max(1, width / w));

        if (bl.Invertido)
        {
            b.JustifyLeft().Bold(bl.Negrita).Reverse(true);
            foreach (var ln in lineas) LineaCompuesta(b, ln, w, h, bl.Alinear ?? "centro", width, "", "");
            return;
        }

        Justificar(b, bl.Alinear);
        b.Bold(bl.Negrita).TextSize(w, h);
        foreach (var ln in lineas) b.Line(ln);
    }

    // ---------------------------------------------------------------- pares

    private static void Pares(EscPosBuilder b, Bloque bl, int width)
    {
        var items = (bl.Items ?? new())
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Valor))
            .ToList();
        if (items.Count == 0) return;

        var extremos = string.Equals(bl.Estilo, "extremos", StringComparison.OrdinalIgnoreCase);
        // Una sola columna de etiquetas para todo el bloque: los valores quedan alineados.
        var col = Math.Min(items.Max(i => (i.Etiqueta ?? "").Length) + 1, Math.Max(4, width / 2));

        b.JustifyLeft();
        foreach (var it in items)
        {
            var (w, h) = Tam(it.Tamano ?? bl.Tamano);
            var ancho = Math.Max(1, width / w);
            var etiqueta = (it.Etiqueta ?? "").Trim();
            var valor = it.Valor!.Trim();

            b.Bold(it.Negrita || bl.Negrita).TextSize(w, h);
            foreach (var ln in extremos ? Extremos(etiqueta, valor, ancho) : EnColumna(etiqueta, valor, col, ancho))
                b.Line(ln);
        }
    }

    /// <summary>"TOTAL:            S/ 2,110.10". Si no cabe, el valor baja a su propia línea, a la derecha.</summary>
    internal static List<string> Extremos(string etiqueta, string valor, int ancho)
    {
        if (etiqueta.Length + 1 + valor.Length <= ancho)
            return new() { etiqueta.PadRight(ancho - valor.Length) + valor };

        var res = TicketRenderer.WordWrap(etiqueta, ancho);
        res.AddRange(TicketRenderer.WordWrap(valor, ancho).Select(v => v.PadLeft(ancho)));
        return res;
    }

    /// <summary>
    /// Etiqueta y valor en columnas. Si el valor no cabe junto a la columna del
    /// grupo, se pega a su propia etiqueta; si tampoco, la etiqueta va arriba y
    /// el valor debajo a todo el ancho (o con sangría si la etiqueta es corta).
    /// </summary>
    internal static List<string> EnColumna(string etiqueta, string valor, int col, int ancho)
    {
        if (etiqueta.Length == 0) return TicketRenderer.WordWrap(valor, ancho);

        col = Math.Max(col, etiqueta.Length + 1);
        if (col + valor.Length <= ancho) return new() { etiqueta.PadRight(col) + valor };

        var propia = etiqueta.Length + 1;
        if (propia + valor.Length <= ancho) return new() { etiqueta + " " + valor };

        if (propia > ancho * 0.4)
        {
            var res = new List<string> { etiqueta };
            res.AddRange(TicketRenderer.WordWrap(valor, ancho));
            return res;
        }
        return TicketRenderer.LineasConEtiqueta(etiqueta, valor, propia, ancho);
    }

    // ---------------------------------------------------------------- tabla

    private static void Tabla(EscPosBuilder b, Bloque bl, int width)
    {
        var lineas = LineasTabla(bl, width);
        b.JustifyLeft();
        foreach (var (texto, negrita) in lineas)
            b.Bold(negrita).Line(texto);
    }

    /// <summary>
    /// Reparte las columnas según el papel. Las columnas fijas miden lo que su
    /// celda más larga; la flexible (la descripción) se queda con el resto. Si a
    /// la flexible le quedan menos de 12 caracteres, o alguna descripción
    /// ocuparía más de dos renglones, la tabla pasa a dos líneas por fila: la
    /// descripción arriba y los números debajo.
    /// </summary>
    internal static List<(string texto, bool negrita)> LineasTabla(Bloque bl, int width)
    {
        var res = new List<(string, bool)>();
        var cols = bl.Columnas;
        if (cols is null || cols.Count == 0) return res;

        var filas = bl.Filas ?? new();
        var n = cols.Count;
        var flex = cols.FindIndex(c => c?.Flexible ?? false);
        if (flex < 0) flex = 0;
        var encabezado = bl.Encabezado ?? true;

        string Cel(List<string?>? f, int i) => f is not null && i < f.Count ? (f[i] ?? "").Trim() : "";
        string Titulo(int i) => (cols[i]?.Titulo ?? "").Trim();
        bool Derecha(int i) => cols[i]?.Alinear is { } a
            ? a.StartsWith("d", StringComparison.OrdinalIgnoreCase)
            : i != flex;

        var anchos = new int[n];
        for (var i = 0; i < n; i++)
        {
            anchos[i] = encabezado ? Titulo(i).Length : 0;
            foreach (var f in filas) anchos[i] = Math.Max(anchos[i], Cel(f, i).Length);
        }

        var fijos = Enumerable.Range(0, n).Where(i => i != flex).ToList();
        var anchoFlex = width - fijos.Sum(i => anchos[i]) - (n - 1);

        var estilo = (bl.Estilo ?? "auto").Trim().ToLowerInvariant();
        var dosLineas = n > 1 && estilo switch
        {
            "doslineas" => true,
            "fila"      => anchoFlex < 4,
            // auto: si la descripción queda muy angosta o alguna ocuparía más de
            // dos renglones, se lee mejor arriba, a todo el ancho.
            _ => anchoFlex < 12 || filas.Any(f => TicketRenderer.WordWrap(Cel(f, flex), anchoFlex).Count > 2),
        };

        if (!dosLineas)
        {
            string Fila(Func<int, string> celda) => string.Join(" ", Enumerable.Range(0, n).Select(i =>
            {
                var ancho = i == flex ? Math.Max(1, anchoFlex) : anchos[i];
                var t = celda(i);
                if (t.Length > ancho) t = t[..ancho];
                return Derecha(i) ? t.PadLeft(ancho) : t.PadRight(ancho);
            })).TrimEnd();

            if (encabezado)
            {
                res.Add((Fila(Titulo), true));
                res.Add((new string('-', width), false));
            }
            foreach (var f in filas)
            {
                var partes = TicketRenderer.WordWrap(Cel(f, flex), Math.Max(1, anchoFlex));
                res.Add((Fila(i => i == flex ? partes[0] : Cel(f, i)), false));
                foreach (var resto in partes.Skip(1))
                    res.Add((Fila(i => i == flex ? resto : ""), false));
            }
            return res;
        }

        // Dos líneas por fila: los números se reparten en casillas iguales.
        if (encabezado)
        {
            if (Titulo(flex).Length > 0) res.Add((Titulo(flex), true));
            res.AddRange(Casillas(fijos.Select(Titulo).ToList(), fijos.Select(Titulo).ToList(), width).Select(l => (l, true)));
            res.Add((new string('-', width), false));
        }
        foreach (var f in filas)
        {
            foreach (var ln in TicketRenderer.WordWrap(Cel(f, flex), width)) res.Add((ln, false));
            var valores = fijos.Select(i => Cel(f, i)).ToList();
            if (valores.Any(v => v.Length > 0))
                res.AddRange(Casillas(valores, fijos.Select(Titulo).ToList(), width).Select(l => (l, false)));
        }
        return res;
    }

    /// <summary>
    /// Reparte valores en casillas de igual ancho, cada uno pegado a la derecha
    /// de la suya. Si alguno no cabe, cada valor va en su línea con su título.
    /// </summary>
    private static List<string> Casillas(List<string> valores, List<string> titulos, int width)
    {
        var k = valores.Count;
        if (k == 0) return new();

        var casilla = width / k;
        if (valores.All(v => v.Length < casilla))
        {
            var linea = string.Concat(valores.Select((v, i) =>
                v.PadLeft(i == k - 1 ? width - casilla * (k - 1) : casilla)));
            return new() { linea };
        }

        return valores
            .Select((v, i) => (v, t: titulos[i]))
            .Where(x => x.v.Length > 0)
            .SelectMany(x => Extremos(x.t.Length > 0 && x.t != x.v ? "  " + x.t + ":" : "", x.v, width))
            .ToList();
    }

    // ---------------------------------------------------- recuadro y banda

    /// <summary>
    /// Recuadro (borde) o banda (blanco sobre negro). Por defecto se dibuja como
    /// imagen, que sale igual en cualquier ticketera; con modo "texto", o si la
    /// imagen falla, se arma con caracteres.
    /// </summary>
    private static void Caja(EscPosBuilder b, Bloque bl, int width, bool banda)
    {
        var lineas = (bl.Lineas ?? new()).Where(l => l is not null && (l.Separador || !string.IsNullOrWhiteSpace(l.Texto))).ToList();
        // Atajo: un recuadro o banda de una sola línea puede venir en "texto".
        if (lineas.Count == 0 && !string.IsNullOrWhiteSpace(bl.Texto))
            lineas.Add(new LineaBloque { Texto = bl.Texto, Tamano = bl.Tamano, Negrita = bl.Negrita });
        if (lineas.Count == 0) return;

        if (!string.Equals(bl.Modo, "texto", StringComparison.OrdinalIgnoreCase))
        {
            var raster = BloqueImagen.Raster(lineas, bl.Alinear, PuntosDe(width), banda);
            if (raster is { } r)
            {
                b.JustifyLeft().RasterImage(r.Packed, r.WidthBytes, r.Height);
                return;
            }
        }

        if (banda) BandaTexto(b, bl, lineas, width);
        else RecuadroTexto(b, bl, lineas, width);
    }

    private static void BandaTexto(EscPosBuilder b, Bloque bl, List<LineaBloque> lineas, int width)
    {
        b.JustifyLeft().Reverse(true);
        foreach (var l in lineas)
        {
            if (l.Separador) { b.Bold(false).TextSize(1, 1).Line(new string(' ', width)); continue; }

            var (w, h) = Tam(l.Tamano);
            b.Bold(l.Negrita);
            foreach (var ln in Parrafos(l.Texto!, Math.Max(1, (width - 2) / w)))
                LineaCompuesta(b, ln, w, h, l.Alinear ?? bl.Alinear ?? "centro", width, " ", " ");
        }
        b.Reverse(false);
    }

    private static void RecuadroTexto(EscPosBuilder b, Bloque bl, List<LineaBloque> lineas, int width)
    {
        // ┌ ─ ┐ │ └ ┘ están en la misma posición en CP437 y CP850.
        var (h0, v, tl, tr, bl0, br) = (bl.Borde ?? "linea").Trim().ToLowerInvariant() switch
        {
            "ascii" => ('-', '|', '+', '+', '+', '+'),
            "doble" => ('═', '║', '╔', '╗', '╚', '╝'),
            _       => ('─', '│', '┌', '┐', '└', '┘'),
        };
        var interior = Math.Max(1, width - 4);

        b.JustifyLeft().Line(tl + new string(h0, width - 2) + tr);
        foreach (var l in lineas)
        {
            if (l.Separador)
            {
                b.Bold(false).TextSize(1, 1).Line($"{v} {new string('-', interior)} {v}");
                continue;
            }

            var (w, h) = Tam(l.Tamano);
            b.Bold(l.Negrita);
            foreach (var ln in Parrafos(l.Texto!, Math.Max(1, interior / w)))
                LineaCompuesta(b, ln, w, h, l.Alinear ?? bl.Alinear ?? "izq", width, v + " ", " " + v);
        }
        b.Bold(false).TextSize(1, 1).Line(bl0 + new string(h0, width - 2) + br);
    }

    /// <summary>
    /// Una línea de ancho completo con texto agrandado dentro: el relleno y los
    /// bordes van a ancho normal (pero al mismo alto) para que la línea mida
    /// exactamente el papel, sea cual sea el tamaño del texto.
    /// </summary>
    private static void LineaCompuesta(EscPosBuilder b, string texto, int w, int h, string alinear, int width, string prefijo, string sufijo)
    {
        var interior = width - prefijo.Length - sufijo.Length;
        var maximo = Math.Max(1, interior / w);
        if (texto.Length > maximo) texto = texto[..maximo];

        var libre = Math.Max(0, interior - texto.Length * w);
        var izq = alinear.StartsWith("d", StringComparison.OrdinalIgnoreCase) ? libre
            : alinear.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? libre / 2
            : 0;

        b.TextSize(1, h).Text(prefijo + new string(' ', izq));
        b.TextSize(w, h).Text(texto);
        b.TextSize(1, h).Text(new string(' ', libre - izq) + sufijo + "\n");
        b.TextSize(1, 1);
    }

    // ------------------------------------------------------------ qr y logo

    private static void Qr(EscPosBuilder b, Bloque bl)
    {
        if (string.IsNullOrWhiteSpace(bl.Datos)) return;
        b.JustifyCenter().QrCode(bl.Datos!, Math.Clamp(bl.Escala ?? 6, 1, 16)).Feed();
    }

    private static void Logo(EscPosBuilder b, TicketPayload d, Bloque bl, int width)
    {
        if (string.IsNullOrWhiteSpace(d.Logo)) return;
        var raster = ImageEscPos.FromDataUri(d.Logo, PuntosDe(width), bl.Escala ?? d.Negocio?.LogoEscala ?? 100);
        if (raster is not { } r) return;
        b.JustifyCenter().RasterImage(r.Packed, r.WidthBytes, r.Height).Feed();
    }

    // ------------------------------------------------------------- comunes

    /// <summary>Puntos imprimibles: 12 por carácter (48 columnas = 576, 32 = 384), en múltiplo de 8.</summary>
    internal static int PuntosDe(int width)
    {
        var puntos = Math.Clamp(width, 16, 64) * 12;
        return puntos - puntos % 8;
    }

    internal static (int w, int h) Tam(string? tamano) => (tamano ?? "").Trim().ToLowerInvariant() switch
    {
        "alto"   => (1, 2),
        "ancho"  => (2, 1),
        "grande" => (2, 2),
        _        => (1, 1),
    };

    private static void Justificar(EscPosBuilder b, string? alinear)
    {
        var a = (alinear ?? "").Trim().ToLowerInvariant();
        if (a.StartsWith('c')) b.JustifyCenter();
        else if (a.StartsWith('d')) b.JustifyRight();
        else b.JustifyLeft();
    }

    private static char Caracter(string? c) => string.IsNullOrEmpty(c) || c[0] < ' ' ? '-' : c[0];

    /// <summary>Respeta los saltos de línea del POS y parte cada párrafo por palabras.</summary>
    private static List<string> Parrafos(string texto, int ancho) =>
        texto.Replace("\r", "").Split('\n')
            .SelectMany(p => TicketRenderer.WordWrap(p.Trim(), ancho))
            .ToList();
}
