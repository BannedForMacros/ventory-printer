using System.Text.Json.Serialization;

namespace VentoryPrint.Models;

/// <summary>
/// Contrato del reporte de cierre de turno que envía ventoryPOS.
/// Todos los montos son numéricos; el agente se encarga del formato e impresión.
/// </summary>
public sealed class ShiftClosurePayload
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("negocio")]
    public NegocioInfo? Negocio { get; set; }

    /// <summary>Datos del turno que se está cerrando.</summary>
    [JsonPropertyName("turno")]
    public TurnoInfo? Turno { get; set; }

    /// <summary>Resumen global de ventas realizadas durante el turno.</summary>
    [JsonPropertyName("resumen")]
    public ResumenCierre? Resumen { get; set; }

    /// <summary>Consolidado por cada método de pago usado en el turno.</summary>
    [JsonPropertyName("metodosPago")]
    public List<MetodoPagoCierre> MetodosPago { get; set; } = new();

    /// <summary>Consolidado de la caja (efectivo: apertura, ventas, movimientos, arqueo).</summary>
    [JsonPropertyName("caja")]
    public CajaCierre? Caja { get; set; }

    /// <summary>Logo del negocio como data URI base64. Opcional.</summary>
    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    /// <summary>Origen del POS que envió el cierre.</summary>
    [JsonPropertyName("origen")]
    public string? Origen { get; set; }

    /// <summary>Pie de página. Si no viene, se usa un texto por defecto.</summary>
    [JsonPropertyName("pie")]
    public string? Pie { get; set; }

    /// <summary>Ancho de papel en mm (58/80). Si no viene, usa la config local.</summary>
    [JsonPropertyName("anchoPapelMm")]
    public int? AnchoPapelMm { get; set; }

    /// <summary>Número de copias a imprimir (1-5).</summary>
    [JsonPropertyName("copias")]
    public int Copias { get; set; } = 1;
}

public sealed class TurnoInfo
{
    [JsonPropertyName("id")]            public string? Id { get; set; }
    [JsonPropertyName("nombre")]        public string? Nombre { get; set; }
    [JsonPropertyName("cajero")]        public string? Cajero { get; set; }
    [JsonPropertyName("caja")]          public string? Caja { get; set; }
    [JsonPropertyName("fechaApertura")] public string? FechaApertura { get; set; }
    [JsonPropertyName("fechaCierre")]   public string? FechaCierre { get; set; }
}

public sealed class ResumenCierre
{
    [JsonPropertyName("numeroVentas")] public int? NumeroVentas { get; set; }
    [JsonPropertyName("subtotal")]     public decimal? Subtotal { get; set; }
    [JsonPropertyName("igv")]          public decimal? Igv { get; set; }
    [JsonPropertyName("descuento")]    public decimal? Descuento { get; set; }
    [JsonPropertyName("total")]        public decimal Total { get; set; }
    [JsonPropertyName("moneda")]       public string? Moneda { get; set; }
}

public sealed class MetodoPagoCierre
{
    [JsonPropertyName("nombre")]   public string Nombre { get; set; } = "";
    [JsonPropertyName("monto")]    public decimal Monto { get; set; }
    [JsonPropertyName("cantidad")] public int? Cantidad { get; set; }
}

public sealed class CajaCierre
{
    /// <summary>Efectivo con el que se aperturó la caja.</summary>
    [JsonPropertyName("montoApertura")]      public decimal? MontoApertura { get; set; }

    /// <summary>Total vendido en efectivo (sin apertura).</summary>
    [JsonPropertyName("ventasEfectivo")]     public decimal? VentasEfectivo { get; set; }

    /// <summary>Entradas de caja (depósitos manuales).</summary>
    [JsonPropertyName("entradas")]           public decimal? Entradas { get; set; }

    /// <summary>Salidas de caja (retiros manuales).</summary>
    [JsonPropertyName("salidas")]            public decimal? Salidas { get; set; }

    /// <summary>Efectivo que debería haber: apertura + ventas efectivo + entradas - salidas.</summary>
    [JsonPropertyName("efectivoEsperado")]   public decimal? EfectivoEsperado { get; set; }

    /// <summary>Efectivo contado físicamente al cerrar.</summary>
    [JsonPropertyName("efectivoDeclarado")]  public decimal? EfectivoDeclarado { get; set; }

    /// <summary>Diferencia entre efectivo declarado y esperado.</summary>
    [JsonPropertyName("diferencia")]         public decimal? Diferencia { get; set; }
}
