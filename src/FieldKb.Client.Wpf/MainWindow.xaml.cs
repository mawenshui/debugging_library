namespace FieldKb.Client.Wpf;

public partial class MainWindow : ThemedWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
