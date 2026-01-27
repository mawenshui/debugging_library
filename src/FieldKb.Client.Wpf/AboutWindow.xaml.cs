using System.Windows;

namespace FieldKb.Client.Wpf;

public partial class AboutWindow : ThemedWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutViewModel vm)
        {
            return;
        }

        vm.RequestClose += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
    }
}
