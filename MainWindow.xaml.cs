using System.Diagnostics;
using System.Windows;

namespace Envoy;

public partial class MainWindow : Window
{
    private readonly WebServer _server = new();

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
            OpenChat();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Unable to start Envoy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_server.Address);
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

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        await _server.DisposeAsync();
    }
}
