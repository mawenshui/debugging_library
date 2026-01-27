using System.Windows;

namespace FieldKb.Client.Wpf;

public partial class DataPurgeWindow : ThemedWindow
{
    public DataPurgeWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DataPurgeViewModel vm)
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
