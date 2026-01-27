namespace FieldKb.Client.Wpf;

public static class DbBusyUiRetry
{
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, string actionName, CancellationToken ct)
    {
        try
        {
            return await action(ct);
        }
        catch (Exception ex) when (DbBusyDetector.IsBusy(ex))
        {
            await Task.Delay(250, ct);
            try
            {
                return await action(ct);
            }
            catch (Exception ex2) when (DbBusyDetector.IsBusy(ex2))
            {
                await ShowAsync(actionName);
                throw;
            }
        }
    }

    public static async Task RunAsync(Func<CancellationToken, Task> action, string actionName, CancellationToken ct)
    {
        try
        {
            await action(ct);
        }
        catch (Exception ex) when (DbBusyDetector.IsBusy(ex))
        {
            await Task.Delay(250, ct);
            try
            {
                await action(ct);
            }
            catch (Exception ex2) when (DbBusyDetector.IsBusy(ex2))
            {
                await ShowAsync(actionName);
                throw;
            }
        }
    }

    public static Task ShowAsync(string actionName)
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            System.Windows.MessageBox.Show(
                $"数据库正忙，暂时无法完成“{actionName}”。\n\n请稍后重试（已自动重试一次）。",
                "数据库忙",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }).Task;
    }
}

