using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VentoryPrint.Services;

/// <summary>
/// Actualización automática del agente. Pregunta al manifiesto version.json
/// (la URL se obtiene de <see cref="AgentSettings.UpdateManifestUrl"/> o se resuelve
/// como {UpdateBaseUrl}/agent/version.json). Si hay versión más nueva descarga
/// el nuevo VentoryPrint.exe, verifica su SHA256 y lo aplica reiniciando.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateService
{
    /// <summary>Fallback mientras el agente aún no ha recibido su primer ticket.</summary>
    public const string DefaultBaseUrl = "https://ventorypos.macsoftperu.com";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly SettingsService _settings;

    public UpdateService(SettingsService settings) => _settings = settings;

    public sealed class Manifest
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("url")]     public string Url { get; set; } = "";
        [JsonPropertyName("sha256")]  public string Sha256 { get; set; } = "";
        [JsonPropertyName("notas")]   public string? Notas { get; set; }
    }

    public string BaseUrl
    {
        get
        {
            var b = _settings.Load()?.UpdateBaseUrl;
            return string.IsNullOrWhiteSpace(b) ? DefaultBaseUrl : b.TrimEnd('/');
        }
    }

    /// <summary>Devuelve el manifiesto si hay una versión MÁS NUEVA que la actual; si no, null.</summary>
    public async Task<Manifest?> CheckAsync(string currentVersion)
    {
        var url = ManifestUrl();
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        var m = JsonSerializer.Deserialize<Manifest>(json);
        if (m is null || string.IsNullOrWhiteSpace(m.Version)) return null;

        return IsNewer(m.Version, currentVersion) ? m : null;
    }

    /// <summary>
    /// URL del manifiesto de actualizaciones. Usa <see cref="AgentSettings.UpdateManifestUrl"/>
    /// si esta configurada; si no, resuelve contra <see cref="BaseUrl"/>.
    /// </summary>
    private string ManifestUrl()
    {
        var explicitUrl = _settings.Load()?.UpdateManifestUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitUrl)) return explicitUrl!;
        return BaseUrl + "/agent/version.json";
    }

    /// <summary>Descarga el exe nuevo a %TEMP%, valida SHA256 (si viene) y devuelve la ruta.</summary>
    public async Task<string> DownloadAsync(Manifest m)
    {
        var url = m.Url;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = BaseUrl + "/" + url.TrimStart('/'); // url relativa → resuelve contra el POS

        var bytes = await Http.GetByteArrayAsync(url);

        if (!string.IsNullOrWhiteSpace(m.Sha256))
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(hash, m.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "El archivo descargado no coincide con el hash esperado (SHA256). Actualización cancelada.");
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"VentoryPrint.update.{m.Version}.exe");
        await File.WriteAllBytesAsync(tmp, bytes);
        return tmp;
    }

    /// <summary>
    /// Lanza el exe recién descargado en modo --apply-update: esperará a que ESTE proceso
    /// cierre, se copiará sobre el exe actual y relanzará el agente. Tras llamar a esto,
    /// el proceso actual debe salir.
    /// </summary>
    public static void LaunchApplier(string newExeTemp, string targetExe)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = newExeTemp,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(targetExe);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        System.Diagnostics.Process.Start(psi);
    }

    internal static bool IsNewer(string remote, string current) =>
        Parse(remote).CompareTo(Parse(current)) > 0;

    private static Version Parse(string v)
    {
        var clean = new string(v.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(clean, out var ver) ? ver : new Version(0, 0, 0);
    }
}
