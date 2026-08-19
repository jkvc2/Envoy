using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Forms;

namespace Envoy;

public partial class MainWindow : Window
{
    private readonly WebServer _server = new();
    private NotifyIcon? _trayIcon;
    private bool _exiting;
    private bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _server.StartAsync();
            AddressText.Text = _server.Address;
            CreateTrayIcon();
            OpenChat();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Unable to start Envoy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIconFromBrand(),
            Text = $"Envoy — {_server.Address}",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Envoy", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClosing)
        {
            return;
        }

        e.Cancel = true;

        if (!_exiting)
        {
            // Minimize to tray instead of closing.
            Hide();
            return;
        }

        // Actually shutting down — clean up resources then close for real.
        await _server.DisposeAsync();
        _trayIcon?.Dispose();
        _reallyClosing = true;
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(_server.Address);
    }

    private void OpenChat_Click(object sender, RoutedEventArgs e)
    {
        OpenChat();
    }

    private void OpenChat()
    {
        Process.Start(new ProcessStartInfo(_server.Address)
        {
            UseShellExecute = true
        });
    }

    private static Icon CreateTrayIconFromBrand()
    {
        // Generate a 32×32 icon matching the Envoy brand mark ("E" on a gradient).
        var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Gradient-filled rounded shape
        using var gradient = new LinearGradientBrush(
            new Rectangle(0, 0, 32, 32),
            Color.FromArgb(67, 97, 238),
            Color.FromArgb(109, 70, 232),
            LinearGradientMode.ForwardDiagonal);

        g.FillEllipse(gradient, 2, 2, 28, 28);

        // "E" letter
        using var font = new System.Drawing.Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("E", font, textBrush, new RectangleF(0, 1, 32, 32), format);

        // Keep the bitmap alive — the Icon wraps its HICON handle.
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
}
