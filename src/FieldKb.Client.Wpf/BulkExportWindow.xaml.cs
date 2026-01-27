using System.Windows;

namespace FieldKb.Client.Wpf;

public partial class BulkExportWindow : ThemedWindow
{
    public BulkExportWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BulkExportViewModel vm)
        {
            return;
        }

        vm.RequestClose += (_, ok) =>
        {
            DialogResult = ok;
            Close();
        };

        _ = vm.LoadCommand.ExecuteAsync(null);
    }
}
