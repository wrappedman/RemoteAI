using System.IO;
using System.Net;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesk.Agent;
using RemoteDesk.Capture;
using RemoteDesk.Core;
using RemoteDesk.Desktop;
using RemoteDesk.Discovery;
using RemoteDesk.Protocol;
using RemoteDesk.Security;
using RemoteDesk.Windows;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int ticks = 0; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += async (_, _) =>
        {
            try
            {
                if (app.MainWindow is not MainWindow window || ((TextBox)window.FindName("IdentityBox")).Text.Length != 15 || !((CheckBox)window.FindName("ListenCheck")).IsEnabled)
                { if (++ticks >= 80) throw new Exception("Workspace did not initialize."); return; }
                timer.Stop();
                string path = args.Length > 0 ? args[0] : "RemoteDesk-workspace.png";
                Screenshot(window, path);
                var number = (TextBox)window.FindName("IdentityBox");
                if (number.GetRectFromCharacterIndex(number.Text.Length - 1, true).Right > number.ActualWidth - number.Padding.Right + 1)
                    throw new Exception("Numeric ID is clipped in the compact sidebar.");
                using (var capture = new DesktopCapture())
                {
                    var watch = Stopwatch.StartNew(); long total = 0;
                    for (int i = 0; i < 8; i++) total += capture.Capture(640, 40).Length;
                    string report = $"Windows runner GDI native-downscale/JPEG benchmark: {watch.Elapsed.TotalMilliseconds / 8:F1} ms per frame, {total / 8:N0} encoded bytes (640px profile).\nNot a physical LAN latency or GPU certification. Actual receive FPS is displayed per session.\n";
                    await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Performance.txt"), report);
                    Console.WriteLine(report);
                }
                string verifier = PasswordVerifier.Create("Test-only-unique-password-123!");
                if (!PasswordVerifier.Verify(verifier, "Test-only-unique-password-123!") || PasswordVerifier.Verify(verifier, "wrong-password-123!")) throw new Exception("DPAPI/PBKDF2 verifier failed.");
                using (var input = new InputController()) input.Post(new(InputKind.ReleaseAll)); // No synthetic keys or physical blocking in CI.
                await TestTabsAsync(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "RemoteDesk-viewer.png"));
                Console.WriteLine("PASS: multiple full-size session tabs, numeric discovery, real desktop capture/binary transfer/background decoding, frame refresh, independent disconnect, fullscreen, settings, DPAPI password verifier and input worker cleanup.");
                window.Closed += (_, _) => app.Shutdown(0); window.Close();
            }
            catch (Exception ex)
            {
                timer.Stop(); Console.Error.WriteLine(ex);
                if (app.MainWindow is { } window) { window.Closed += (_, _) => app.Shutdown(1); window.Close(); } else app.Shutdown(1);
            }
        };
        timer.Start(); return app.Run();
    }
    private static LanAgent CreateHost(DeviceIdentity identity) => new(identity)
    {
        Authorize = (request, _) => Task.FromResult<SessionGrant?>(new(request.ViewScreen, false, false, false)),
        Established = session =>
        {
            var runtime = new HostSessionRuntime(session, new AppSettings { DefaultWidth = 960 }, _ => Task.FromResult(false), Console.WriteLine);
            _ = DisposeRuntimeAsync(session, runtime);
            return Task.CompletedTask;
        }
    };
    private static async Task DisposeRuntimeAsync(RemoteDesk.Transport.SecureSession session, HostSessionRuntime runtime)
    { await session.Completion; runtime.Dispose(); }
    private static async Task TestTabsAsync(MainWindow window, string screenshot)
    {
        var availability = (CheckBox)window.FindName("ListenCheck"); availability.IsChecked = false;
        await Until(() => availability.IsEnabled && ((TextBlock)window.FindName("ListenerBadge")).Text == "OFFLINE");
        using var identity1 = DeviceIdentity.CreateForTesting(); using var identity2 = DeviceIdentity.CreateForTesting();
        await using var host1 = CreateHost(identity1); await using var host2 = CreateHost(identity2);
        host1.Start(0, IPAddress.Any); host2.Start(0, IPAddress.Loopback);
        await using var discovery = new LanDiscovery(identity1, host1.Port, "Host one"); discovery.Start();
        var workspace = (TabControl)window.FindName("Workspace");
        ((TextBox)window.FindName("TargetIdBox")).Text = NumericDeviceId.Format(NumericDeviceId.FromFingerprint(identity1.DeviceId));
        ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.ActiveViews.Count == 1 && Image(window.ActiveViews[0]).Source is BitmapSource);
        var first = window.ActiveViews[0];
        workspace.SelectedItem = window.FindName("ConnectionCenterTab");
        ((CheckBox)window.FindName("ManualCheck")).IsChecked = true;
        ((TextBox)window.FindName("AddressBox")).Text = "127.0.0.1";
        ((TextBox)window.FindName("PortBox")).Text = host2.Port.ToString();
        ((TextBox)window.FindName("FingerprintBox")).Text = identity2.DeviceId;
        await Until(() => ((Button)window.FindName("ConnectButton")).IsEnabled);
        ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.ActiveViews.Count == 2 && window.ActiveViews.Any(v => v != first && Image(v).Source is BitmapSource));
        var second = window.ActiveViews.Single(v => v != first);
        if (workspace.SelectedContent != second) throw new Exception("New connection did not open/select its own tab.");
        if (Image(second).ActualHeight < 200) throw new Exception("Remote image is not a full-size tab surface.");
        await Until(() =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(((TextBlock)second.FindName("ViewerStatus")).Text, @"frames (\d+)");
            return match.Success && long.Parse(match.Groups[1].Value) >= 3;
        });
        window.SetFullScreen(true); if (window.WindowStyle != WindowStyle.None) throw new Exception("Fullscreen failed.");
        window.SetFullScreen(false); Screenshot(window, screenshot);
        ((Button)second.FindName("ViewerDisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.ActiveViews.Count == 1);
        if (first.Session.Completion.IsCompleted) throw new Exception("Closing one tab disconnected the other.");
        workspace.SelectedItem = workspace.Items.OfType<TabItem>().Single(t => t.Content == first);
        await Task.Delay(200);
        ((Button)first.FindName("ViewerDisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.ActiveViews.Count == 0);
        var settings = (TabItem)window.FindName("SettingsTab"); settings.Visibility = Visibility.Visible; workspace.SelectedItem = settings;
        Screenshot(window, Path.Combine(Path.GetDirectoryName(screenshot)!, "RemoteDesk-settings.png"));
    }
    private static Image Image(SessionView view) => (Image)view.FindName("RemoteImage");
    private static async Task Until(Func<bool> condition)
    {
        long start = Environment.TickCount64;
        while (!condition()) { if (Environment.TickCount64 - start > 35000) throw new TimeoutException("Workspace end-to-end condition timed out."); await Task.Delay(100); }
    }
    private static void Screenshot(Window window, string path)
    {
        var visual = (FrameworkElement)window.Content; visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); using var file = File.Create(path); encoder.Save(file);
    }
}
