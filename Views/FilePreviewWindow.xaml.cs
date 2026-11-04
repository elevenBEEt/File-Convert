using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf.IO;

namespace SDS_Convert.Views;

public partial class FilePreviewWindow : Window
{
    private readonly string _path;

    public FilePreviewWindow(string path)
    {
        InitializeComponent();
        _path = path;
        PreviewName.Text = Path.GetFileName(path);
        var info = new FileInfo(path);
        PreviewInfo.Text = $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} · {info.Length / (1024d * 1024d):0.0} MB · loaded on demand";
    }

    public async Task LoadPreviewAsync()
    {
        var extension = Path.GetExtension(_path).ToLowerInvariant();
        try
        {
            if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff")
            {
                var image = await Task.Run(() =>
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 1200;
                    bitmap.UriSource = new Uri(_path);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                });
                PreviewImage.Source = image;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewMessage.Visibility = Visibility.Collapsed;
                return;
            }

            if (extension == ".pdf")
            {
                var pages = await Task.Run(() =>
                {
                    using var document = PdfReader.Open(_path, PdfDocumentOpenMode.Import);
                    return document.PageCount;
                });
                PreviewMessage.Text = $"PDF document · {pages} page(s)\n\nThis lightweight build shows PDF file information here. Use Merge PDFs or Split PDF from the queue to edit it locally.";
                return;
            }

            if (extension is ".txt" or ".md" or ".markdown" or ".html" or ".htm" or ".rtf")
            {
                var text = await Task.Run(() => ReadSnippet(_path, 24_000));
                PreviewText.Text = text;
                PreviewText.Visibility = Visibility.Visible;
                PreviewMessage.Visibility = Visibility.Collapsed;
                return;
            }

            PreviewMessage.Text = "No local preview codec is available for this format. Your source file has not been changed.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            PreviewMessage.Text = $"Preview unavailable: {error.Message}";
        }
    }

    private static string ReadSnippet(string path, int maxCharacters)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[maxCharacters];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, length);
        return reader.Peek() >= 0 ? text + "\n\n… Preview truncated to keep memory use low." : text;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
