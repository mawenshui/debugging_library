using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FieldKb.Client.Wpf;

public sealed partial class AttachmentPreviewViewModel : ObservableObject
{
    private const long MaxTextPreviewBytes = 2 * 1024 * 1024;
    private readonly Action _close;

    public AttachmentPreviewViewModel(string filePath, string fileName, Action close)
    {
        FilePath = filePath;
        FileName = fileName;
        _close = close;

        OpenExternalCommand = new RelayCommand(OpenExternal);
        CloseCommand = new RelayCommand(() => _close());

        Load();
    }

    public string FilePath { get; }
    public string FileName { get; }

    [ObservableProperty]
    private ImageSource? _imageSource;

    [ObservableProperty]
    private string _textContent = string.Empty;

    [ObservableProperty]
    private bool _isImage;

    [ObservableProperty]
    private bool _isText;

    [ObservableProperty]
    private bool _isUnknown;

    public IRelayCommand OpenExternalCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public static bool CanPreview(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff"
            or ".txt" or ".log" or ".json" or ".xml" or ".csv" or ".md" or ".yaml" or ".yml";
    }

    private void Load()
    {
        ImageSource = null;
        TextContent = string.Empty;
        IsImage = false;
        IsText = false;
        IsUnknown = false;

        var ext = Path.GetExtension(FileName ?? string.Empty).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff")
        {
            LoadImage();
            return;
        }

        if (ext is ".txt" or ".log" or ".json" or ".xml" or ".csv" or ".md" or ".yaml" or ".yml")
        {
            LoadText();
            return;
        }

        IsUnknown = true;
    }

    private void LoadImage()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(FilePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            ImageSource = bmp;
            IsImage = true;
        }
        catch
        {
            ImageSource = null;
            IsImage = false;
            IsUnknown = true;
        }
    }

    private void LoadText()
    {
        try
        {
            var fileInfo = new FileInfo(FilePath);
            if (!fileInfo.Exists)
            {
                TextContent = "附件文件不存在。";
                IsText = true;
                return;
            }

            if (fileInfo.Length > MaxTextPreviewBytes)
            {
                using var fs = File.OpenRead(FilePath);
                var buf = new byte[MaxTextPreviewBytes];
                var read = fs.Read(buf, 0, buf.Length);
                var text = DecodeText(buf.AsSpan(0, read));
                TextContent = text + $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}预览已截断：文件较大（{fileInfo.Length} bytes），仅展示前 {read} bytes。";
                IsText = true;
                return;
            }

            var bytes = File.ReadAllBytes(FilePath);
            TextContent = DecodeText(bytes);
            IsText = true;
        }
        catch
        {
            TextContent = string.Empty;
            IsText = false;
            IsUnknown = true;
        }
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return Encoding.Default.GetString(bytes.ToArray());
        }
    }

    private void OpenExternal()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
    }
}
