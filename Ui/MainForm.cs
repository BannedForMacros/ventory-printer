using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Windows.Forms;
using VentoryPrint.Cli;
using VentoryPrint.Hosting;
using VentoryPrint.Models;
using VentoryPrint.Printing;
using VentoryPrint.Services;

namespace VentoryPrint.Ui;

[SupportedOSPlatform("windows")]
public sealed class MainForm : Form
{
    private readonly SettingsService _settings = new();
    private readonly AgentRunner _runner = new();
    private readonly UpdateService _updates;
    private readonly bool _autostartMode;

    private ComboBox _cbPrinter = null!;
    private Button _btnRefreshPrinters = null!;
    private TextBox _txtCaja = null!;
    private TextBox _txtHost = null!;
    private TextBox _txtToken = null!;
    private NumericUpDown _numPort = null!;
    private CheckBox _chkCajonSiempre = null!;

    private RadioButton _rb80 = null!;
    private RadioButton _rb58 = null!;
    private RadioButton _rbCustom = null!;
    private NumericUpDown _numTotal = null!;
    private Label _lblBreakdown = null!;

    private Button _btnSave = null!;
    private Button _btnTestPrint = null!;
    private Button _btnOpenStatus = null!;
    private Button _btnCheckUpdate = null!;

    private Label _lblAutostart = null!;
    private Button _btnAutostartOn = null!;
    private Button _btnAutostartOff = null!;

    private Label _lblAgentStatus = null!;
    private Button _btnAgentStart = null!;
    private Button _btnAgentStop = null!;

    private StatusStrip _status = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private NotifyIcon _trayIcon = null!;

    private System.Windows.Forms.Timer _updateTimer = null!;
    private ToolStripMenuItem _menuUpdate = null!;
    private UpdateService.Manifest? _pendingUpdate;
    private string _notifiedVersion = "";

    public MainForm(bool autostartMode)
    {
        _autostartMode = autostartMode;
        _updates = new UpdateService(_settings);

        Text = "VentoryPrint - Agente de impresión";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(660, 740);
        Size = new Size(700, 780);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        if (autostartMode)
        {
            // Evita el flash inicial de la ventana antes de mandarla al tray.
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Opacity = 0;
        }

        BuildUi();
        WireEvents();
        LoadFromSettings();
        RefreshAutostartLabel();
        SetAgentRunningUi(false);

        _runner.StatusChanged += msg => SafeInvoke(() => _statusLabel.Text = msg);
        _runner.RunningChanged += running => SafeInvoke(() => SetAgentRunningUi(running));

        Load += async (_, _) =>
        {
            if (_autostartMode)
            {
                Hide();
                Opacity = 1;
                _trayIcon.Visible = true;
                if (_settings.IsConfigured())
                {
                    try { await _runner.StartAsync(); }
                    catch { /* status label ya muestra el error */ }
                }
            }

            SetupUpdateChecks();
        };
    }

    // ------------------------------------------------------------- actualización

    private void SetupUpdateChecks()
    {
        _trayIcon.BalloonTipClicked += async (_, _) => await OnUpdateClickAsync();

        // Primer chequeo poco después de arrancar; luego cada 6 horas.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 6 * 60 * 60 * 1000;
            await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    /// <summary>Consulta el servidor. Si hay versión nueva, avisa (balloon + menú/estado).</summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            if (manual) _statusLabel.Text = "Buscando actualizaciones...";
            var m = await _updates.CheckAsync(PrintServer.Version);

            if (m is null)
            {
                _pendingUpdate = null;
                _menuUpdate.Text = "Buscar actualización...";
                if (manual) _statusLabel.Text = $"Ya tienes la última versión (v{PrintServer.Version}).";
                return;
            }

            _pendingUpdate = m;
            _menuUpdate.Text = $"Actualizar a v{m.Version} (clic)";
            _statusLabel.Text = $"Actualización disponible: v{m.Version}.";

            if (manual || _notifiedVersion != m.Version)
            {
                _notifiedVersion = m.Version;
                _trayIcon.Visible = true;
                _trayIcon.ShowBalloonTip(7000, "VentoryPrint",
                    $"Hay una actualización disponible (v{m.Version}). Haz clic aquí para instalarla.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            if (manual) _statusLabel.Text = "No se pudo verificar actualizaciones: " + ex.Message;
        }
    }

    /// <summary>El "1 clic": descarga, verifica, aplica y reinicia el agente.</summary>
    private async Task OnUpdateClickAsync()
    {
        if (_pendingUpdate is null) await CheckForUpdatesAsync(manual: true);
        var m = _pendingUpdate;
        if (m is null) return; // ya está en la última versión

        var confirm = MessageBox.Show(this,
            $"Se actualizará VentoryPrint a la versión {m.Version}." +
            (string.IsNullOrWhiteSpace(m.Notas) ? "" : $"\n\nNovedades: {m.Notas}") +
            "\n\nEl agente se cerrará y volverá a abrirse solo en unos segundos. ¿Continuar?",
            "Actualizar VentoryPrint", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        try
        {
            _statusLabel.Text = "Descargando actualización...";
            var tmp = await _updates.DownloadAsync(m);

            _statusLabel.Text = "Aplicando actualización y reiniciando...";
            if (_runner.IsRunning) await _runner.StopAsync();

            var target = Environment.ProcessPath!;
            UpdateService.LaunchApplier(tmp, target);

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }
        catch (Exception ex)
        {
            Error("No se pudo actualizar: " + ex.Message);
        }
    }

    // ----------------------------------------------------------------- UI build

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildCajaGroup());
        root.Controls.Add(BuildAnchoGroup());
        root.Controls.Add(BuildAccionesGroup());
        root.Controls.Add(BuildAutostartGroup());
        root.Controls.Add(BuildAgentGroup());

        Controls.Add(root);

        _status = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel("Listo.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _status.Items.Add(_statusLabel);
        Controls.Add(_status);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "VentoryPrint",
            Visible = false,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostrar", null, (_, _) => RestoreFromTray());
        _menuUpdate = new ToolStripMenuItem("Buscar actualización...", null, async (_, _) => await OnUpdateClickAsync());
        menu.Items.Add(_menuUpdate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir",   null, async (_, _) => { await ExitFullyAsync(); });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private GroupBox BuildCajaGroup()
    {
        var gb = NewGroup("Impresora y caja");
        var t = NewTable(3);

        t.Controls.Add(new Label { Text = "Impresora:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _cbPrinter = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        t.Controls.Add(_cbPrinter, 1, 0);
        _btnRefreshPrinters = new Button { Text = "Detectar", AutoSize = true };
        t.Controls.Add(_btnRefreshPrinters, 2, 0);

        t.Controls.Add(new Label { Text = "Caja:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _txtCaja = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Ej: CAJA 1 (referencial)" };
        t.Controls.Add(_txtCaja, 1, 1);
        t.SetColumnSpan(_txtCaja, 2);

        t.Controls.Add(new Label { Text = "Host:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _txtHost = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "127.0.0.1 (local) o + (todas las redes)",
        };
        t.Controls.Add(_txtHost, 1, 2);
        t.SetColumnSpan(_txtHost, 2);

        t.Controls.Add(new Label { Text = "Token(s):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _txtToken = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Pega los tokens de las cajas separados por coma, espacio o salto de línea.",
        };
        t.Controls.Add(_txtToken, 1, 3);
        t.SetColumnSpan(_txtToken, 2);

        t.Controls.Add(new Label { Text = "Puerto:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        _numPort = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = 9111, Width = 90 };
        var portFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        portFlow.Controls.Add(_numPort);
        portFlow.Controls.Add(new Label
        {
            Text = "(9111 = el que espera ventoryPOS; no cambiar salvo conflicto)",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Padding = new Padding(6, 6, 0, 0),
        });
        t.Controls.Add(portFlow, 1, 4);
        t.SetColumnSpan(portFlow, 2);

        _chkCajonSiempre = new CheckBox
        {
            Text = "Abrir gaveta de dinero en cada impresión (aunque el POS no lo pida)",
            AutoSize = true,
        };
        t.Controls.Add(_chkCajonSiempre, 1, 5);
        t.SetColumnSpan(_chkCajonSiempre, 2);

        gb.Controls.Add(t);
        return gb;
    }

    private GroupBox BuildAnchoGroup()
    {
        var gb = NewGroup("Ancho del ticket");
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
        };

        _rb80     = new RadioButton { Text = "80mm  -  48 columnas (recomendado)", AutoSize = true };
        _rb58     = new RadioButton { Text = "58mm  -  32 columnas",                AutoSize = true };
        _rbCustom = new RadioButton { Text = "Personalizado",                       AutoSize = true };

        outer.Controls.Add(_rb80);
        outer.Controls.Add(_rb58);
        outer.Controls.Add(_rbCustom);

        var custom = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 4, 0, 0),
        };
        _numTotal = new NumericUpDown { Minimum = 28, Maximum = 80, Value = 48, Width = 70 };
        custom.Controls.Add(new Label { Text = "Columnas totales:", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
        custom.Controls.Add(_numTotal);
        outer.Controls.Add(custom);

        _lblBreakdown = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        outer.Controls.Add(_lblBreakdown);

        gb.Controls.Add(outer);
        return gb;
    }

    /// <summary>
    /// Reparte un ancho total en (Producto, Cantidad, P.U., Importe) usando la
    /// proporción del preset 80mm. Garantiza mínimos legibles en anchos chicos.
    /// </summary>
    private static (int name, int qty, int pu, int sub) ComputeColumns(int total)
    {
        var qty = Math.Max(4, (int)Math.Round(total * 5.0 / 48.0));
        var pu  = Math.Max(6, (int)Math.Round(total * 8.0 / 48.0));
        var sub = Math.Max(8, (int)Math.Round(total * 9.0 / 48.0));
        var name = total - qty - pu - sub;
        if (name < 10) name = 10;
        return (name, qty, pu, sub);
    }

    private GroupBox BuildAccionesGroup()
    {
        var gb = NewGroup("Acciones");
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
        };
        _btnSave       = new Button { Text = "Guardar configuración", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        _btnTestPrint  = new Button { Text = "Imprimir prueba",       AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        _btnOpenStatus = new Button { Text = "Ver estado (navegador)", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        _btnCheckUpdate = new Button { Text = "Buscar actualización", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        flow.Controls.AddRange(new Control[] { _btnSave, _btnTestPrint, _btnOpenStatus, _btnCheckUpdate });
        gb.Controls.Add(flow);
        return gb;
    }

    private GroupBox BuildAutostartGroup()
    {
        var gb = NewGroup("Inicio automático con Windows");
        var t = NewTable(2);

        _lblAutostart = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        t.Controls.Add(_lblAutostart, 0, 0);
        t.SetColumnSpan(_lblAutostart, 2);

        var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _btnAutostartOn  = new Button { Text = "Activar autostart",    AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        _btnAutostartOff = new Button { Text = "Desactivar autostart", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        flow.Controls.Add(_btnAutostartOn);
        flow.Controls.Add(_btnAutostartOff);
        t.Controls.Add(flow, 0, 1);
        t.SetColumnSpan(flow, 2);

        gb.Controls.Add(t);
        return gb;
    }

    private GroupBox BuildAgentGroup()
    {
        var gb = NewGroup("Agente");
        var t = NewTable(2);

        _lblAgentStatus = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        t.Controls.Add(_lblAgentStatus, 0, 0);
        t.SetColumnSpan(_lblAgentStatus, 2);

        var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _btnAgentStart = new Button { Text = "Iniciar agente", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        _btnAgentStop  = new Button { Text = "Detener agente", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        flow.Controls.Add(_btnAgentStart);
        flow.Controls.Add(_btnAgentStop);
        t.Controls.Add(flow, 0, 1);
        t.SetColumnSpan(flow, 2);

        gb.Controls.Add(t);
        return gb;
    }

    private static GroupBox NewGroup(string title) => new()
    {
        Text = title,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        Padding = new Padding(8),
        Margin = new Padding(0, 0, 0, 8),
    };

    private static TableLayoutPanel NewTable(int cols)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = cols,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 1; i < cols - 1; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        if (cols > 1) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    // ------------------------------------------------------------------- events

    private void WireEvents()
    {
        _btnRefreshPrinters.Click += (_, _) => LoadPrinters();

        _rb80.CheckedChanged     += (_, _) => { if (_rb80.Checked)     ApplyTotal(48); };
        _rb58.CheckedChanged     += (_, _) => { if (_rb58.Checked)     ApplyTotal(32); };
        _rbCustom.CheckedChanged += (_, _) => { ToggleTotal(_rbCustom.Checked); UpdateBreakdownLabel(); };

        _numTotal.ValueChanged += (_, _) => UpdateBreakdownLabel();

        _btnSave.Click        += (_, _) => OnSave();
        _btnTestPrint.Click   += (_, _) => OnTestPrint();
        _btnOpenStatus.Click  += (_, _) => OnOpenStatus();
        _btnCheckUpdate.Click += async (_, _) => await OnUpdateClickAsync();

        _btnAutostartOn.Click  += (_, _) => OnAutostartOn();
        _btnAutostartOff.Click += (_, _) => { AutostartCli.Uninstall(); RefreshAutostartLabel(); _statusLabel.Text = "Autostart desactivado."; };

        _btnAgentStart.Click += async (_, _) => { try { await _runner.StartAsync(); }
            catch (Exception ex) { Error("Fallo al iniciar agente: " + ex.Message); } };
        _btnAgentStop.Click  += async (_, _) => await _runner.StopAsync();

        FormClosing += async (_, e) =>
        {
            if (_runner.IsRunning && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            if (_runner.IsRunning) await _runner.StopAsync();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) MinimizeToTray();
        };
    }

    // ------------------------------------------------------------------ lógica

    private void LoadFromSettings()
    {
        LoadPrinters();
        var s = _settings.Load();
        if (s is null)
        {
            _cbPrinter.Text = "";
            _numPort.Value = 9111;
            ApplyTotal(48);
            _rb80.Checked = true;
            return;
        }

        _cbPrinter.Text = s.PrinterName;
        _txtCaja.Text = s.CajaNombre;
        _txtHost.Text = s.Host;
        _numPort.Value = Math.Clamp(s.Port, 1024, 65535);
        _chkCajonSiempre.Checked = s.AbrirCajonSiempre;
        _txtToken.Text = string.Join(", ", _settings.GetTokensPlain(s));

        var total = Math.Clamp(s.TotalCols, 28, 80);
        _numTotal.Value = total;

        if (total == 48)      _rb80.Checked = true;
        else if (total == 32) _rb58.Checked = true;
        else                  _rbCustom.Checked = true;

        ToggleTotal(_rbCustom.Checked);
        UpdateBreakdownLabel();
    }

    private void LoadPrinters()
    {
        var current = _cbPrinter.Text;
        _cbPrinter.Items.Clear();
        try
        {
            foreach (var p in PrinterSettings.InstalledPrinters)
                _cbPrinter.Items.Add(p!);
        }
        catch { /* ignore — el usuario podrá escribirla a mano */ }

        if (!string.IsNullOrEmpty(current)) _cbPrinter.Text = current;
        else if (_cbPrinter.Items.Count > 0) _cbPrinter.SelectedIndex = 0;
    }

    private void ApplyTotal(int total)
    {
        _numTotal.Value = Math.Clamp(total, (int)_numTotal.Minimum, (int)_numTotal.Maximum);
        ToggleTotal(_rbCustom.Checked);
        UpdateBreakdownLabel();
    }

    private void ToggleTotal(bool enabled) => _numTotal.Enabled = enabled;

    private void UpdateBreakdownLabel()
    {
        var total = (int)_numTotal.Value;
        var (name, qty, pu, sub) = ComputeColumns(total);
        _lblBreakdown.Text =
            $"Total: {total} columnas   -   Producto {name} | Cantidad {qty} | P.U. {pu} | Importe {sub}";
    }

    private void OnSave()
    {
        var printer = _cbPrinter.Text.Trim();
        if (string.IsNullOrEmpty(printer))
        {
            Error("Selecciona la impresora (ticketera) de esta caja.");
            return;
        }

        var host = _txtHost.Text.Trim();
        if (string.IsNullOrEmpty(host)) host = "127.0.0.1";

        var tokenLines = _txtToken.Text
            .Split(new[] { ',', ';', '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        if (tokenLines.Count == 0 &&
            MessageBox.Show(this,
                "No pegaste ningún token. Sin tokens el agente aceptará tickets de CUALQUIER caja.\n\n¿Guardar de todas formas?",
                "VentoryPrint", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
        {
            return;
        }

        try
        {
            var (name, qty, pu, sub) = ComputeColumns((int)_numTotal.Value);
            var s = _settings.Load() ?? new AgentSettings();
            s.PrinterName = printer;
            s.CajaNombre = _txtCaja.Text.Trim();
            s.Host = host;
            s.Port = (int)_numPort.Value;
            s.AbrirCajonSiempre = _chkCajonSiempre.Checked;
            s.ColName = name; s.ColQty = qty; s.ColPu = pu; s.ColSub = sub;

            // Reemplazamos la lista de tokens con las líneas actuales.
            s.TokensProtectedBase64.Clear();
            foreach (var t in tokenLines)
                _settings.SetToken(s, t);

            _settings.Save(s);

            _statusLabel.Text = _runner.IsRunning
                ? "Configuración guardada. Reinicia el agente para aplicar el puerto."
                : "Configuración guardada.";
        }
        catch (Exception ex)
        {
            Error("Error al guardar: " + ex.Message);
        }
    }

    private void OnTestPrint()
    {
        var printer = _cbPrinter.Text.Trim();
        if (string.IsNullOrEmpty(printer))
        {
            Error("Selecciona una impresora primero.");
            return;
        }

        try
        {
            // Usa la config guardada para el layout; si aún no guardó, defaults.
            var renderer = new TicketRenderer(_settings);
            var bytes = renderer.Render(TestPrintCli.SamplePayload());
            RawPrinterHelper.SendBytesToPrinter(printer, bytes, "Ventory Test (UI)");
            _statusLabel.Text = "Ticket de prueba enviado a " + printer;
        }
        catch (Exception ex)
        {
            Error("Error al imprimir: " + ex.Message);
        }
    }

    private void OnOpenStatus()
    {
        var port = (int)_numPort.Value;
        try
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error("No se pudo abrir el navegador: " + ex.Message);
        }
    }

    private void RefreshAutostartLabel()
    {
        var on = AutostartCli.IsInstalled();
        _lblAutostart.Text = on ? "Estado: ACTIVO" : "Estado: INACTIVO";
        _lblAutostart.ForeColor = on ? Color.SeaGreen : Color.DimGray;
        _btnAutostartOn.Enabled  = !on;
        _btnAutostartOff.Enabled = on;
    }

    private void OnAutostartOn()
    {
        var settings = _settings.Load();
        if (!string.IsNullOrWhiteSpace(settings?.Host) && settings.Host is not "127.0.0.1" and not "localhost")
        {
            var acl = AutostartCli.EnsureUrlAcl(settings);
            if (!acl.Ok)
            {
                var msg = $"El host está configurado como '{settings.Host}', lo que requiere un permiso de red (URL ACL) para que el agente escuche al arrancar." +
                          $"\n\n{acl.Message}\n\n" +
                          $"Para solucionarlo, ejecuta UNA vez como administrador:\n" +
                          $"netsh http add urlacl url={acl.Url} user=\"{acl.User}\"\n\n" +
                          "¿Instalar el autostart de todas formas?";
                if (MessageBox.Show(this, msg, "Permiso de red necesario",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                {
                    _statusLabel.Text = "Autostart no instalado: falta permiso de red.";
                    return;
                }
            }
        }

        AutostartCli.Install();
        RefreshAutostartLabel();
        _statusLabel.Text = "Autostart activado.";
    }

    private void SetAgentRunningUi(bool running)
    {
        _lblAgentStatus.Text = running ? "Estado: EN EJECUCIÓN" : "Estado: DETENIDO";
        _lblAgentStatus.ForeColor = running ? Color.SeaGreen : Color.DimGray;
        _btnAgentStart.Enabled = !running;
        _btnAgentStop.Enabled  = running;

        _cbPrinter.Enabled = !running;
        _btnRefreshPrinters.Enabled = !running;
        _txtCaja.Enabled = !running;
        _txtToken.Enabled = !running;
        _numPort.Enabled = !running;
        _btnSave.Enabled = !running;
    }

    private void Error(string msg)
    {
        _statusLabel.Text = msg;
        MessageBox.Show(this, msg, "VentoryPrint",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void MinimizeToTray()
    {
        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;
        Hide();
        _trayIcon.Visible = true;
        _trayIcon.ShowBalloonTip(2500, "VentoryPrint", "El agente sigue corriendo en segundo plano.", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        Opacity = 1;
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        _trayIcon.Visible = false;
    }

    private async Task ExitFullyAsync()
    {
        if (_runner.IsRunning) await _runner.StopAsync();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
