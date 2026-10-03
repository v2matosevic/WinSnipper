using System.IO;
using System.Windows;
using Version2.Capture;

namespace WinSnipper;

public partial class CaptureHubWindow : Window
{
    private readonly CaptureClient _client;
    private readonly string _name;
    private readonly Func<byte[]> _export;
    private bool _working;
    private CaptureSubmission? _submission;
    public CaptureHubWindow(string name, Func<byte[]> export)
    {
        _name = name; _export = export;
        _client = new CaptureClient(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSnipper", "CaptureOutbox"));
        InitializeComponent(); CaptureName.Text = name;
        DarkWindow.Attach(this, Root);
        Loaded += async (_, _) => await RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        if (_working) return; _working = true; AttachButton.IsEnabled = RetryButton.IsEnabled = false;
        try
        {
            Saved.ItemsSource = await Task.Run(_client.Pending);
            Destinations.ItemsSource = await _client.DestinationsAsync(); Destinations.SelectedIndex = 0;
            AttachButton.IsEnabled = Destinations.Items.Count > 0; RetryButton.IsEnabled = Saved.Items.Count > 0;
            Status.Text = Destinations.Items.Count == 0 ? "Open an agent tile in ADE, then refresh." : "The final screenshot will join this draft. Nothing is sent to the agent.";
        }
        catch (Exception ex) { Status.Text = ex.Message; RetryButton.IsEnabled = Saved.Items.Count > 0; }
        finally { _working = false; }
    }
    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (_working || Destinations.SelectedItem is not CaptureDestination destination) return;
        _working = true; AttachButton.IsEnabled = RetryButton.IsEnabled = false;
        try
        {
            _submission ??= CaptureClient.Screenshot(destination, _name, await Task.Run(_export));
            Destinations.IsEnabled = false;
            var result = await _client.StageAsync(_submission); Status.Text = result.Message;
            Saved.ItemsSource = await Task.Run(_client.Pending);
            if (result.Accepted) { AttachButton.Content = "Added to draft"; Destinations.IsEnabled = false; }
            else AttachButton.Content = "Retry this capture";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _working = false; AttachButton.IsEnabled = _submission is null || Saved.Items.Count > 0; RetryButton.IsEnabled = Saved.Items.Count > 0; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_working || Saved.SelectedItem is not CaptureSubmission submission) return;
        _working = true; RetryButton.IsEnabled = false;
        try { Status.Text = (await _client.RetryAsync(submission)).Message; Saved.ItemsSource = await Task.Run(_client.Pending); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _working = false; RetryButton.IsEnabled = Saved.Items.Count > 0; }
    }
}
