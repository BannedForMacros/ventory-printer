using System.Text.Json.Serialization;

namespace VentoryPrint.Models;

/// <summary>
/// Configuración persistida en %APPDATA%\VentoryPrint\config.json.
/// Los tokens se guardan cifrados con DPAPI (solo el usuario de Windows los lee).
/// </summary>
public sealed class AgentSettings
{
    public string PrinterName { get; set; } = "";
    public int Port { get; set; } = 9111;

    /// <summary>
    /// Host donde escucha el agente. Default 127.0.0.1 (solo esta PC).
    /// Usar "+" para escuchar en todas las interfaces de red (necesita netsh).
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Tokens de caja permitidos (cifrados). Puede haber varios.</summary>
    public List<string> TokensProtectedBase64 { get; set; } = new();

    /// <summary>
    /// Compatibilidad con config.json antiguo que tenía un solo token.
    /// Al deserializar se migra automáticamente a <see cref="TokensProtectedBase64"/>.
    /// </summary>
    [JsonPropertyName("tokenProtectedBase64")]
    public string? LegacyTokenProtectedBase64
    {
        get => null;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!TokensProtectedBase64.Contains(value))
                TokensProtectedBase64.Insert(0, value);
        }
    }

    /// <summary>Nombre visible de la caja (referencial, sale en /status).</summary>
    public string CajaNombre { get; set; } = "";

    // Anchos de columna del ticket. Total = ColName + ColQty + ColPu + ColSub.
    // 80mm típico = 48 cols (26+5+8+9). 58mm típico = 32 cols (14+4+6+8).
    public int ColName { get; set; } = 26;
    public int ColQty  { get; set; } = 5;
    public int ColPu   { get; set; } = 8;
    public int ColSub  { get; set; } = 9;

    /// <summary>Abrir gaveta en cada impresión aunque el POS no lo pida.</summary>
    public bool AbrirCajonSiempre { get; set; } = false;

    /// <summary>
    /// URL base del servidor del POS (se auto-descubre desde los tickets que llegan).
    /// El agente le pide aqui /agent/version.json para las actualizaciones.
    /// </summary>
    public string UpdateBaseUrl { get; set; } = "";

    /// <summary>
    /// URL completa del manifiesto version.json para actualizaciones automaticas.
    /// Si se configura, tiene prioridad sobre <see cref="UpdateBaseUrl"/>.
    /// Ejemplo: https://github.com/usuario/repo/releases/latest/download/version.json
    /// </summary>
    public string UpdateManifestUrl { get; set; } = "";


    public int TotalCols => ColName + ColQty + ColPu + ColSub;
}
