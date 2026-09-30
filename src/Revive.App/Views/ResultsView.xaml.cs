using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using Revive.App.ViewModels;

namespace Revive.App.Views;

public partial class ResultsView : UserControl
{
    public ResultsView() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>Sorting is done by the view model so it survives the list refreshing during a scan.</summary>
    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in Files.Columns)
            column.SortDirection = null;
        e.Column.SortDirection = direction;
        ViewModel.Sort(e.Column.SortMemberPath, direction);
    }

    /// <summary>Space ticks or unticks the highlighted file.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Files.SelectedItem is FileItemViewModel item)
        {
            item.IsSelected = !item.IsSelected;
            e.Handled = true;
        }
    }
}
