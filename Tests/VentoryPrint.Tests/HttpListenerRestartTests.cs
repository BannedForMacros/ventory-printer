using System.Net;
using Xunit;

namespace VentoryPrint.Tests;

/// <summary>
/// Reproduce el ciclo "Detener agente" → "Iniciar agente" dentro del mismo
/// proceso: en 1.2.2 el segundo listener quedaba registrado en HTTP.sys pero
/// toda petición recibía 503 sin llegar al proceso.
/// </summary>
public class HttpListenerRestartTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<(HttpListener listener, Task loop, CancellationTokenSource cts)> StartLikeAgent(int port, string host = "127.0.0.1")
    {
        var l = new HttpListener();
        if (host is "127.0.0.1" or "localhost")
        {
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            l.Prefixes.Add($"http://localhost:{port}/");
        }
        else
        {
            l.Prefixes.Add($"http://{host}:{port}/");
        }
        l.Start();

        var cts = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await l.GetContextAsync().WaitAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = Task.Run(async () =>
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                });
            }
            try { l.Stop(); l.Close(); } catch { }
        });

        // Igual que el POS: la primera petición debe contestar 200.
        await Task.Delay(100);
        return (l, loop, cts);
    }

    private static async Task Stop((HttpListener listener, Task loop, CancellationTokenSource cts) a)
    {
        a.cts.Cancel();
        await a.loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Detener_y_volver_a_iniciar_en_el_mismo_proceso_sigue_respondiendo()
    {
        const int port = 9197;

        var a = await StartLikeAgent(port);
        var r1 = await Http.GetAsync($"http://127.0.0.1:{port}/status");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        await Stop(a);

        var b = await StartLikeAgent(port);
        try
        {
            var r2 = await Http.GetAsync($"http://127.0.0.1:{port}/status");
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        }
        finally { await Stop(b); }
    }

    /// <summary>
    /// El caso real de la 1.2.2: arrancó con Host "+" (todas las interfaces),
    /// el usuario lo cambió a 127.0.0.1 y pulsó Detener / Iniciar.
    /// "http://+:9111/" solo se puede abrir con URL ACL (o como admin); si no
    /// hay permiso, el test no puede opinar y termina sin verificar nada.
    /// </summary>
    [Fact]
    public async Task De_todas_las_interfaces_a_localhost_en_el_mismo_proceso_sigue_respondiendo()
    {
        const int port = 9111;

        (HttpListener, Task, CancellationTokenSource) a;
        try { a = await StartLikeAgent(port, "+"); }
        catch (HttpListenerException ex) when (ex.ErrorCode is 5 or 183 or 32)
        {
            return; // sin URL ACL para "+", o el puerto está ocupado por el agente real
        }

        var r1 = await Http.GetAsync($"http://127.0.0.1:{port}/status");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        await Stop(a);

        (HttpListener, Task, CancellationTokenSource) b;
        try { b = await StartLikeAgent(port, "127.0.0.1"); }
        catch (HttpListenerException ex) when (ex.ErrorCode is 5)
        {
            return; // la URL ACL de "+" no cubre 127.0.0.1 sin admin: este test necesita consola elevada
        }
        try
        {
            var r2 = await Http.GetAsync($"http://127.0.0.1:{port}/status");
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        }
        finally { await Stop(b); }
    }
}
