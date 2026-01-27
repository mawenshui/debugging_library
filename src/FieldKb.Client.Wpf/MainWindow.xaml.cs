namespace FieldKb.Client.Wpf;

public partial class MainWindow : ThemedWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync(CancellationToken.None);
    }
}
