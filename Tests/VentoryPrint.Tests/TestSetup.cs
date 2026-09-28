using System.Runtime.CompilerServices;
using System.Text;

namespace VentoryPrint.Tests;

internal static class TestSetup
{
    /// <summary>Igual que Program.cs: sin esto .NET no conoce la página de códigos 850 de la ticketera.</summary>
    [ModuleInitializer]
    internal static void RegistrarCodePages() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
