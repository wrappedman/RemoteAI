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
using RemoteDesk.Transport;
using RemoteDesk.Files;
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
                TestResponsiveLayout(window);
                double originalWidth = window.Width; window.Width = 1400; await Task.Delay(150); window.UpdateLayout();
                Screenshot(window, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "RemoteDesk-workspace-wide.png"));
                window.Width = originalWidth; await Task.Delay(100);
                if (window.Icon is null) throw new Exception("Application window icon missing.");
                var launch = ElevationLauncher.CreateStartInfo(@"C:\RemoteDesk\RemoteDesk.exe", @"C:\RemoteDesk");
                if (!launch.UseShellExecute || launch.Verb != "runas") throw new Exception("Admin restart does not invoke normal UAC.");
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
                await TestConsentAndFilesAsync(window, Path.GetDirectoryName(Path.GetFullPath(path))!);
                await TestPasswordSetupAsync(window, Path.GetDirectoryName(Path.GetFullPath(path))!);
                await TestAdminContractAsync();
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
    private static void TestResponsiveLayout(MainWindow window)
    {
        double width = window.Width;
        foreach (double size in new[] { 1100d, 1400d, 1680d })
        {
            window.Width = size; window.UpdateLayout();
            var content = (FrameworkElement)window.FindName("CenterContent");
            var cards = (FrameworkElement)window.FindName("ConnectionCards");
            if (cards.ActualWidth < content.ActualWidth - 2 || cards.ActualWidth < 760)
                throw new Exception("Connection center keeps an unused fixed-width column.");
        }
        window.Width = width; window.UpdateLayout();
    }
    private static async Task SavePolicyAsync(MainWindow window, AppSettings policy)
    {
        var page = (SettingsView)window.FindName("SettingsPage"); page.Load(policy);
        ((Button)page.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => ((Button)page.FindName("SaveSettingsButton")).IsEnabled && ((TextBlock)page.FindName("SettingsStatus")).Text.StartsWith("Saved."));
    }
    private static async Task TestConsentAndFilesAsync(MainWindow window, string screenshots)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "RemoteDesk-workspace-" + Guid.NewGuid().ToString("N"));
        string hostRoot = Path.Combine(temporary, "host"), clientRoot = Path.Combine(temporary, "controller");
        Directory.CreateDirectory(hostRoot); Directory.CreateDirectory(clientRoot);
        await File.WriteAllTextAsync(Path.Combine(hostRoot, "from-host.txt"), "Host selected folder");
        await File.WriteAllTextAsync(Path.Combine(clientRoot, "from-controller.txt"), "Controller selected folder");
        using var identity = DeviceIdentity.CreateForTesting();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await SavePolicyAsync(window, new AppSettings { AllowEffects = false });
            string pin = ((TextBox)window.FindName("FullFingerprintBox")).Text;
            var connecting = SecureSession.ConnectAsync("127.0.0.1", 45831, pin, "Consent regression", identity, _ => { }, deadline.Token,
                requested: new(true, true, true, true));
            await Until(() => ((FrameworkElement)window.FindName("ApprovalPanel")).Visibility == Visibility.Visible);
            if (((CheckBox)window.FindName("GrantEffectsCheck")).IsEnabled || !((TextBlock)window.FindName("ApprovalPolicyHint")).Text.Contains("Animations"))
                throw new Exception("Disabled animation grant has no policy explanation.");
            if (!((CheckBox)window.FindName("GrantFilesCheck")).IsEnabled) throw new Exception("File exchange cannot be granted before choosing a folder.");
            // Saving same-port policies while approving MUST NOT tear down the pending request.
            await SavePolicyAsync(window, new AppSettings { SharedFolder = hostRoot, AllowEffects = true });
            if (((FrameworkElement)window.FindName("ApprovalPanel")).Visibility != Visibility.Visible || connecting.IsCompleted)
                throw new Exception("Policy save cancelled the pending approval.");
            foreach (string name in new[] { "GrantControlCheck", "GrantFilesCheck", "GrantEffectsCheck" })
            {
                var box = (CheckBox)window.FindName(name); if (!box.IsEnabled) throw new Exception("Grant remained disabled after policy save: " + name);
                box.IsChecked = true; if (box.IsChecked != true) throw new Exception("Grant checkbox did not toggle: " + name);
            }
            ((TabControl)window.FindName("Workspace")).SelectedItem = window.FindName("ConnectionCenterTab");
            Screenshot(window, Path.Combine(screenshots, "RemoteDesk-approval.png"));
            ((Button)window.FindName("ApproveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await using var client = await connecting;
            using var clientRuntime = new HostSessionRuntime(client, new AppSettings(), _ => Task.FromResult(false), _ => { });
            client.Start();
            await Until(() => window.ActiveViews.Any(v => v.Session.IsHost));
            var hostView = window.ActiveViews.Single(v => v.Session.IsHost);
            if (!client.Permissions.FileAccess || !client.Permissions.DesktopEffects) throw new Exception("Approval lost selected capabilities.");
            // A Controller's configured folder is NOT silently exposed to the Host.
            try { await hostView.Session.RequestAsync(RemoteCommand.Create(Operation.ShareInfo), deadline.Token); throw new Exception("Unselected Controller folder exposed."); }
            catch (IOException ex) when (ex.Message.Contains("No folder")) { }
            await using var clientView = new SessionView(client, 20, 960)
            { ShareLocalFolder = folder => { clientRuntime.ShareFolder(folder); return Task.CompletedTask; }, StopLocalSharing = clientRuntime.StopSharing };
            await clientView.ShareFolderAsync(clientRoot);
            if (!((Button)hostView.FindName("FilesButton")).IsEnabled) throw new Exception("Incoming Host tab cannot browse an explicitly shared Controller folder.");
            ((Button)hostView.FindName("FilesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => ((ListBox)hostView.FindName("FileList")).Items.OfType<FileEntry>().Any(e => e.Name == "from-controller.txt"));
            if (!((TextBlock)hostView.FindName("RemoteFolderInfo")).Text.Contains(clientRoot)) throw new Exception("Remote folder identity not shown.");
            var own = await client.RequestAsync(RemoteCommand.Create(Operation.ListFiles, ""), deadline.Token);
            if (own.Entries?.Any(e => e.Name == "from-host.txt") != true) throw new Exception("Configured Host folder is not served by production runtime.");
            string source = Path.Combine(temporary, "upload.bin"); await File.WriteAllBytesAsync(source, Enumerable.Range(0, 90000).Select(i => (byte)i).ToArray());
            await clientView.UploadFileAsync(source);
            await hostView.UploadFileAsync(source);
            if (!File.Exists(Path.Combine(hostRoot, "upload.bin")) || !File.Exists(Path.Combine(clientRoot, "upload.bin"))) throw new Exception("Two-way upload did not reach both selected roots.");
            string downloaded = Path.Combine(temporary, "download.bin");
            await hostView.DownloadFileAsync("upload.bin", downloaded);
            if (!((TextBlock)hostView.FindName("TransferText")).Text.Contains("Transfer complete; SHA-256 verified")) throw new Exception("Transfer completion was erased by folder refresh.");
            Screenshot(window, Path.Combine(screenshots, "RemoteDesk-files.png"));
            byte[] expectedBytes = await File.ReadAllBytesAsync(source), downloadedBytes = await File.ReadAllBytesAsync(downloaded);
            if (!expectedBytes.SequenceEqual(downloadedBytes)) throw new Exception("Reverse download corrupted content.");
            // Host must never use symmetric file RPC to request input/admin/effects on Controller.
            try { await hostView.Session.RequestAsync(RemoteCommand.Create(Operation.RequestAdmin), deadline.Token); throw new Exception("Reverse admin command allowed."); }
            catch (UnauthorizedAccessException) { }
            ((CheckBox)clientView.FindName("AnimationsCheck")).IsChecked = true;
            await Until(() => ((TextBlock)clientView.FindName("ActionStatus")).Text.Contains("Animations disabled"));
            if (((CheckBox)clientView.FindName("AnimationsCheck")).IsChecked != true) throw new Exception("Animation checkbox failed to keep acknowledged state.");
            ((CheckBox)clientView.FindName("AnimationsCheck")).IsChecked = false;
            await Until(() => ((TextBlock)clientView.FindName("ActionStatus")).Text.Contains("preferences restored"));
            await SavePolicyAsync(window, new AppSettings { SharedFolder = hostRoot, AllowEffects = false });
            if (client.Completion.IsCompleted) throw new Exception("Same-port settings disconnected an authorized session.");
            ((CheckBox)clientView.FindName("AnimationsCheck")).IsChecked = true;
            await Until(() => ((TextBlock)clientView.FindName("ActionStatus")).Text.Contains("Animations are not permitted"));
            if (((CheckBox)clientView.FindName("AnimationsCheck")).IsChecked == true) throw new Exception("Refused animation update was not rolled back.");
            ((Button)clientView.FindName("AdminButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => ((TextBlock)clientView.FindName("ActionStatus")).Text.Contains("enable admin restart"));
            await Task.Delay(1300); // Media/status refresh must not erase operation errors.
            if (!((TextBlock)clientView.FindName("ActionStatus")).Text.Contains("enable admin restart")) throw new Exception("Admin denial hidden by frame status.");
            ((Button)clientView.FindName("StopSharingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try { await hostView.Session.RequestAsync(RemoteCommand.Create(Operation.ListFiles, ""), deadline.Token); throw new Exception("Stopped share remained accessible."); }
            catch (IOException) { }
            await client.DisconnectAsync(); await Until(() => window.ActiveViews.Count == 0);
            await SavePolicyAsync(window, new AppSettings());
            Console.WriteLine("PASS: real local approval, policy explanations/hot-save, selected checkbox grants, production two-way folder listing/upload/download, explicit Controller share/revoke, animation acknowledgement/rollback, persistent admin errors, reverse-sensitive-command denial and UAC launch contract.");
        }
        finally { Directory.Delete(temporary, true); }
    }
    private static async Task TestPasswordSetupAsync(MainWindow window, string screenshots)
    {
        const string password = "Test-only-Auto-Password-7491!";
        var page = (SettingsView)window.FindName("SettingsPage");
        using var controllerIdentity = DeviceIdentity.CreateForTesting();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        await SavePolicyAsync(window, new AppSettings());
        // A valid draft must survive an unrelated validation failure. Nothing was saved yet.
        page.Load(new AppSettings());
        ((TextBox)page.FindName("FolderSetting")).Text = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing");
        ((PasswordBox)page.FindName("NewPassword")).Password = password;
        ((CheckBox)page.FindName("PasswordSetting")).IsChecked = true;
        ((CheckBox)page.FindName("PasswordControlSetting")).IsChecked = true;
        ((CheckBox)page.FindName("PasswordFilesSetting")).IsChecked = true;
        ((Button)page.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => page.IsEnabled && ((TextBlock)page.FindName("SettingsStatus")).Text.StartsWith("Not saved:"));
        if (((PasswordBox)page.FindName("NewPassword")).Password != password || AppSettings.Load().ProtectedPasswordVerifier is not null)
            throw new Exception("Failed settings save erased the password draft or persisted invalid settings.");
        ((TextBox)page.FindName("FolderSetting")).Text = "";
        ((Button)page.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => page.IsEnabled && ((TextBlock)page.FindName("SettingsStatus")).Text.StartsWith("Saved."));
        var saved = AppSettings.Load();
        if (!saved.PasswordAccess || saved.TrustedControllers.Count != 0 || !PasswordVerifier.Verify(saved.ProtectedPasswordVerifier!, password) ||
            ((PasswordBox)page.FindName("NewPassword")).Password.Length != 0 || !((TextBlock)page.FindName("PasswordState")).Text.Contains("waiting for a trusted"))
            throw new Exception("Saving a password before pairing did not persist a usable verifier / setup state.");
        string json = await File.ReadAllTextAsync(Path.Combine(AppSettings.DataDirectory, "settings-v3.json"));
        if (json.Contains(password)) throw new Exception("Password was stored as plaintext.");
        string pin = ((TextBox)window.FindName("FullFingerprintBox")).Text;
        async Task<SecureSession> Connect(string? secret) => await SecureSession.ConnectAsync("127.0.0.1", 45831, pin, "Password regression", controllerIdentity,
            _ => { }, deadline.Token, requested: new(true, true, true, true), password: secret);
        async Task Rejected(string secret)
        {
            try { await using var unexpected = await Connect(secret); throw new Exception("Invalid automatic authorization was accepted."); }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(100);
            if (((FrameworkElement)window.FindName("ApprovalPanel")).Visibility == Visibility.Visible)
                throw new Exception("A submitted password silently fell back to manual consent.");
        }
        await Rejected(password); // Correct password, unknown Controller: still rejected.
        var pairing = Connect(null);
        await Until(() => ((FrameworkElement)window.FindName("ApprovalPanel")).Visibility == Visibility.Visible);
        // Simulate a draft already being edited when a manual pairing is approved.
        ((PasswordBox)page.FindName("NewPassword")).Password = "Unsaved-draft-must-not-be-lost";
        ((CheckBox)window.FindName("TrustControllerCheck")).IsChecked = true;
        ((Button)window.FindName("ApproveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await using (var paired = await pairing)
        {
            paired.Start();
            await Until(() => window.ActiveViews.Count == 1);
            if (((PasswordBox)page.FindName("NewPassword")).Password != "Unsaved-draft-must-not-be-lost")
                throw new Exception("Manual trust approval erased the settings password draft.");
            await paired.DisconnectAsync(); await Until(() => window.ActiveViews.Count == 0); await Task.Delay(100);
        }
        saved = AppSettings.Load();
        if (!saved.TrustedControllers.Contains(controllerIdentity.DeviceId) || !PasswordVerifier.Verify(saved.ProtectedPasswordVerifier!, password))
            throw new Exception("Pairing did not preserve the saved password / trusted full key.");
        page.Load(saved); // Discard the deliberately unsaved test draft.
        await using (var automatic = await Connect(password))
        {
            automatic.Start(); await Until(() => window.ActiveViews.Count == 1);
            if (!automatic.Permissions.ControlInput || !automatic.Permissions.FileAccess || automatic.Permissions.DesktopEffects ||
                ((FrameworkElement)window.FindName("ApprovalPanel")).Visibility == Visibility.Visible)
                throw new Exception("Trusted password approval did not bypass the popup with the configured capability subset.");
            await automatic.DisconnectAsync(); await Until(() => window.ActiveViews.Count == 0); await Task.Delay(100);
        }
        var settingsTab = (TabItem)window.FindName("SettingsTab"); settingsTab.Visibility = Visibility.Visible;
        ((TabControl)window.FindName("Workspace")).SelectedItem = settingsTab;
        page.ShowPasswordSection(); await Task.Delay(200);
        if (((ScrollViewer)page.FindName("SettingsScroller")).VerticalOffset == 0) throw new Exception("Password setup shortcut did not reveal the password section.");
        Screenshot(window, Path.Combine(screenshots, "RemoteDesk-password.png"));
        // A missing shared folder must not reset the password when the app reloads settings.
        string removable = Path.Combine(Path.GetTempPath(), "RemoteDesk-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(removable); saved.SharedFolder = removable; await saved.SaveAsync(); Directory.Delete(removable);
        var recovered = AppSettings.Load();
        if (!recovered.PasswordAccess || recovered.TrustedControllers.Count != 1 || !PasswordVerifier.Verify(recovered.ProtectedPasswordVerifier!, password) || recovered.LoadWarning is null || recovered.SharedFolder != "")
            throw new Exception("An unavailable shared folder discarded password/trust settings.");
        await SavePolicyAsync(window, recovered);
        for (int i = 0; i < 3; i++) await Rejected("Wrong-test-password-7491!");
        await Rejected(password); // Correct password is blocked during the real Host cooldown.
        long now = 100000;
        var policy = new AutomaticAccessPolicy(() => now);
        var request = new ApprovalRequest(controllerIdentity.DeviceId, "Policy test", "127.0.0.1", Password: "Wrong-test-password-7491!");
        for (int i = 0; i < 3; i++)
            if (await policy.EvaluateAsync(recovered, request, deadline.Token) != AutomaticAccessResult.InvalidPassword) throw new Exception("Wrong password result.");
        if (await policy.EvaluateAsync(recovered, request with { Password = password }, deadline.Token) != AutomaticAccessResult.Cooldown) throw new Exception("Cooldown not enforced.");
        now += 60001;
        if (await policy.EvaluateAsync(recovered, request with { Password = password }, deadline.Token) != AutomaticAccessResult.Approved) throw new Exception("Cooldown did not expire.");
        recovered.PasswordAccess = false; await SavePolicyAsync(window, recovered);
        await Rejected(password); // Disabled auto access must not display a manual popup either.
        await SavePolicyAsync(window, new AppSettings());
        Console.WriteLine("PASS: actual Settings password save before pairing, draft retention, DPAPI reload/no plaintext, unknown-key rejection, manual full-key trust, automatic TLS login without popup, capability subset, missing-folder recovery, wrong-password/disabled fail-closed and cooldown expiry.");
    }
    private static async Task TestAdminContractAsync()
    {
        using var hostIdentity = DeviceIdentity.CreateForTesting(); using var controllerIdentity = DeviceIdentity.CreateForTesting();
        bool accepted = false, elevated = false; int confirmations = 0;
        await using var host = new LanAgent(hostIdentity)
        {
            Authorize = (_, _) => Task.FromResult<SessionGrant?>(new(false, true)),
            Established = session =>
            {
                var runtime = new HostSessionRuntime(session, new AppSettings { AllowAdminRequests = true },
                    _ => { confirmations++; return Task.FromResult(accepted); }, _ => { }, () => elevated);
                _ = DisposeRuntimeAsync(session, runtime); return Task.CompletedTask;
            }
        };
        host.Start(0, IPAddress.Loopback);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var controller = await SecureSession.ConnectAsync("127.0.0.1", host.Port, hostIdentity.DeviceId, "Admin regression", controllerIdentity,
            _ => { }, deadline.Token, requested: new(false, true));
        controller.Start();
        try { await controller.RequestAsync(RemoteCommand.Create(Operation.RequestAdmin), deadline.Token); throw new Exception("Admin rejection ignored."); }
        catch (IOException ex) when (ex.Message.Contains("declined")) { }
        if (confirmations != 1) throw new Exception("Admin request bypassed local confirmation.");
        accepted = true;
        await controller.RequestAsync(RemoteCommand.Create(Operation.RequestAdmin), deadline.Token);
        if (confirmations != 2) throw new Exception("Admin acceptance did not invoke explicit confirmation.");
        elevated = true;
        var already = await controller.RequestAsync(RemoteCommand.Create(Operation.RequestAdmin), deadline.Token);
        if (already.Path != "already-elevated" || confirmations != 2) throw new Exception("Already-elevated Host did not report its state.");
        await controller.DisconnectAsync();
        Console.WriteLine("PASS: admin policy, explicit local rejection/acceptance, already-elevated response. Native UAC approval intentionally manual.");
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
        ((PasswordBox)window.FindName("ConnectionPassword")).Password = "First-contact-must-not-send-123!";
        ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => ((Button)window.FindName("ConnectButton")).IsEnabled);
        if (!((TextBlock)window.FindName("ErrorText")).Text.Contains("previously pinned Host") || window.ActiveViews.Count != 0)
            throw new Exception("A numeric first-contact password was sent without a prior Host pin.");
        ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.ActiveViews.Count == 1 && Image(window.ActiveViews[0]).Source is BitmapSource);
        var first = window.ActiveViews[0];
        await using (var profile = new SessionView(first.Session, 30, 960))
        {
            if (((ComboBox)profile.FindName("QualityBox")).SelectedIndex != 0 || ((ComboBoxItem)((ComboBox)profile.FindName("FpsBox")).SelectedItem).Content.ToString() != "30")
                throw new Exception("Saved FPS/width defaults did not initialize the session profile.");
        }
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
