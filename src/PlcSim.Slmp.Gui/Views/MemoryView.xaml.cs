using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlcSim.Slmp.Gui.ViewModels;

namespace PlcSim.Slmp.Gui.Views;

public partial class MemoryView : UserControl
{
    public static readonly DependencyProperty AddressColumnWidthProperty = DependencyProperty.Register(
        nameof(AddressColumnWidth), typeof(GridLength), typeof(MemoryView), new PropertyMetadata(new GridLength(150)));

    private const double RowHeight = 32;
    private MemoryCell? _editing;

    public MemoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
                oldVm.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged newVm)
                newVm.PropertyChanged += OnViewModelChanged;
        };
    }

    public GridLength AddressColumnWidth
    {
        get => (GridLength)GetValue(AddressColumnWidthProperty);
        set => SetValue(AddressColumnWidthProperty, value);
    }

    private MemoryViewModel? Vm => DataContext as MemoryViewModel;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MemoryViewModel.SelectedArea) || Vm?.SelectedArea is not { } area)
            return;
        AddressColumnWidth = new GridLength(area.Kind == AreaKind.Label ? 260 : area.Prefix.Length > 4 ? 190 : 120);
    }

    private void RowsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Vm is { } vm)
            vm.VisibleRows = Math.Max(1, (int)(RowsHost.ActualHeight / RowHeight));
    }

    private void RowsHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Vm?.Scroll(e.Delta > 0 ? -3 : 3);
        e.Handled = true;
    }

    private void JumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Vm?.JumpCommand.Execute(null);
    }

    private void Recent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is RecentItem item)
        {
            Vm?.OpenRecentCommand.Execute(item);
            list.SelectedItem = null;
        }
    }

    private void Cell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: MemoryCell cell } border || Vm is not { } vm)
            return;
        vm.Select(cell);
        if (e.ClickCount < 2 || !cell.Editable)
            return;
        e.Handled = true;
        if (cell.IsBit || (cell.Role == "label" && cell.Text is "ON" or "OFF"))
        {
            var error = vm.Edit(cell, null);
            if (error is not null)
                vm.Message = error;
            vm.Select(cell);
            return;
        }

        _editing = cell;
        EditTitle.Text = "新しい値（10進 / 0x16進 / 末尾 h で 16 進）";
        EditBox.Text = vm.EditText(cell);
        EditError.Text = string.Empty;
        EditPopup.PlacementTarget = border;
        EditPopup.IsOpen = true;
        EditBox.Focus();
        EditBox.SelectAll();
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Commit();
        else if (e.Key == Key.Escape)
            EditPopup.IsOpen = false;
    }

    private void EditOk_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (_editing is null || Vm is not { } vm)
            return;
        var error = vm.Edit(_editing, EditBox.Text);
        if (error is not null)
        {
            EditError.Text = error;
            return;
        }

        EditPopup.IsOpen = false;
        vm.Select(_editing);
    }
}
