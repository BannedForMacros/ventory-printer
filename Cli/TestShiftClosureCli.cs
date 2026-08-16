using System.Runtime.Versioning;
using VentoryPrint.Models;
using VentoryPrint.Printing;
using VentoryPrint.Services;

namespace VentoryPrint.Cli;

[SupportedOSPlatform("windows")]
public static class TestShiftClosureCli
{
    public static int Run()
    {
        var settings = new SettingsService();
        var s = settings.Load();
        if (s is null || string.IsNullOrWhiteSpace(s.PrinterName))
        {
            Console.WriteLine("No hay impresora configurada. Abre VentoryPrint.exe y configura primero.");
            return 1;
        }

        try
        {
            var renderer = new ShiftClosureRenderer(settings);
            var bytes = renderer.Render(SamplePayload());
            RawPrinterHelper.SendBytesToPrinter(s.PrinterName, bytes, "Ventory Cierre Test");
            Console.WriteLine($"Reporte de cierre de turno de prueba enviado a '{s.PrinterName}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error al imprimir: " + ex.Message);
            return 1;
        }
    }

    public static ShiftClosurePayload SamplePayload() => new()
    {
        Negocio = new NegocioInfo
        {
            Nombre = "Ferreteria de Prueba",
            Ruc = "20123456789",
            Direccion = "Av. Prueba 123 - Chiclayo",
            Telefono = "987654321",
            MostrarIgv = true,
        },
        Turno = new TurnoInfo
        {
            Id = "T-001",
            Nombre = "Turno Mañana",
            Cajero = "CAJERA PRUEBA",
            Caja = "CAJA 1",
            FechaApertura = DateTime.Now.AddHours(-10).ToString("dd/MM/yyyy hh:mm tt"),
            FechaCierre = DateTime.Now.ToString("dd/MM/yyyy hh:mm tt"),
        },
        Resumen = new ResumenCierre
        {
            NumeroVentas = 45,
            Subtotal = 1457.63m,
            Igv = 262.37m,
            Descuento = 50.00m,
            Total = 1670.00m,
            Moneda = "PEN",
        },
        MetodosPago = new List<MetodoPagoCierre>
        {
            new() { Nombre = "Efectivo",       Monto = 820.00m, Cantidad = 23 },
            new() { Nombre = "Visa",           Monto = 400.00m, Cantidad = 10 },
            new() { Nombre = "Mastercard",     Monto = 100.00m, Cantidad = 2 },
            new() { Nombre = "Yape",           Monto = 250.00m, Cantidad = 7 },
            new() { Nombre = "Plin",           Monto = 100.00m, Cantidad = 3 },
        },
        Caja = new CajaCierre
        {
            MontoApertura = 200.00m,
            VentasEfectivo = 820.00m,
            Entradas = 50.00m,
            Salidas = 30.00m,
            EfectivoEsperado = 1040.00m,
            EfectivoDeclarado = 1035.00m,
            Diferencia = -5.00m,
        },
        Pie = "Reporte generado por ventoryPOS",
        Copias = 1,
    };
}
