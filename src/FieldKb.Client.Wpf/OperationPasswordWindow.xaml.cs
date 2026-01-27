using System.Windows;

namespace FieldKb.Client.Wpf;

public partial class OperationPasswordWindow : ThemedWindow
{
    public OperationPasswordWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OperationPasswordViewModel vm)
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
