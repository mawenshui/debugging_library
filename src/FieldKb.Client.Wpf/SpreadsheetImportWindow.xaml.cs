using System.Windows;

namespace FieldKb.Client.Wpf;

public partial class SpreadsheetImportWindow : ThemedWindow
{
    public SpreadsheetImportWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpreadsheetImportViewModel vm)
        {
            return;
        }

        vm.RequestClose += (_, ok) =>
        {
            DialogResult = ok;
            Close();
        };
    }
}
