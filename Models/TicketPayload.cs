using System.Text.Json.Serialization;

namespace VentoryPrint.Models;

/// <summary>
/// Contrato del ticket que envía ventoryPOS (resources/js/lib/ticketPrinter.ts).
/// Los nombres JSON deben coincidir 1:1 con TicketPayload de TypeScript.
/// </summary>
public sealed class TicketPayload
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("negocio")]
    public NegocioInfo? Negocio { get; set; }

    [JsonPropertyName("documento")]
    public DocumentoInfo? Documento { get; set; }

    [JsonPropertyName("cliente")]
    public ClienteInfo? Cliente { get; set; }

    [JsonPropertyName("items")]
    public List<TicketItem> Items { get; set; } = new();

    [JsonPropertyName("totales")]
    public TotalesInfo? Totales { get; set; }

    [JsonPropertyName("pago")]
    public PagoInfo? Pago { get; set; }

    [JsonPropertyName("pie")]
    public string? Pie { get; set; }

    [JsonPropertyName("qr")]
    public string? Qr { get; set; }

    /// <summary>Logo del negocio como data URI base64 (data:image/png;base64,...). Opcional.</summary>
    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    /// <summary>
    /// Origen (URL base) del POS que envió el ticket, ej. "https://ventorypos.macsoftperu.com".
    /// El agente lo guarda para saber a qué servidor pedirle actualizaciones.
    /// </summary>
    [JsonPropertyName("origen")]
    public string? Origen { get; set; }

    [JsonPropertyName("abrirCajon")]
    public bool AbrirCajon { get; set; }

    [JsonPropertyName("copias")]
    public int Copias { get; set; } = 1;

    [JsonPropertyName("anchoPapelMm")]
    public int? AnchoPapelMm { get; set; }
}

public sealed class NegocioInfo
{
    [JsonPropertyName("nombre")]      public string? Nombre { get; set; }
    [JsonPropertyName("ruc")]         public string? Ruc { get; set; }
    [JsonPropertyName("direccion")]   public string? Direccion { get; set; }
    [JsonPropertyName("telefono")]    public string? Telefono { get; set; }
    /// <summary>El POS puede pedir ocultar el RUC del negocio en el ticket.</summary>
    [JsonPropertyName("mostrarRuc")]  public bool MostrarRuc { get; set; } = true;
    /// <summary>El POS puede pedir ocultar el desglose de IGV en el ticket.</summary>
    [JsonPropertyName("mostrarIgv")]  public bool MostrarIgv { get; set; } = false;
    /// <summary>Escala del logo en % (10-200). 100 = ancho máximo del papel.</summary>
    [JsonPropertyName("logoEscala")]  public int LogoEscala { get; set; } = 100;
}

public sealed class DocumentoInfo
{
    [JsonPropertyName("tipo")]     public string? Tipo { get; set; }
    [JsonPropertyName("serie")]    public string? Serie { get; set; }
    [JsonPropertyName("numero")]   public string? Numero { get; set; }
    [JsonPropertyName("fecha")]    public string? Fecha { get; set; }
    [JsonPropertyName("vendedor")] public string? Vendedor { get; set; }
    [JsonPropertyName("caja")]     public string? Caja { get; set; }

    /// <summary>Líneas adicionales ya formateadas por el POS; se imprimen tal cual
    /// al final de la sección. Permite agregar datos sin recompilar el agente.</summary>
    [JsonPropertyName("extra")]    public List<string>? Extra { get; set; }
}

public sealed class ClienteInfo
{
    [JsonPropertyName("nombre")]    public string? Nombre { get; set; }
    [JsonPropertyName("doc")]       public string? Doc { get; set; }
    [JsonPropertyName("direccion")] public string? Direccion { get; set; }
    [JsonPropertyName("telefono")]  public string? Telefono { get; set; }

    /// <summary>Líneas adicionales ya formateadas por el POS; se imprimen tal cual
    /// al final de la sección. Permite agregar datos sin recompilar el agente.</summary>
    [JsonPropertyName("extra")]     public List<string>? Extra { get; set; }
}

public sealed class TicketItem
{
    [JsonPropertyName("cant")]    public decimal Cant { get; set; }
    [JsonPropertyName("desc")]    public string Desc { get; set; } = "";
    [JsonPropertyName("precio")]  public decimal Precio { get; set; }
    [JsonPropertyName("importe")] public decimal Importe { get; set; }
    [JsonPropertyName("unidad")]  public string? Unidad { get; set; }
}

public sealed class TotalesInfo
{
    [JsonPropertyName("subtotal")]  public decimal? Subtotal { get; set; }
    [JsonPropertyName("igv")]       public decimal? Igv { get; set; }
    [JsonPropertyName("descuento")] public decimal? Descuento { get; set; }
    [JsonPropertyName("total")]     public decimal Total { get; set; }
    [JsonPropertyName("moneda")]    public string? Moneda { get; set; }
}

public sealed class PagoInfo
{
    [JsonPropertyName("metodo")]   public string? Metodo { get; set; }
    [JsonPropertyName("recibido")] public decimal? Recibido { get; set; }
    [JsonPropertyName("vuelto")]   public decimal? Vuelto { get; set; }
}
