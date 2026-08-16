using System.Runtime.Versioning;
using VentoryPrint.Models;
using VentoryPrint.Printing;
using VentoryPrint.Services;

namespace VentoryPrint.Cli;

[SupportedOSPlatform("windows")]
public static class TestPrintCli
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
            var renderer = new TicketRenderer(settings);
            var bytes = renderer.Render(SamplePayload());
            RawPrinterHelper.SendBytesToPrinter(s.PrinterName, bytes, "Ventory Test");
            Console.WriteLine($"Ticket de prueba enviado a '{s.PrinterName}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error al imprimir: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Ticket de ejemplo con la misma forma que manda ventoryPOS.</summary>
    public static TicketPayload SamplePayload() => new()
    {
        Negocio = new NegocioInfo
        {
            Nombre = "Ferreteria de Prueba",
            Ruc = "20123456789",
            Direccion = "Av. Prueba 123 - Chiclayo",
            Telefono = "987654321",
            MostrarIgv = true,
        },
        Documento = new DocumentoInfo
        {
            Tipo = "NOTA DE VENTA",
            Numero = "V-00000001",
            Fecha = DateTime.Now.ToString("dd/MM/yyyy hh:mm tt"),
            Vendedor = "CAJERA PRUEBA",
            Caja = "CAJA 1",
        },
        Cliente = new ClienteInfo { Nombre = "Cliente Varios" },
        Items = new List<TicketItem>
        {
            new() { Cant = 2,    Desc = "Cemento Pacasmayo Tipo I",       Unidad = "BOLSA",  Precio = 28.50m, Importe = 57.00m },
            new() { Cant = 100,  Desc = "Ladrillo King Kong 18 huecos",   Unidad = "UNIDAD", Precio = 1.20m,  Importe = 120.00m },
            new() { Cant = 0.5m, Desc = "Arena gruesa",                   Unidad = "M3",     Precio = 60.00m, Importe = 30.00m },
        },
        Totales = new TotalesInfo { Subtotal = 207.00m, Descuento = 7.00m, Total = 200.00m, Moneda = "PEN" },
        Pago = new PagoInfo { Metodo = "Efectivo", Recibido = 250.00m, Vuelto = 50.00m },
        Pie = "Gracias por su preferencia",
        Copias = 1,
    };
}
