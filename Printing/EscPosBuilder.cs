using System.Text;

namespace VentoryPrint.Printing;

/// <summary>
/// Acumula bytes ESC/POS para ticketeras térmicas (Epson y compatibles).
/// </summary>
public sealed class EscPosBuilder
{
    private readonly MemoryStream _buf = new();
    private readonly Encoding _enc;

    public const byte ESC = 0x1B;
    public const byte GS  = 0x1D;
    public const byte LF  = 0x0A;

    public EscPosBuilder(Encoding encoding)
    {
        _enc = encoding;
    }

    public EscPosBuilder Init()
    {
        Write(ESC, (byte)'@');
        return this;
    }

    public EscPosBuilder JustifyLeft()   { Write(ESC, (byte)'a', 0); return this; }
    public EscPosBuilder JustifyCenter() { Write(ESC, (byte)'a', 1); return this; }
    public EscPosBuilder JustifyRight()  { Write(ESC, (byte)'a', 2); return this; }

    public EscPosBuilder TextSize(int wMul, int hMul)
    {
        var w = (byte)Math.Clamp(wMul - 1, 0, 7);
        var h = (byte)Math.Clamp(hMul - 1, 0, 7);
        var n = (byte)((w << 4) | h);
        Write(GS, (byte)'!', n);
        return this;
    }

    public EscPosBuilder Bold(bool on)
    {
        Write(ESC, (byte)'E', (byte)(on ? 1 : 0));
        return this;
    }

    public EscPosBuilder Text(string s)
    {
        var bytes = _enc.GetBytes(s);
        _buf.Write(bytes, 0, bytes.Length);
        return this;
    }

    public EscPosBuilder Line(string s) => Text(s + "\n");

    public EscPosBuilder Feed(int n = 1)
    {
        for (var i = 0; i < n; i++) _buf.WriteByte(LF);
        return this;
    }

    public EscPosBuilder FullCut()
    {
        Write(ESC, (byte)'d', 5);
        Write(GS, (byte)'V', 0);
        return this;
    }

    /// <summary>Pulso al pin 2: abre la gaveta de dinero conectada a la ticketera.</summary>
    public EscPosBuilder Pulse()
    {
        Write(ESC, (byte)'p', 0, 0x32, 0x96);
        return this;
    }

    /// <summary>
    /// Imprime un código QR nativo (GS ( k, modelo 2). Soportado por la gran
    /// mayoría de térmicas Epson-compatibles. Si la impresora no lo soporta,
    /// simplemente ignora los comandos.
    /// </summary>
    public EscPosBuilder QrCode(string data, int moduleSize = 6)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        if (bytes.Length == 0 || bytes.Length > 7000) return this;

        // Seleccionar modelo 2
        Write(GS, (byte)'(', (byte)'k', 4, 0, 49, 65, 50, 0);
        // Tamaño de módulo (1-16)
        Write(GS, (byte)'(', (byte)'k', 3, 0, 49, 67, (byte)Math.Clamp(moduleSize, 1, 16));
        // Corrección de errores nivel L
        Write(GS, (byte)'(', (byte)'k', 3, 0, 49, 69, 48);
        // Almacenar datos
        var len = bytes.Length + 3;
        Write(GS, (byte)'(', (byte)'k', (byte)(len & 0xFF), (byte)((len >> 8) & 0xFF), 49, 80, 48);
        _buf.Write(bytes, 0, bytes.Length);
        // Imprimir
        Write(GS, (byte)'(', (byte)'k', 3, 0, 49, 81, 48);
        return this;
    }

    /// <summary>
    /// Imprime una imagen rasterizada con GS v 0 (modo bit-image raster).
    /// `packed` son <paramref name="height"/> filas de <paramref name="widthBytes"/> bytes;
    /// 1 bit por punto, MSB primero, bit=1 = punto negro. Soportado por la gran mayoría
    /// de térmicas Epson-compatibles; si no lo soporta, ignora los comandos.
    /// </summary>
    public EscPosBuilder RasterImage(byte[] packed, int widthBytes, int height)
    {
        if (packed.Length == 0 || widthBytes <= 0 || height <= 0) return this;

        // GS v 0 m xL xH yL yH  (m=0: normal). xL/xH = bytes por fila; yL/yH = filas.
        Write(GS, (byte)'v', (byte)'0', 0,
            (byte)(widthBytes & 0xFF), (byte)((widthBytes >> 8) & 0xFF),
            (byte)(height & 0xFF), (byte)((height >> 8) & 0xFF));
        _buf.Write(packed, 0, packed.Length);
        return this;
    }

    public byte[] ToArray() => _buf.ToArray();

    private void Write(params byte[] bytes) => _buf.Write(bytes, 0, bytes.Length);
}
