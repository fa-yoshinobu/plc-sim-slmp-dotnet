using Wpf.Ui.Controls;
using System.ComponentModel;
using PlcSim.Slmp.Gui.ViewModels;

namespace PlcSim.Slmp.Gui;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.Scripts.ConfirmClose())
            e.Cancel = true;
        base.OnClosing(e);
    }
}
