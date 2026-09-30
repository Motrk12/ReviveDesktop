using System.ComponentModel;
using System.IO;
using System.Windows;
using Revive.App.ViewModels;

namespace Revive.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // "Revive.exe card.img" (or dropping an image on the exe) opens that disk image.
        var vm = (MainViewModel)DataContext;
        foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
            if (File.Exists(arg))
                vm.AddImage(Path.GetFullPath(arg));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (vm.IsRecovering && MessageBox.Show("Files are still being recovered. Stop and close Revive?", "Revive",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        vm.Shutdown();
        base.OnClosing(e);
    }
}
