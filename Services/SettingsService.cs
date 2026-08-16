using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VentoryPrint.Models;

namespace VentoryPrint.Services;

/// <summary>
/// Persiste la configuración en %APPDATA%\VentoryPrint\config.json.
/// El token de la caja se cifra con DPAPI (solo el usuario de Windows que
/// lo guardó puede leerlo).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VentoryPrint");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public bool IsConfigured()
    {
        var s = Load();
        return s is not null && !string.IsNullOrWhiteSpace(s.PrinterName);
    }

    public AgentSettings? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            var s = JsonSerializer.Deserialize<AgentSettings>(json);
            if (s is null) return null;
            if (s.Port is < 1024 or > 65535) s.Port = 9111;
            if (string.IsNullOrWhiteSpace(s.Host)) s.Host = "127.0.0.1";
            if (s.ColName <= 0) s.ColName = 26;
            if (s.ColQty  <= 0) s.ColQty  = 5;
            if (s.ColPu   <= 0) s.ColPu   = 8;
            if (s.ColSub  <= 0) s.ColSub  = 9;
            return s;
        }
        catch
        {
            return null;
        }
    }

    public void Save(AgentSettings settings)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    /// <summary>Agrega o reemplaza un token de caja en la lista.</summary>
    public void SetToken(AgentSettings settings, string plainToken)
    {
        var protectedToken = plainToken.Length == 0
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainToken),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser));

        // Si ya existe el mismo token (comparando en claro), no duplicar.
        var existing = GetTokensPlain(settings);
        if (existing.Contains(plainToken))
        {
            // Actualizar el protected por si acaso, manteniendo posición.
            var idx = existing.IndexOf(plainToken);
            settings.TokensProtectedBase64[idx] = protectedToken;
            return;
        }

        settings.TokensProtectedBase64.Add(protectedToken);
    }

    /// <summary>Elimina un token de la lista.</summary>
    public void RemoveToken(AgentSettings settings, string plainToken)
    {
        var existing = GetTokensPlain(settings);
        var idx = existing.IndexOf(plainToken);
        if (idx >= 0)
            settings.TokensProtectedBase64.RemoveAt(idx);
    }

    /// <summary>Todos los tokens en claro.</summary>
    public List<string> GetTokensPlain(AgentSettings? settings = null)
    {
        var s = settings ?? Load();
        if (s is null) return new List<string>();

        var result = new List<string>();
        foreach (var protectedToken in s.TokensProtectedBase64)
        {
            if (string.IsNullOrEmpty(protectedToken)) continue;
            try
            {
                var bytes = Convert.FromBase64String(protectedToken);
                var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                result.Add(Encoding.UTF8.GetString(plain));
            }
            catch { /* token corrupto, lo ignoramos */ }
        }
        return result;
    }

    /// <summary>Primer token en claro, o "" si no hay. Compatibilidad.</summary>
    public string GetTokenPlain()
    {
        var tokens = GetTokensPlain();
        return tokens.FirstOrDefault() ?? "";
    }

    public static string LogsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VentoryPrint", "logs");
}
