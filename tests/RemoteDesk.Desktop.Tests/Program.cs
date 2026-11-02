using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesk.Agent;
using RemoteDesk.Capture;
using RemoteDesk.Desktop;
using RemoteDesk.Discovery;
using RemoteDesk.Protocol;
using RemoteDesk.Security;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += async (_, _) =>
        {
            try
            {
                if (app.MainWindow is not MainWindow window || ((TextBox)window.FindName("IdentityBox")).Text.Length != 15 ||
                    !((CheckBox)window.FindName("ListenCheck")).IsEnabled)
                {
                    if (++ticks >= 80) throw new Exception("Numeric identity / LAN availability did not initialize.");
                    return;
                }
                timer.Stop();
                if (!((Button)window.FindName("ConnectButton")).IsEnabled) throw new Exception("Connect is not ready.");
                if (((CheckBox)window.FindName("ListenCheck")).IsChecked != true) throw new Exception("Visible app should allow attended requests by default.");
                if (((TextBlock)window.FindName("StateText")).Text != "Disconnected") throw new Exception("Incorrect initial state.");
                string path = args.Length > 0 ? args[0] : "RemoteDesk-workspace.png";
                SaveScreenshot(window, path);
                await TestLiveViewingAsync(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "RemoteDesk-viewer.png"));
                Console.WriteLine("PASS: WPF numeric ID, signed discovery, TLS approval, REAL desktop capture/transport/decoding and disconnect/close.");
                window.Closed += (_, _) => app.Shutdown(0); window.Close();
            }
            catch (Exception ex)
            {
                timer.Stop(); Console.Error.WriteLine(ex);
                if (app.MainWindow is { } window) { window.Closed += (_, _) => app.Shutdown(1); window.Close(); }
                else app.Shutdown(1);
            }
        };
        timer.Start(); return app.Run();
    }
    private static async Task TestLiveViewingAsync(MainWindow window, string screenshot)
    {
        // Stop the application's incoming endpoint so the separate, auto-approved TEST fixture can
        // advertise on the normal discovery port. Production consent is never bypassed by this fixture.
        var available = (CheckBox)window.FindName("ListenCheck"); available.IsChecked = false;
        await WaitUntilAsync(() => available.IsEnabled && ((TextBlock)window.FindName("ListenerBadge")).Text == "OFFLINE");
        using var host = DeviceIdentity.CreateForTesting();
        await using var agent = new LanAgent(host)
        {
            Approve = (request, _) => Task.FromResult(request.ViewScreen),
            Established = session =>
            {
                session.StartScreenProducer(async token =>
                {
                    try
                    {
                        using var capture = new DesktopCapture();
                        while (!token.IsCancellationRequested)
                        {
                            byte[] jpeg = capture.Capture(); JpegFrame.Validate(jpeg);
                            await session.SendScreenAsync(jpeg, token);
                            await Task.Delay(250, token);
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
                });
                return Task.CompletedTask;
            }
        };
        agent.Start(0, IPAddress.Any);
        await using var discovery = new LanDiscovery(host, agent.Port, "Live capture test Host"); discovery.Start();
        ((TextBox)window.FindName("TargetIdBox")).Text = NumericDeviceId.Format(NumericDeviceId.FromFingerprint(host.DeviceId));
        ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => ((Image)window.FindName("RemoteImage")).Source is BitmapSource);
        var image = (BitmapSource)((Image)window.FindName("RemoteImage")).Source;
        if (image.PixelWidth is < 1 or > JpegFrame.MaxWidth || image.PixelHeight is < 1 or > JpegFrame.MaxHeight)
            throw new Exception("Unexpected decoded screen dimensions.");
        if (((TextBlock)window.FindName("StateText")).Text != "Viewing remote desktop") throw new Exception("Connected state is not proof of video.");
        SaveScreenshot(window, screenshot);
        ((Button)window.FindName("ViewerDisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => ((TextBlock)window.FindName("StateText")).Text == "Disconnected");
        if (((Image)window.FindName("RemoteImage")).Source is not null) throw new Exception("Stale screen retained after disconnect.");
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(25);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("WPF end-to-end condition timed out.");
            await Task.Delay(100);
        }
    }
    private static void SaveScreenshot(Window window, string path)
    {
        var visual = (FrameworkElement)window.Content; visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path); encoder.Save(file);
    }
}
