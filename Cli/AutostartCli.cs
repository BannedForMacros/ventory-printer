using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using VentoryPrint.Services;

namespace VentoryPrint.Cli;

/// <summary>
/// Autostart por usuario vía HKCU\...\Run o vía tarea programada elevada.
///
/// - Modo simple (HKCU): funciona perfecto para host 127.0.0.1 / localhost.
/// - Modo elevado (tarea programada): necesario cuando el host es "+" (todas las
///   interfaces) porque HTTP.sys exige un URL ACL y el proceso autostart debe
///   tener privilegios de administrador para agregarlo y para escuchar.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutostartCli
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VentoryPrint";
    private const string TaskName = "VentoryPrintAgent";

    /// <summary>Modo simple: solo HKCU. Recomendado para localhost.</summary>
    public static int Install()
    {
        var settings = new SettingsService().Load();
        if (settings is null)
        {
            Console.WriteLine("No hay configuracion guardada. Guarda la configuracion antes de activar el inicio automatico.");
            return 1;
        }

        var host = string.IsNullOrWhiteSpace(settings.Host) ? "127.0.0.1" : settings.Host.Trim();
        if (host is not "127.0.0.1" and not "localhost")
        {
            Console.WriteLine();
            Console.WriteLine($"ADVERTENCIA: el host esta configurado como '{host}', lo que requiere permiso de red (URL ACL).");
            Console.WriteLine("Para que el agente inicie solo al arrancar Windows, usa la UI y el boton 'Reparar inicio automatico (admin)'");
            Console.WriteLine("o ejecuta UNA vez como administrador: .\\VentoryPrint.exe --install-autostart-elevated");
            return 1;
        }

        InstallHkcu();
        Console.WriteLine();
        Console.WriteLine("Autostart instalado. El agente arrancara al iniciar sesion.");
        return 0;
    }

    private static void InstallHkcu()
    {
        var exePath = GetExePath();
        var command = $"\"{exePath}\" --autostart";
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("No se pudo abrir HKCU\\" + RunKey);
        key.SetValue(ValueName, command);
    }

    /// <summary>
    /// Modo elevado: se ejecuta como administrador, registra el URL ACL necesario
    /// y crea una tarea programada que inicia el agente con privilegios altos.
    /// </summary>
    public static int InstallElevated()
    {
        LogElevated("=== Iniciando instalacion elevada ===");
        var settings = new SettingsService().Load();
        if (settings is null)
        {
            LogElevated("ERROR: No hay configuracion guardada.");
            Console.WriteLine("No hay configuracion guardada.");
            return 1;
        }

        LogElevated($"Host configurado: {settings.Host}, Puerto: {settings.Port}");
        var acl = EnsureUrlAcl(settings);
        if (!acl.Ok)
        {
            LogElevated($"ERROR URL ACL: {acl.Message}");
            Console.WriteLine();
            Console.WriteLine("ERROR: no se pudo registrar el permiso de red (URL ACL):");
            Console.WriteLine(acl.Message);
            return 1;
        }
        LogElevated("URL ACL OK.");

        var exePath = GetExePath();
        var tr = $"\\\"{exePath}\\\" --autostart";
        var arguments = $"/Create /TN \"{TaskName}\" /TR \"{tr}\" /SC ONLOGON /RL HIGHEST /F";
        LogElevated($"schtasks arguments: {arguments}");

        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                LogElevated("ERROR: no se pudo iniciar schtasks (proc null).");
                Console.WriteLine("ERROR: no se pudo iniciar schtasks.");
                return 1;
            }
            proc.WaitForExit(15000);
            LogElevated($"schtasks ExitCode: {proc.ExitCode}");
            if (proc.ExitCode != 0)
            {
                Console.WriteLine($"ERROR: schtasks devolvio el codigo {proc.ExitCode}.");
                return 1;
            }

            // Si quedaba una entrada HKCU antigua, la quitamos para no duplicar inicios.
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName);
                LogElevated("Entrada HKCU antigua eliminada.");
            }

            LogElevated("Instalacion elevada completada.");
            Console.WriteLine();
            Console.WriteLine("Autostart instalado con privilegios de administrador.");
            Console.WriteLine("El agente arrancara solo al iniciar sesion, incluso con host '+' (todas las interfaces).");
            return 0;
        }
        catch (Exception ex)
        {
            LogElevated($"ERROR excepcion schtasks: {ex}");
            Console.WriteLine("ERROR al crear tarea programada: " + ex.Message);
            return 1;
        }
    }

    public static int Uninstall()
    {
        bool removed = false;

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName);
            removed = true;
        }

        if (IsElevatedTaskInstalled())
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{TaskName}\" /F",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                if (proc?.ExitCode == 0) removed = true;
            }
            catch { /* si el usuario cancela UAC, dejamos la tarea */ }
        }

        Console.WriteLine(removed
            ? "Autostart desinstalado."
            : "No habia entrada de autostart.");
        return 0;
    }

    public static int UninstallElevated()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);

        if (!IsElevatedTaskInstalled())
        {
            Console.WriteLine("No habia tarea elevada de autostart.");
            return 0;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Delete /TN \"{TaskName}\" /F",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.WriteLine("No se pudo iniciar schtasks.");
                return 1;
            }
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
            {
                Console.WriteLine($"schtasks devolvio el codigo {proc.ExitCode}.");
                return 1;
            }
            Console.WriteLine("Tarea elevada de autostart eliminada.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error al eliminar tarea: " + ex.Message);
            return 1;
        }
    }

    public static bool IsInstalled() => IsHkcuInstalled() || IsElevatedTaskInstalled();

    public static bool IsHkcuInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static bool IsElevatedTaskInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\" /FO CSV /NH",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    public static int Status()
    {
        var hkcu = IsHkcuInstalled();
        var task = IsElevatedTaskInstalled();
        var value = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(ValueName) as string;

        if (hkcu && task)
            Console.WriteLine("Autostart: ACTIVO (HKCU + tarea elevada).");
        else if (task)
            Console.WriteLine("Autostart: ACTIVO (tarea elevada -> admin).");
        else if (hkcu)
            Console.WriteLine("Autostart: ACTIVO (HKCU) -> " + value);
        else
            Console.WriteLine("Autostart: NO instalado.");

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

        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"http add urlacl url={url} user=\"{user}\"",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        // Si ya somos admin, ejecutamos netsh directamente y capturamos su salida
        // para diagnosticar. Si no, pedimos UAC.
        if (IsCurrentProcessElevated())
        {
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        else
        {
            psi.Verb = "runas";
            psi.UseShellExecute = true;
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, url, user, "No se pudo iniciar netsh (UAC cancelado?).");

            var output = psi.RedirectStandardOutput ? proc.StandardOutput.ReadToEnd() : "";
            var error = psi.RedirectStandardError ? proc.StandardError.ReadToEnd() : "";
            proc.WaitForExit(15000);
            var exit = proc.ExitCode;
            var detail = string.Join(" ", new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

            // 0 = exito, 183 = ya existe (ERROR_ALREADY_EXISTS).
            // En algunos Windows en español netsh devuelve 1 pero el mensaje interno contiene "Error: 183".
            var alreadyExists = exit == 183
                || detail.Contains("183")
                || detail.Contains("ya existe", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("already exists", StringComparison.OrdinalIgnoreCase);

            if (exit == 0 || alreadyExists)
                return (true, url, user, "URL ACL registrado correctamente." + (string.IsNullOrWhiteSpace(detail) ? "" : $" Detalle: {detail}"));

            return (false, url, user, $"netsh devolvio el codigo {exit}." + (string.IsNullOrWhiteSpace(detail) ? "" : $" Mensaje: {detail}"));
        }
        catch (Exception ex)
        {
            return (false, url, user, "Excepcion al ejecutar netsh: " + ex.Message);
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string GetExePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("No se pudo determinar la ruta del .exe");
    }

    private static void LogElevated(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VentoryPrint", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "autostart-install.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* no dejar que fallar el log rompa la instalacion */ }
    }
}
