using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using VentoryPrint.Services;

namespace VentoryPrint.Cli;

/// <summary>
/// Autostart por usuario vía HKCU\...\Run: al iniciar sesión, el agente
/// arranca minimizado en la bandeja e imprime sin que nadie toque nada.
/// Si el host es "+" (todas las interfaces) se necesita un URL ACL en
/// HTTP.sys; lo registramos con netsh al instalar el autostart (UAC una
/// sola vez). Después el agente puede arrancar sin privilegios elevados.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutostartCli
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VentoryPrint";

    public static int Install()
    {
        var settings = new SettingsService().Load();
        var acl = EnsureUrlAcl(settings);

        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("No se pudo determinar la ruta del .exe");

        var command = $"\"{exePath}\" --autostart";

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("No se pudo abrir HKCU\\" + RunKey);

        key.SetValue(ValueName, command);

        Console.WriteLine();
        Console.WriteLine("Autostart instalado: " + command);
        Console.WriteLine("El agente arrancará automáticamente al iniciar sesión en esta PC.");
        if (!acl.Ok)
        {
            Console.WriteLine();
            Console.WriteLine("ADVERTENCIA: no se pudo registrar el permiso de red (URL ACL) para el host configurado.");
            Console.WriteLine(acl.Message);
            Console.WriteLine("El agente puede no escuchar al arrancar. Ejecuta como administrador:");
            Console.WriteLine($"  netsh http add urlacl url={acl.Url} user=\\\"{acl.User}\"");
        }
        return 0;
    }

    public static int Uninstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            Console.WriteLine("No había entrada de autostart.");
            return 0;
        }

        var existed = key.GetValue(ValueName) is not null;
        key.DeleteValue(ValueName, throwOnMissingValue: false);

        Console.WriteLine(existed
            ? "Autostart desinstalado. El agente ya no arrancará con Windows."
            : "No había entrada de autostart.");
        return 0;
    }

    public static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static int Status()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(ValueName) as string;

        Console.WriteLine(value is null
            ? "Autostart: NO instalado. Actívalo con: .\\VentoryPrint.exe --install-autostart"
            : "Autostart: ACTIVO -> " + value);
        return 0;
    }

    /// <summary>Registra el URL ACL necesario para escuchar en un host no localhost.</summary>
    public static (bool Ok, string Url, string User, string Message) EnsureUrlAcl(VentoryPrint.Models.AgentSettings? settings)
    {
        var host = string.IsNullOrWhiteSpace(settings?.Host) ? "127.0.0.1" : settings!.Host.Trim();
        var port = settings?.Port ?? 9111;

        if (host is "127.0.0.1" or "localhost")
            return (true, "", "", "Localhost no requiere URL ACL.");

        var url = $"http://{host}:{port}/";
        var user = WindowsIdentity.GetCurrent().Name;

        // netsh http add urlacl url=http://+:9111/ user=DOMINIO\usuario
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"http add urlacl url={url} user=\"{user}\"",
            Verb = "runas",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, url, user, "No se pudo iniciar netsh (¿UAC cancelado?).");

            proc.WaitForExit(15000);
            var exit = proc.ExitCode;

            // 0 = éxito, 183 = ya existe (ERROR_ALREADY_EXISTS).
            if (exit == 0 || exit == 183)
                return (true, url, user, "URL ACL registrado correctamente.");

            return (false, url, user, $"netsh devolvió el código {exit}.");
        }
        catch (Exception ex)
        {
            return (false, url, user, "Excepción al ejecutar netsh: " + ex.Message);
        }
    }
}
