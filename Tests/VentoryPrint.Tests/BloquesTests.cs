using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using VentoryPrint.Models;
using VentoryPrint.Printing;
using VentoryPrint.Services;
using Xunit;

namespace VentoryPrint.Tests;

/// <summary>
/// Ticket por plantilla: el POS manda bloques y el agente los dibuja. Se prueba
/// con el ticket de una ferretería (envío a obra, por cancelar), que usa todos
/// los bloques. Las vistas previas quedan en %TEMP% (texto y PNG).
/// </summary>
public class BloquesTests
{
    /// <summary>Lo que mandaría el POS: bloques nuevos + los campos de siempre.</summary>
    private const string TicketJsonEjemplo = """
    {
      "token": "abc",
      "negocio": { "nombre": "Ferretería H&C", "ruc": "20600134648", "direccion": "Chiclayo" },
      "documento": { "tipo": "NOTA DE VENTA", "numero": "V-0021", "fecha": "27/09/2026 10:15 AM", "vendedor": "CAJERA 1" },
      "cliente": { "nombre": "DAGOBERTO RODRIGUEZ" },
      "items": [ { "cant": 20, "desc": "Cemento Azul Pacasmayo Antisalitre", "precio": 35, "importe": 700, "unidad": "Und" } ],
      "totales": { "total": 2110.10, "moneda": "PEN" },
      "pago": { "metodo": "Yape" },
      "pie": "Gracias por su compra",
      "bloques": [
        { "tipo": "texto", "texto": "FERROMATERIALES", "alinear": "centro", "tamano": "ancho", "negrita": true },
        { "tipo": "texto", "texto": "FERRETERÍA H&C\nRUC: 20600134648\nAv. Los Incas 450, La Victoria\nChiclayo", "alinear": "centro" },
        { "tipo": "linea" },
        { "tipo": "texto", "texto": "NOTA DE VENTA\nV-0021", "alinear": "centro", "negrita": true },
        { "tipo": "pares", "items": [
          { "etiqueta": "Fecha:", "valor": "27/09/2026 10:15 a. m." },
          { "etiqueta": "Cajero:", "valor": "CAJERA 1" },
          { "etiqueta": "Cel. cajero:", "valor": 974123456 },
          { "etiqueta": "Caja:", "valor": null }
        ] },
        { "tipo": "linea" },
        { "tipo": "texto", "texto": "DATOS DEL CLIENTE", "negrita": true },
        { "tipo": "pares", "items": [
          { "etiqueta": "Cliente:", "valor": "DAGOBERTO RODRIGUEZ" },
          { "etiqueta": "DNI:", "valor": "16789012" }
        ] },
        { "tipo": "recuadro", "modo": "MODO", "lineas": [
          { "texto": "ZONA" },
          { "texto": "Pomalca", "tamano": "alto", "negrita": true },
          { "separador": true },
          { "texto": "TELÉFONO" },
          { "texto": "979 555 012", "tamano": "alto", "negrita": true },
          { "separador": true },
          { "texto": "DIRECCIÓN" },
          { "texto": "Calle Los Cedros 245, frente al parque principal de la urbanización", "tamano": "alto", "negrita": true }
        ] },
        { "tipo": "pares", "items": [ { "etiqueta": "Obs.:", "valor": "Dejar el material por la puerta lateral" } ] },
        { "tipo": "recuadro", "modo": "MODO", "alinear": "centro", "lineas": [ { "texto": "ENVÍO A OBRA", "tamano": "grande", "negrita": true } ] },
        { "tipo": "banda", "modo": "MODO", "lineas": [
          { "texto": "RUTA 3", "tamano": "grande", "negrita": true },
          { "texto": "POMALCA", "negrita": true }
        ] },
        { "tipo": "pares", "items": [ { "etiqueta": "Entrega programada:", "valor": "Lun 28/09/2026 - 9:00 a. m.", "negrita": true } ] },
        { "tipo": "linea" },
        { "tipo": "tabla", "estilo": "dosLineas",
          "columnas": [ { "titulo": "", "flexible": true }, { "titulo": "Cant" }, { "titulo": "P.U." }, { "titulo": "Importe" } ],
          "filas": [
            [ "Cemento Azul Pacasmayo Antisalitre x Und", 20, "35.00", "700.00" ],
            [ "Piedra Chancada 1/2 x m3", 5, "75.00", "375.00" ]
          ] },
        { "tipo": "linea" },
        { "tipo": "tabla",
          "columnas": [ { "titulo": "Producto", "flexible": true }, { "titulo": "Vendida" }, { "titulo": "Entregado" }, { "titulo": "Pendiente" } ],
          "filas": [
            [ "Cemento Azul Pacasmayo Antisalitre", "100", "40", "60" ],
            [ "Fierro 8 mm Siderperú", "5", "5", "0" ]
          ] },
        { "tipo": "linea" },
        { "tipo": "pares", "estilo": "extremos", "items": [
          { "etiqueta": "OP. GRAVADA:", "valor": "S/ 1,788.22" },
          { "etiqueta": "IGV (18%):", "valor": "S/ 321.88" },
          { "etiqueta": "TOTAL:", "valor": "S/ 2,110.10", "negrita": true, "tamano": "alto" }
        ] },
        { "tipo": "linea" },
        { "tipo": "texto", "texto": "FORMA DE PAGO", "negrita": true },
        { "tipo": "pares", "estilo": "extremos", "items": [
          { "etiqueta": "A cuenta (Yape BCP):", "valor": "S/ 1,000.00" },
          { "etiqueta": "Saldo:", "valor": "S/ 1,110.10" }
        ] },
        { "tipo": "banda", "modo": "MODO", "lineas": [
          { "texto": "POR CANCELAR", "tamano": "alto", "negrita": true },
          { "texto": "COBRAR EN OBRA", "negrita": true },
          { "texto": "S/ 1,110.10", "tamano": "grande", "negrita": true }
        ] },
        { "tipo": "holograma", "texto": "un bloque que este agente no conoce" },
        { "tipo": "tabla" },
        { "tipo": "espacio", "n": 1 },
        { "tipo": "texto", "texto": "Gracias por su compra", "alinear": "centro" }
      ]
    }
    """;

    private static TicketPayload Ticket(string modo) =>
        JsonSerializer.Deserialize<TicketPayload>(TicketJsonEjemplo.Replace("\"MODO\"", $"\"{modo}\""), TicketJson.Options)!;

    [Theory]
    [InlineData(80, 48)]
    [InlineData(58, 32)]
    public void En_modo_texto_todo_cabe_en_el_papel(int papel, int ancho)
    {
        var lineas = Render(Ticket("texto"), papel, $"bloques-texto-{papel}mm.txt");

        Assert.All(lineas, ln => Assert.True(ln.Length <= ancho, $"'{ln}' mide {ln.Length} > {ancho}"));

        // Texto y pares
        Assert.Contains("FERROMATERIALES", lineas);
        Assert.Contains(lineas, ln => ln.StartsWith("Cel. cajero:") && ln.EndsWith("974123456"));   // número donde va texto
        Assert.DoesNotContain(lineas, ln => ln.StartsWith("Caja:"));                                 // valor vacío: no sale
        Assert.Contains(lineas, ln => ln.StartsWith("TOTAL:") && ln.EndsWith("S/ 2,110.10") && ln.Length == ancho);

        // Recuadro: borde completo y texto dentro
        Assert.Contains(lineas, ln => ln == "┌" + new string('─', ancho - 2) + "┐");
        Assert.Contains(lineas, ln => ln.StartsWith("│ ") && ln.EndsWith(" │") && ln.Contains("979 555 012"));

        // Banda: en blanco sobre negro
        Assert.Contains(lineas, ln => ln.Trim() == "POR CANCELAR");

        // Lo que el agente no conoce o viene incompleto se salta sin romper nada
        Assert.DoesNotContain(lineas, ln => ln.Contains("no conoce"));
        Assert.Equal("Gracias por su compra", lineas.Last(ln => ln.Trim().Length > 0));
    }

    [Fact]
    public void Un_par_que_no_cabe_se_acomoda_sin_dejar_sangrias_enormes()
    {
        // Cabe en la columna del grupo
        Assert.Equal(new[] { "Cajero:      CAJERA 1" }, BloqueRenderer.EnColumna("Cajero:", "CAJERA 1", 13, 32));
        // No cabe en la columna del grupo, pero sí pegado a su etiqueta
        Assert.Equal(new[] { "Fecha: 27/09/2026 10:15 a. m." }, BloqueRenderer.EnColumna("Fecha:", "27/09/2026 10:15 a. m.", 13, 32));
        // Etiqueta larga: arriba, y el valor debajo a todo el ancho
        Assert.Equal(new[] { "Entrega programada:", "Lun 28/09/2026 - 9:00 a. m." },
            BloqueRenderer.EnColumna("Entrega programada:", "Lun 28/09/2026 - 9:00 a. m.", 20, 32));
        // Etiqueta corta y valor largo: sangría bajo el valor
        Assert.Equal(new[] { "Obs.: Dejar el material por la", "      puerta lateral" },
            BloqueRenderer.EnColumna("Obs.:", "Dejar el material por la puerta lateral", 6, 32));
    }

    private static Bloque TablaDespacho(params string[] productos) => new()
    {
        Tipo = "tabla",
        Columnas = new()
        {
            new() { Titulo = "Producto", Flexible = true }, new() { Titulo = "Vendida" },
            new() { Titulo = "Entregado" }, new() { Titulo = "Pendiente" },
        },
        Filas = productos.Select(p => new List<string?> { p, "100", "40", "60" }).ToList(),
    };

    [Fact]
    public void La_tabla_pone_cada_numero_bajo_su_titulo()
    {
        var lineas = BloqueRenderer.LineasTabla(TablaDespacho("Cemento Sol", "Fierro 1/2 Siderperú"), 48)
            .Select(l => l.texto).ToList();

        Assert.Equal("Producto             Vendida Entregado Pendiente", lineas[0]);
        Assert.Equal(new string('-', 48), lineas[1]);
        Assert.Equal("Cemento Sol              100        40        60", lineas[2]);
        Assert.Equal("Fierro 1/2 Siderperú     100        40        60", lineas[3]);
    }

    [Theory]
    [InlineData(48, "Cemento Azul Pacasmayo Antisalitre Tipo MS x Bolsa de 42.5 kg")]   // ocuparía 3 renglones
    [InlineData(32, "Cemento Sol")]                                                      // 58 mm: no hay sitio al lado
    public void Si_la_descripcion_no_cabe_al_lado_va_arriba_y_los_numeros_debajo(int ancho, string producto)
    {
        var lineas = BloqueRenderer.LineasTabla(TablaDespacho(producto), ancho).Select(l => l.texto).ToList();

        Assert.All(lineas, ln => Assert.True(ln.Length <= ancho, $"'{ln}' mide {ln.Length} > {ancho}"));
        Assert.Equal("Producto", lineas[0]);
        Assert.True(lineas[1].TrimStart().StartsWith("Vendida") && lineas[1].EndsWith("Pendiente") && lineas[1].Length == ancho);

        var numeros = lineas.Single(ln => ln.TrimStart().StartsWith("100"));
        Assert.Equal(lineas[1].IndexOf("Entregado") + "Entregado".Length, numeros.IndexOf("40") + 2);
        Assert.EndsWith("60", numeros);
        Assert.StartsWith(producto.Split(' ')[0], lineas[3]);
    }

    [Fact]
    public void El_pos_puede_forzar_el_estilo_de_la_tabla()
    {
        var t = TablaDespacho("Cemento Sol");
        t.Estilo = "dosLineas";
        Assert.Equal("Cemento Sol", BloqueRenderer.LineasTabla(t, 48)[3].texto);

        t = TablaDespacho("Cemento Azul Pacasmayo Antisalitre Tipo MS x Bolsa de 42.5 kg");
        t.Estilo = "fila";
        Assert.StartsWith("Cemento Azul ", BloqueRenderer.LineasTabla(t, 48)[2].texto);
        Assert.EndsWith(" 60", BloqueRenderer.LineasTabla(t, 48)[2].texto);
    }

    [Theory]
    [InlineData(80, 576)]
    [InlineData(58, 384)]
    public void Por_defecto_recuadros_y_bandas_salen_como_imagen(int papel, int puntos)
    {
        var lineas = Render(Ticket("imagen"), papel, $"bloques-imagen-{papel}mm.txt");

        // 2 recuadros + 2 bandas, al ancho exacto del papel
        Assert.Equal(4, lineas.Count(ln => ln.StartsWith($"[IMAGEN {puntos}x")));
        Assert.DoesNotContain(lineas, ln => ln.Contains("POR CANCELAR"));   // va dentro de la imagen
        Assert.Contains(lineas, ln => ln.StartsWith("TOTAL:"));              // el resto sigue siendo texto
    }

    [Fact]
    public void La_banda_es_negra_y_el_recuadro_blanco()
    {
        var t = Ticket("imagen");
        var banda = t.Bloques!.Last(b => b.Tipo == "banda");
        var recuadro = t.Bloques!.First(b => b.Tipo == "recuadro");

        Assert.InRange(Negro(banda, banda: true, "bloque-banda.png"), 0.55, 0.98);
        Assert.InRange(Negro(recuadro, banda: false, "bloque-recuadro.png"), 0.02, 0.35);
    }

    [Fact]
    public void Un_agente_anterior_ignora_los_bloques_e_imprime_el_ticket_de_siempre()
    {
        // Así leen el ticket los agentes hasta 1.2.6: sin la clave "bloques".
        var viejo = JsonSerializer.Deserialize<PayloadAnterior>(TicketJsonEjemplo, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        })!;

        Assert.Equal("V-0021", viejo.Documento!.Numero);
        Assert.Single(viejo.Items);
        Assert.Equal(2110.10m, viejo.Totales!.Total);
    }

    [Fact]
    public void Sin_bloques_el_ticket_sale_con_el_diseno_de_siempre()
    {
        var t = Ticket("texto");
        t.Bloques = null;

        var lineas = Render(t, 80, "bloques-sin-bloques.txt");

        Assert.Contains(lineas, ln => ln.StartsWith("Producto") && ln.EndsWith("Impte."));
        Assert.Contains(lineas, ln => ln.StartsWith("PAGO:") && ln.EndsWith("YAPE"));
    }

    [Theory]
    [InlineData(80, 48)]
    [InlineData(58, 32)]
    public void La_muestra_de_prueba_en_papel_se_lee_y_cabe(int papel, int ancho)
    {
        var t = JsonSerializer.Deserialize<TicketPayload>(VentoryPrint.Cli.TestBloquesCli.Muestra, TicketJson.Options)!;
        var lineas = Render(t, papel, $"bloques-muestra-{papel}mm.txt");

        Assert.All(lineas, ln => Assert.True(ln.Length <= ancho, $"'{ln}' mide {ln.Length} > {ancho}"));
        Assert.Equal(3, lineas.Count(ln => ln.StartsWith("[IMAGEN ")));
        Assert.Contains(lineas, ln => ln.StartsWith("[QR]"));
        Assert.Contains(lineas, ln => ln.Trim() == "POR CANCELAR");   // la banda en modo texto
    }

    /// <summary>
    /// Contrato con el POS: dibuja los tickets que arma ventoryPOS de verdad.
    /// VENTORYPRINT_TICKETS_DIR apunta a una carpeta con ticket-*.json; sin ella no hace nada.
    /// </summary>
    [Fact]
    public void Dibuja_los_tickets_que_arma_el_pos()
    {
        var dir = Environment.GetEnvironmentVariable("VENTORYPRINT_TICKETS_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        foreach (var archivo in Directory.GetFiles(dir, "ticket-*.json"))
        {
            var t = JsonSerializer.Deserialize<TicketPayload>(File.ReadAllText(archivo), TicketJson.Options)!;
            Assert.NotEmpty(t.Bloques!);

            foreach (var (papel, ancho) in new[] { (80, 48), (58, 32) })
            {
                var lineas = Render(t, papel, $"{Path.GetFileNameWithoutExtension(archivo)}-{papel}mm.txt");
                Assert.All(lineas, ln => Assert.True(ln.Length <= ancho, $"{Path.GetFileName(archivo)}: '{ln}' mide {ln.Length} > {ancho}"));
                Assert.Contains(lineas, ln => ln.StartsWith("TOTAL:"));
            }
        }
    }

    // ------------------------------------------------------------------ util

    private sealed class PayloadAnterior
    {
        public DocumentoInfo? Documento { get; set; }
        public List<TicketItem> Items { get; set; } = new();
        public TotalesInfo? Totales { get; set; }
    }

    private static double Negro(Bloque bl, bool banda, string archivo)
    {
        using var bmp = BloqueImagen.Dibujar(bl.Lineas!, bl.Alinear, 576, banda);
        bmp.Save(Path.Combine(Dir(), "ventoryprint-preview-" + archivo), ImageFormat.Png);

        var r = BloqueImagen.Empaquetar(bmp);
        Assert.Equal(576 / 8, r.WidthBytes);
        var negros = r.Packed.Sum(b => System.Numerics.BitOperations.PopCount(b));
        return negros / (double)(r.WidthBytes * 8 * r.Height);
    }

    private static string Dir() => Environment.GetEnvironmentVariable("VENTORYPRINT_PREVIEW_DIR") ?? Path.GetTempPath();

    private static List<string> Render(TicketPayload p, int papelMm, string archivo)
    {
        p.AnchoPapelMm = papelMm;
        var texto = ATexto(new TicketRenderer(new SettingsService()).Render(p));
        File.WriteAllText(Path.Combine(Dir(), "ventoryprint-preview-" + archivo), texto, new UTF8Encoding(false));
        return texto.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
    }

    /// <summary>
    /// Recorre los bytes como lo haría la ticketera: salta los comandos (con su
    /// longitud exacta, porque una imagen puede contener cualquier byte) y deja
    /// el texto en CP850.
    /// </summary>
    private static string ATexto(byte[] b)
    {
        var texto = new MemoryStream();
        var marca = (string s) => texto.Write(Encoding.GetEncoding(850).GetBytes(s));

        for (var i = 0; i < b.Length;)
        {
            if (b[i] == 0x1B) // ESC
            {
                i += (char)b[i + 1] switch { '@' => 2, 'p' => 5, _ => 3 };
            }
            else if (b[i] == 0x1D) // GS
            {
                switch ((char)b[i + 1])
                {
                    case 'v': // GS v 0 m xL xH yL yH + datos
                        var bytesFila = b[i + 4] | (b[i + 5] << 8);
                        var filas = b[i + 6] | (b[i + 7] << 8);
                        marca($"[IMAGEN {bytesFila * 8}x{filas}]\n");
                        i += 8 + bytesFila * filas;
                        break;
                    case '(': // GS ( k pL pH ...
                        var len = b[i + 3] | (b[i + 4] << 8);
                        if (b[i + 6] == 81) marca("[QR]");
                        i += 5 + len;
                        break;
                    default: // GS ! n, GS B n, GS V m
                        i += 3;
                        break;
                }
            }
            else
            {
                texto.WriteByte(b[i++]);
            }
        }

        return Encoding.GetEncoding(850).GetString(texto.ToArray());
    }
}
