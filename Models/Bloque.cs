using System.Text.Json;
using System.Text.Json.Serialization;

namespace VentoryPrint.Models;

/// <summary>
/// Un bloque del ticket por plantilla. El POS arma el ticket como una lista de
/// bloques y el agente solo los dibuja: cambiar el diseño de un ticket ya no
/// requiere publicar una versión del agente.
///
/// El POS no conoce el ancho del papel (58 u 80 mm, o columnas a medida): por
/// eso ningún bloque trae anchos. El agente parte las líneas, alinea los pares
/// y reparte las columnas de las tablas según el papel de cada caja.
///
/// Tipos (<see cref="Tipo"/>), con los campos que usa cada uno:
/// <list type="bullet">
/// <item><c>texto</c>: texto, alinear, tamano, negrita, invertido.</item>
/// <item><c>pares</c>: items [{etiqueta, valor, negrita, tamano}], estilo
///   ("columna": etiquetas en columna, "extremos": valor a la derecha).</item>
/// <item><c>tabla</c>: columnas [{titulo, alinear, flexible}], filas [[...]],
///   encabezado, estilo ("auto", "fila", "dosLineas").</item>
/// <item><c>recuadro</c>: lineas [{texto, tamano, negrita, alinear, separador}],
///   alinear, modo ("imagen" o "texto"), borde ("linea", "doble", "ascii").</item>
/// <item><c>banda</c>: igual que recuadro, en blanco sobre negro.</item>
/// <item><c>linea</c>: caracter (por defecto "-").</item>
/// <item><c>espacio</c>: n (líneas en blanco).</item>
/// <item><c>qr</c>: datos, escala (tamaño del módulo, 1 a 16).</item>
/// <item><c>logo</c>: escala (% del ancho). Usa el logo del ticket.</item>
/// </list>
/// Un tipo desconocido se salta: un POS más nuevo nunca rompe un agente más viejo.
/// </summary>
public sealed class Bloque
{
    [JsonPropertyName("tipo")]       public string? Tipo { get; set; }

    [JsonPropertyName("texto")]      public string? Texto { get; set; }
    /// <summary>"izq", "centro" o "der".</summary>
    [JsonPropertyName("alinear")]    public string? Alinear { get; set; }
    /// <summary>"normal", "alto" (doble alto), "ancho" (doble ancho) o "grande" (doble en ambos).</summary>
    [JsonPropertyName("tamano")]     public string? Tamano { get; set; }
    [JsonPropertyName("negrita")]    public bool Negrita { get; set; }
    [JsonPropertyName("invertido")]  public bool Invertido { get; set; }

    [JsonPropertyName("items")]      public List<ParBloque>? Items { get; set; }
    [JsonPropertyName("estilo")]     public string? Estilo { get; set; }

    [JsonPropertyName("columnas")]   public List<ColumnaBloque>? Columnas { get; set; }
    [JsonPropertyName("filas")]      public List<List<string?>>? Filas { get; set; }
    [JsonPropertyName("encabezado")] public bool? Encabezado { get; set; }

    [JsonPropertyName("lineas")]     public List<LineaBloque>? Lineas { get; set; }
    [JsonPropertyName("modo")]       public string? Modo { get; set; }
    [JsonPropertyName("borde")]      public string? Borde { get; set; }

    [JsonPropertyName("caracter")]   public string? Caracter { get; set; }
    [JsonPropertyName("n")]          public int? N { get; set; }
    [JsonPropertyName("datos")]      public string? Datos { get; set; }
    [JsonPropertyName("escala")]     public int? Escala { get; set; }
}

public sealed class ParBloque
{
    [JsonPropertyName("etiqueta")] public string? Etiqueta { get; set; }
    [JsonPropertyName("valor")]    public string? Valor { get; set; }
    [JsonPropertyName("negrita")]  public bool Negrita { get; set; }
    [JsonPropertyName("tamano")]   public string? Tamano { get; set; }
}

public sealed class ColumnaBloque
{
    [JsonPropertyName("titulo")]   public string? Titulo { get; set; }
    /// <summary>"izq" o "der". Por defecto: la flexible a la izquierda, el resto a la derecha.</summary>
    [JsonPropertyName("alinear")]  public string? Alinear { get; set; }
    /// <summary>La columna que se queda con el espacio sobrante y parte su texto (la descripción).</summary>
    [JsonPropertyName("flexible")] public bool Flexible { get; set; }
}

public sealed class LineaBloque
{
    [JsonPropertyName("texto")]     public string? Texto { get; set; }
    [JsonPropertyName("tamano")]    public string? Tamano { get; set; }
    [JsonPropertyName("negrita")]   public bool Negrita { get; set; }
    [JsonPropertyName("alinear")]   public string? Alinear { get; set; }
    /// <summary>Raya divisoria dentro del recuadro en lugar de texto.</summary>
    [JsonPropertyName("separador")] public bool Separador { get; set; }
}

/// <summary>Opciones con las que el agente lee lo que manda el POS.</summary>
public static class TicketJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new TextoFlexibleConverter() },
    };
}

/// <summary>
/// Acepta un número o un booleano donde se espera texto. PHP manda 20 en lugar
/// de "20" con facilidad, y un ticket no debe rechazarse por eso.
/// </summary>
public sealed class TextoFlexibleConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"Se esperaba texto y llegó {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
