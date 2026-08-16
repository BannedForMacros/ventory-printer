using System.IO;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VentoryPrint.Hosting;
using VentoryPrint.Printing;
using VentoryPrint.Services;

namespace VentoryPrint.Ui;

/// <summary>
/// Envuelve el IHost del agente para que la UI pueda Start/Stop sin bloquear.
/// Una sola instancia por proceso.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentRunner
{
    private IHost? _host;
    private readonly object _gate = new();

    public bool IsRunning { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<bool>? RunningChanged;

    public async Task StartAsync()
    {
        IHost local;
        lock (_gate)
        {
            if (IsRunning) return;
            ConfigureLogger();
            _host = BuildHost();
            local = _host;
        }

        Notify("Iniciando agente...");
        try
        {
            await local.StartAsync();
            IsRunning = true;
            RunningChanged?.Invoke(true);
            var settings = new SettingsService().Load();
            var port = settings?.Port ?? 9111;
            var host = string.IsNullOrWhiteSpace(settings?.Host) ? "127.0.0.1" : settings.Host;
            Notify($"Agente escuchando en http://{host}:{port}/");
        }
        catch (Exception ex)
        {
            Notify("Fallo al iniciar: " + ex.Message);
            try { await local.StopAsync(); } catch { /* ignore */ }
            local.Dispose();
            lock (_gate) _host = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        IHost? local;
        lock (_gate)
        {
            if (!IsRunning || _host is null) return;
            local = _host;
        }

        Notify("Deteniendo agente...");
        try
        {
            await local.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Notify("Error al detener: " + ex.Message);
        }
        finally
        {
            local.Dispose();
            lock (_gate) { _host = null; IsRunning = false; }
            RunningChanged?.Invoke(false);
            Notify("Agente detenido");
            await Log.CloseAndFlushAsync();
        }
    }

    public static IHost BuildHost()
    {
        return Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SettingsService>();
                services.AddSingleton<TicketRenderer>();
                services.AddSingleton<ShiftClosureRenderer>();
                services.AddHostedService<PrintServer>();
            })
            .Build();
    }

    public static void ConfigureLogger()
    {
        Directory.CreateDirectory(SettingsService.LogsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(SettingsService.LogsDir, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }

    private void Notify(string msg) => StatusChanged?.Invoke(msg);
}
