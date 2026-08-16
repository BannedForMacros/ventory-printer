using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Serilog;
using VentoryPrint.Cli;
using VentoryPrint.Services;
using VentoryPrint.Ui;

[assembly: SupportedOSPlatform("windows")]

namespace VentoryPrint;

public static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var mode = args.FirstOrDefault();
        if (mode is "--service" or "--test-print" or "--test-print-shift"
            or "--install-autostart" or "--uninstall-autostart" or "--autostart-status"
            or "--install-autostart-elevated" or "--uninstall-autostart-elevated"
            or "--help" or "-h" or "/?")
        {
            // WinExe no abre consola propia; nos enganchamos a la del padre
            // (cmd/PowerShell) para que Console.WriteLine funcione normal.
            AttachConsole(ATTACH_PARENT_PROCESS);
        }

        switch (mode)
        {
            case "--apply-update":         return ApplyUpdate(args);
            case "--service":              return RunHeadlessAsync().GetAwaiter().GetResult();
            case "--test-print":           return TestPrintCli.Run();
            case "--test-print-shift":     return TestShiftClosureCli.Run();
            case "--install-autostart":    return AutostartCli.Install();
            case "--uninstall-autostart":  return AutostartCli.Uninstall();
            case "--autostart-status":     return AutostartCli.Status();
            case "--install-autostart-elevated":   return ShowElevatedResult(AutostartCli.InstallElevated(), "instalar");
            case "--uninstall-autostart-elevated": return ShowElevatedResult(AutostartCli.UninstallElevated(), "desinstalar");
            case "--help":
            case "-h":
            case "/?":
                PrintHelp();
                return 0;
        }

        // Default: GUI. Con --autostart abre minimizada al tray y arranca el agente.
        var autostart = args.Contains("--autostart");
        return RunGui(autostart);
    }

    private static int RunGui(bool autostart)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(autostart));
        return 0;
    }

    private static async Task<int> RunHeadlessAsync()
    {
        AgentRunner.ConfigureLogger();

        var settings = new SettingsService();
        if (!settings.IsConfigured())
        {
            Console.WriteLine();
            Console.WriteLine("No hay configuración. Abre VentoryPrint.exe sin parámetros para usar la interfaz.");
            Console.WriteLine();
            return 1;
        }

        try
        {
            using var host = AgentRunner.BuildHost();
            Log.Information("VentoryPrint {V} (--service) iniciando...", Hosting.PrintServer.Version);
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fallo fatal");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Modo interno de auto-actualización. Este exe es la COPIA NUEVA (en %TEMP%):
    /// espera a que el agente viejo cierre, se copia sobre él y lo relanza.
    /// args: --apply-update &lt;rutaExeDestino&gt; &lt;pidViejo&gt;
    /// </summary>
    private static int ApplyUpdate(string[] args)
    {
        try
        {
            var target = args.Length > 1 ? args[1] : "";
            var pid = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 0;
            if (string.IsNullOrWhiteSpace(target)) return 1;

            // Esperar a que el proceso viejo termine para que suelte el .exe.
            if (pid > 0)
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    proc.WaitForExit(15000);
                }
                catch { /* ya no existe: perfecto */ }
            }

            var self = Environment.ProcessPath!;

            // Reintentos por si Windows aún mantiene bloqueado el archivo.
            var copiado = false;
            for (var i = 0; i < 20 && !copiado; i++)
            {
                try { File.Copy(self, target, overwrite: true); copiado = true; }
                catch { System.Threading.Thread.Sleep(500); }
            }
            if (!copiado) return 1;

            // Relanzar el agente ya actualizado, minimizado al tray como venía.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                Arguments = "--autostart",
                UseShellExecute = true,
            });
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static int ShowElevatedResult(int exitCode, string action)
    {
        var title = "VentoryPrint";
        var doneText = action == "instalar" ? "instalado" : "desinstalado";
        if (exitCode == 0)
        {
            MessageBox.Show(
                $"Inicio automatico {doneText} correctamente con permisos de administrador.",
                title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(
                $"No se pudo {action} el inicio automatico. " +
                "Abre una consola (cmd/PowerShell) como administrador y ejecuta:\n\n" +
                $"  .\\VentoryPrint.exe --{(action == "instalar" ? "install" : "uninstall")}-autostart-elevated\n\n" +
                "Asi veras el mensaje de error exacto.",
                title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return exitCode;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("VentoryPrint - Agente local de impresión de ventoryPOS");
        Console.WriteLine();
        Console.WriteLine("Uso:");
        Console.WriteLine("  VentoryPrint.exe                  Abre la interfaz gráfica (recomendado)");
        Console.WriteLine("  VentoryPrint.exe --autostart      GUI minimizada al tray + agente corriendo");
        Console.WriteLine("  VentoryPrint.exe --service        Solo el agente en consola (sin GUI)");
        Console.WriteLine();
        Console.WriteLine("Utilitarios:");
        Console.WriteLine("  --test-print              Imprime un ticket de prueba");
        Console.WriteLine("  --test-print-shift        Imprime un reporte de cierre de turno de prueba");
        Console.WriteLine("  --install-autostart       Activa el arranque con Windows (solo localhost)");
        Console.WriteLine("  --install-autostart-elevated  Activa el arranque con admin (host '+')");
        Console.WriteLine("  --uninstall-autostart     Lo desactiva");
        Console.WriteLine("  --uninstall-autostart-elevated  Desactiva el modo admin");
        Console.WriteLine("  --autostart-status        Estado del autostart");
        Console.WriteLine("  --help                    Esta ayuda");
    }
}
