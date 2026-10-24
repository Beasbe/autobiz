using System.Runtime.Versioning;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using AutoParser.App.ViewModels;

namespace AutoParser.App;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged += OnVmChanged;
            // AutoParser.exe "<папка заявки>" — открыть сразу.
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && System.IO.Directory.Exists(args[1]))
                Loaded += (_, _) => vm.OpenFolder(System.IO.Path.GetFullPath(args[1]));
        }
    }

    /// <summary>После смены превью прокручиваем к подсвеченному фрагменту.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.PreviewImage) || sender is not MainViewModel vm) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var focus = vm.PreviewFocus;
            if (focus.IsEmpty || PreviewImage.ActualWidth <= 0) { PreviewScroll.ScrollToHome(); return; }
            var w = PreviewImage.ActualWidth * Zoom.Value;
            var h = PreviewImage.ActualHeight * Zoom.Value;
            PreviewScroll.ScrollToHorizontalOffset(Math.Max(0, focus.X * w - PreviewScroll.ViewportWidth / 4));
            PreviewScroll.ScrollToVerticalOffset(Math.Max(0, focus.Y * h - PreviewScroll.ViewportHeight / 3));
        });
    }
}
