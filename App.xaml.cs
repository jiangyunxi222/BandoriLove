using System.Windows;
using System.Windows.Controls;
using System.IO;

namespace BandoriLove;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Window? loading = null;
        try
        {
            var bundle = EmbeddedBundle.Find();
            if (bundle is not null)
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 12, 0, 0) };
                var panel = new StackPanel { Margin = new Thickness(24) };
                panel.Children.Add(new TextBlock { Text = "正在准备本机语音模型和运行库…", FontSize = 16 });
                panel.Children.Add(bar);
                loading = new Window
                {
                    Title = "BandoriLove", Content = panel, Width = 460, Height = 140,
                    ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                var progress = new Progress<double>(value => bar.Value = value);
                if (!bundle.IsReady)
                {
                    loading.Closing += (_, args) => args.Cancel = bundle.IsExtracting;
                    loading.Show();
                }
                EmbeddedBundle.Root = await Task.Run(() => bundle.Extract(progress));
            }
            MainWindow = new MainWindow();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow.Show();
            loading?.Close();
        }
        catch (Exception error)
        {
            loading?.Close();
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "BandoriLove-startup.log"), error.ToString()); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show("无法启动 BandoriLove：" + error.Message, "BandoriLove", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
