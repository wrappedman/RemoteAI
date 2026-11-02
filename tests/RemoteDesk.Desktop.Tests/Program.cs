using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteDesk.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (app.MainWindow is not MainWindow window)
                {
                    if (++ticks >= 80) throw new Exception("StartupUri did not create the main window.");
                    return;
                }
                var id = (TextBox)window.FindName("IdentityBox");
                if (id.Text.Length != 64)
                {
                    if (++ticks >= 80) throw new Exception("Device identity did not initialize in the real WPF window.");
                    return;
                }
                timer.Stop();
                if (!((Button)window.FindName("ConnectButton")).IsEnabled) throw new Exception("Connect is not ready.");
                if (((CheckBox)window.FindName("ListenCheck")).IsChecked == true) throw new Exception("Listener must default off.");
                if (((TextBlock)window.FindName("StateText")).Text != "Disconnected") throw new Exception("Incorrect initial state.");
                var visual = (FrameworkElement)window.Content;
                visual.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string path = args.Length > 0 ? args[0] : "RemoteDesk-workspace.png";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using (var file = File.Create(path)) encoder.Save(file);
                Console.WriteLine("PASS: real English WPF workspace, CNG initialization, disconnected/default-off state; rendered screenshot saved.");
                window.Close(); app.Shutdown(0);
            }
            catch (Exception ex)
            {
                timer.Stop(); Console.Error.WriteLine(ex); app.MainWindow?.Close(); app.Shutdown(1);
            }
        };
        timer.Start();
        return app.Run();
    }
}
