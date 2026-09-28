using VentoryPrint.Hosting;
using VentoryPrint.Services;
using Xunit;

namespace VentoryPrint.Tests;

public class VersionTests
{
    [Fact]
    public void La_version_sale_del_csproj_y_no_de_una_constante_olvidada()
    {
        var asm = typeof(PrintServer).Assembly.GetName().Version!;
        Assert.Equal($"{asm.Major}.{asm.Minor}.{asm.Build}", PrintServer.Version);
        Assert.NotEqual("1.2.0", PrintServer.Version); // la constante que se quedó pegada
    }

    [Fact]
    public void El_agente_no_se_ofrece_a_si_mismo_como_actualizacion()
    {
        // Si el manifiesto trae la misma versión que corre, no hay actualización.
        Assert.False(UpdateService.IsNewer(PrintServer.Version, PrintServer.Version));
        Assert.True(UpdateService.IsNewer("9.9.9", PrintServer.Version));
        Assert.False(UpdateService.IsNewer("1.2.0", PrintServer.Version));
    }
}
