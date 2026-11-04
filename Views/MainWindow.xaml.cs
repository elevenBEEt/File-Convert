using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using SDS_Convert.Models;
using SDS_Convert.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace SDS_Convert.Views;

public partial class MainWindow : Window
{
    private const int RecentLimit = 50;
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwRestore = 9;
    private const int SwMaximize = 3;
    private static readonly string[] SupportedExtensions =
    [
        ".pdf", ".doc", ".docx", ".txt", ".md", ".markdown", ".html", ".htm", ".rtf", ".odt", ".pptx", ".xlsx",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg", ".heic",
    ];

    private readonly ConversionService _converter = new();
    private readonly ObservableCollection<ConversionJob> _jobs = [];
    private readonly ObservableCollection<RecentConversion> _recent = [];
    private readonly List<HelpArticle> _helpArticles = [];
    private CancellationTokenSource? _conversionCancellation;
    private bool _isConverting;
    private bool _updatingImageOptions;
    private bool _isFullscreen;
    private NativeRect _windowedBounds;
    private IntPtr _windowedNativeStyle;
    private WindowState _windowedState;
    private string _outputFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SDS_Convert");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        QueueGrid.ItemsSource = Jobs;
        RecentList.ItemsSource = _recent;
        OutputFolderBox.Text = _outputFolder;
        LibreOfficeStatus = _converter.HasLibreOffice
            ? "LibreOffice ready · Office conversion is local"
            : "LibreOffice not found · add the portable suite to this folder";
        LoadRecent();
        UpdateRecentEmptyState();
        InitializeHelp();
        RefreshQueueCount();
        Closed += (_, _) => _conversionCancellation?.Cancel();
    }

    public ObservableCollection<ConversionJob> Jobs => _jobs;
    public string LibreOfficeStatus { get; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr window, int command);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1180, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(790, Math.Max(MinHeight, workArea.Height - 24));
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_isFullscreen);

    private void SetFullscreen(bool fullscreen)
    {
        if (fullscreen == _isFullscreen)
        {
            return;
        }

        var window = new WindowInteropHelper(this).Handle;
        if (fullscreen)
        {
            if (!GetWindowRect(window, out _windowedBounds))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the app window bounds.");
            }
            _windowedNativeStyle = GetWindowLongPtr(window, GwlStyle);
            _windowedState = WindowState;
            var monitor = System.Windows.Forms.Screen.FromHandle(window).Bounds;
            var fullscreenStyle = new IntPtr(_windowedNativeStyle.ToInt64() & ~(WsCaption | WsThickFrame));
            SetWindowStyle(window, fullscreenStyle);
            try
            {
                ShowWindow(window, SwRestore);
                SetWindowBounds(window, monitor.Left, monitor.Top, monitor.Width, monitor.Height);
                _isFullscreen = true;
                FullscreenButton.Content = "Exit full screen";
            }
            catch
            {
                SetWindowStyle(window, _windowedNativeStyle);
                throw;
            }
        }
        else
        {
            SetWindowStyle(window, _windowedNativeStyle);
            ShowWindow(window, SwRestore);
            SetWindowBounds(
                window,
                _windowedBounds.Left,
                _windowedBounds.Top,
                _windowedBounds.Right - _windowedBounds.Left,
                _windowedBounds.Bottom - _windowedBounds.Top);
            if (_windowedState == WindowState.Maximized)
            {
                ShowWindow(window, SwMaximize);
            }
            _isFullscreen = false;
            FullscreenButton.Content = "Full screen";
        }
    }

    private static void SetWindowStyle(IntPtr window, IntPtr style)
    {
        if (SetWindowLongPtr(window, GwlStyle, style) == IntPtr.Zero
            && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not update the app window style.");
        }
    }

    private static void SetWindowBounds(IntPtr window, int left, int top, int width, int height)
    {
        if (!SetWindowPos(window, IntPtr.Zero, left, top, width, height,
                SwpNoZOrder | SwpFrameChanged | SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resize the app window.");
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add files to SDS_Convert",
            Multiselect = true,
            Filter = "Supported files|*.pdf;*.doc;*.docx;*.txt;*.md;*.markdown;*.html;*.htm;*.rtf;*.odt;*.pptx;*.xlsx;*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.svg;*.heic|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            AddPaths(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a folder. Supported files in this folder and its subfolders will be added.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            AddFolder(dialog.SelectedPath);
        }
    }

    private void Window_Drop(object sender, DragEventArgs e) => HandleDrop(e);
    private void DropZone_Drop(object sender, DragEventArgs e) => HandleDrop(e);

    private void Window_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);
    private void DropZone_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);

    private static void SetDropEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void HandleDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    AddFolder(path);
                }
                else
                {
                    AddPaths([path]);
                }
            }
        }
        e.Handled = true;
    }

    private void AddFolder(string path)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            };
            AddPaths(Directory.EnumerateFiles(path, "*", options));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowError("Could not read this folder", error.Message);
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        var unsupported = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                unsupported++;
                continue;
            }

            if (_jobs.Any(job => string.Equals(job.InputPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var formats = _converter.GetAvailableTargets(path);
            var job = new ConversionJob(path, formats);
            if (formats.Count == 0)
            {
                job.Status = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? "Use PDF tools"
                    : Path.GetExtension(path).ToLowerInvariant() is ".doc" or ".docx" or ".odt" or ".pptx" or ".xlsx"
                        ? "Needs LibreOffice"
                        : "Needs another local codec";
                job.Message = _converter.GetCompatibilityMessage(path, job.OutputExtension);
            }
            else
            {
                job.Message = _converter.GetCompatibilityMessage(path, job.OutputExtension);
            }
            _jobs.Add(job);
            added++;
        }

        RefreshQueueCount();
        if (added > 0)
        {
            MainTabs.SelectedIndex = 0;
            StatusText.Text = unsupported > 0
                ? $"Added {added} file(s) · skipped {unsupported} unsupported file(s)"
                : $"Added {added} file(s) · originals remain unchanged";
        }
        else if (unsupported > 0)
        {
            StatusText.Text = $"No supported files found · skipped {unsupported} unsupported file(s)";
        }
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where converted files will be saved.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_outputFolder)
                ? _outputFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputFolder = dialog.SelectedPath;
            OutputFolderBox.Text = _outputFolder;
        }
    }

    private void SetSelectedOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            return;
        }

        var selected = QueueGrid.SelectedItems.Cast<ConversionJob>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select one or more queue rows first.";
            return;
        }
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a separate output folder for the selected files.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = _outputFolder,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            foreach (var job in selected)
            {
                job.OutputDirectory = dialog.SelectedPath;
            }
            StatusText.Text = $"Set a per-file output folder for {selected.Count} row(s).";
        }
    }

    private void UseBatchOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            return;
        }

        foreach (var job in QueueGrid.SelectedItems.Cast<ConversionJob>())
        {
            job.OutputDirectory = null;
        }
        StatusText.Text = "Selected files now use the batch output folder.";
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            return;
        }

        var ready = _jobs.Where(job => job.AvailableFormats.Count > 0
            && job.AvailableFormats.Contains(job.OutputExtension, StringComparer.OrdinalIgnoreCase)
            && job.Status is not "Done").ToList();
        if (ready.Count == 0)
        {
            StatusText.Text = _jobs.Count == 0
                ? "Add files to begin."
                : "No ready files · check the compatibility guidance or install a required local codec.";
            return;
        }

        try
        {
            var root = Path.GetPathRoot(_outputFolder);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                var estimatedBytes = ready.Sum(job => job.SizeBytes);
                if (drive.IsReady && drive.AvailableFreeSpace < estimatedBytes)
                {
                    StatusText.Text = $"Preflight warning · output drive has less than the {estimatedBytes / (1024d * 1024d):0.0} MB input size estimate.";
                    if (System.Windows.MessageBox.Show(
                        this,
                        "There may not be enough free space for this batch. Continue anyway?",
                        "Low disk space",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
            }
        }
        catch (IOException error)
        {
            StatusText.Text = $"Could not check free disk space: {error.Message}";
            return;
        }

        _isConverting = true;
        _conversionCancellation = new CancellationTokenSource();
        ConvertButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        SetQueueEditingEnabled(false);
        StatusText.Text = $"Converting {ready.Count} file(s) locally · one job at a time";

        try
        {
            foreach (var job in ready)
            {
                _conversionCancellation.Token.ThrowIfCancellationRequested();
                job.Status = "Converting";
                job.Message = _converter.GetCompatibilityMessage(job.InputPath, job.OutputExtension);
                job.Progress = 0;
                var progress = new Progress<double>(value => job.Progress = Math.Clamp(value, 0, 100));
                try
                {
                    var output = await _converter.ConvertAsync(
                        job,
                        _outputFolder,
                        progress,
                        _conversionCancellation.Token);
                    job.Status = "Done";
                    job.Progress = 100;
                    job.Message = Path.GetFileName(output);
                    AddRecent(new RecentConversion(job.InputPath, output, DateTime.Now));
                    StatusText.Text = $"Saved locally · {output}";
                }
                catch (OperationCanceledException)
                {
                    job.Status = "Canceled";
                    job.Message = "Ready to retry";
                    throw;
                }
                catch (Exception error)
                {
                    job.Status = "Error";
                    job.Message = error.Message;
                    StatusText.Text = $"{job.FileName} · {error.Message}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Canceled · completed files were kept; remaining files can be retried.";
        }
        finally
        {
            _conversionCancellation.Dispose();
            _conversionCancellation = null;
            _isConverting = false;
            ConvertButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            SetQueueEditingEnabled(true);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _conversionCancellation?.Cancel();
        StatusText.Text = "Stopping after the current safe cancellation point…";
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            return;
        }
        _jobs.Clear();
        RefreshQueueCount();
        StatusText.Text = "Queue cleared · source files were not changed.";
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            return;
        }
        foreach (var job in QueueGrid.SelectedItems.Cast<ConversionJob>().ToList())
        {
            _jobs.Remove(job);
        }
        RefreshQueueCount();
    }

    private void ApplyNamePattern_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            return;
        }

        var selected = QueueGrid.SelectedItems.Cast<ConversionJob>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select one or more files before applying a batch name.";
            return;
        }

        var pattern = BatchNamePatternBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            StatusText.Text = "Enter a non-empty output-name pattern.";
            return;
        }
        foreach (var job in selected)
        {
            job.OutputNamePattern = pattern;
        }
        StatusText.Text = $"Applied the output-name pattern to {selected.Count} file(s).";
    }

    private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedImages = QueueGrid.SelectedItems.Cast<ConversionJob>()
            .Where(job => IsRaster(job.InputPath))
            .ToList();
        ImageOptions.IsEnabled = !_isConverting;
        if (selectedImages.Count == 0)
        {
            return;
        }

        var job = selectedImages[0];
        _updatingImageOptions = true;
        ImageWidthBox.Text = job.Width.ToString();
        ImageHeightBox.Text = job.Height.ToString();
        ImageQualitySlider.Value = job.Quality;
        ImageQualityText.Text = job.Quality.ToString();
        RotationBox.SelectedIndex = job.Rotation switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };
        FlipHorizontalBox.IsChecked = job.FlipHorizontal;
        FlipVerticalBox.IsChecked = job.FlipVertical;
        _updatingImageOptions = false;
    }

    private async void PreviewSelected_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not ConversionJob job)
        {
            StatusText.Text = "Select one file to preview it.";
            return;
        }
        try
        {
            var preview = new FilePreviewWindow(job.InputPath) { Owner = this };
            await preview.LoadPreviewAsync();
            preview.ShowDialog();
        }
        catch (Exception error)
        {
            ShowError("Could not preview this file", error.Message);
        }
    }

    private void ImageOptions_Expanded(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not ConversionJob job || !IsRaster(job.InputPath))
        {
            StatusText.Text = "Select one PNG, JPEG, GIF, BMP, or TIFF row to edit image settings.";
        }
    }

    private void ImageOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingImageOptions || QueueGrid is null || QueueGrid.SelectedItems.Count == 0)
        {
            return;
        }

        if (!int.TryParse(ImageWidthBox.Text, out var width) || width < 0
            || !int.TryParse(ImageHeightBox.Text, out var height) || height < 0)
        {
            StatusText.Text = "Image width and height must be whole numbers greater than or equal to 0.";
            return;
        }

        var rotation = (RotationBox.SelectedItem as ComboBoxItem)?.Tag is string value
            && int.TryParse(value, out var angle) ? angle : 0;

        foreach (var job in QueueGrid.SelectedItems.Cast<ConversionJob>().Where(job => IsRaster(job.InputPath)))
        {
            job.Width = width;
            job.Height = height;
            job.Quality = (int)ImageQualitySlider.Value;
            job.Rotation = rotation;
            job.FlipHorizontal = FlipHorizontalBox.IsChecked == true;
            job.FlipVertical = FlipVerticalBox.IsChecked == true;
        }
    }

    private void ImageQuality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageQualityText is not null)
        {
            ImageQualityText.Text = ((int)e.NewValue).ToString();
        }
        if (!_updatingImageOptions && QueueGrid is not null && QueueGrid.SelectedItems.Count > 0)
        {
            ImageOptions_Changed(sender, e);
        }
    }

    private async void QueueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueGrid.SelectedItem is not ConversionJob job || !File.Exists(job.InputPath))
        {
            return;
        }
        try
        {
            var preview = new FilePreviewWindow(job.InputPath) { Owner = this };
            await preview.LoadPreviewAsync();
            preview.ShowDialog();
        }
        catch (Exception error)
        {
            ShowError("Could not open this file", error.Message);
        }
    }

    private async void MergePdfs_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            StatusText.Text = "Wait for the current conversion to finish or cancel it first.";
            return;
        }
        var paths = QueueGrid.SelectedItems.Cast<ConversionJob>()
            .Select(job => job.InputPath)
            .Where(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (paths.Count < 2)
        {
            StatusText.Text = "Select at least two PDF rows in the queue, then choose Merge PDFs.";
            return;
        }

        SetBusy(true, $"Merging {paths.Count} PDFs locally…");
        try
        {
            var outputs = await _converter.MergePdfsAsync(paths, _outputFolder, "Merged document", CancellationToken.None);
            foreach (var output in outputs)
            {
                AddRecent(new RecentConversion(string.Join("; ", paths.Select(Path.GetFileName)), output, DateTime.Now));
            }
            StatusText.Text = $"Merged {paths.Count} PDFs locally · {outputs[0]}";
        }
        catch (Exception error)
        {
            ShowError("PDF merge failed", error.Message);
        }
        finally
        {
            SetBusy(false, "Ready · local conversion only · no online provider is configured");
        }
    }

    private async void SplitPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_isConverting)
        {
            StatusText.Text = "Wait for the current conversion to finish or cancel it first.";
            return;
        }
        var selected = QueueGrid.SelectedItems.Cast<ConversionJob>()
            .FirstOrDefault(job => Path.GetExtension(job.InputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            StatusText.Text = "Select one PDF row in the queue, then choose Split PDF.";
            return;
        }

        SetBusy(true, "Splitting PDF locally…");
        try
        {
            var outputs = await _converter.SplitPdfAsync(selected.InputPath, _outputFolder, CancellationToken.None);
            foreach (var output in outputs)
            {
                AddRecent(new RecentConversion(selected.InputPath, output, DateTime.Now));
            }
            StatusText.Text = $"Split into {outputs.Count} page file(s) · originals remain unchanged.";
        }
        catch (Exception error)
        {
            ShowError("PDF split failed", error.Message);
        }
        finally
        {
            SetBusy(false, "Ready · local conversion only · no online provider is configured");
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 2;
        HelpSearchBox.Focus();
    }

    private void HelpSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (HelpSearchPlaceholder is not null)
        {
            HelpSearchPlaceholder.Visibility = string.IsNullOrEmpty(HelpSearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        FilterHelp();
    }

    private void HelpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DisplayHelpArticle(HelpList.SelectedItem as HelpArticle);
    }

    private void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentConversion item)
        {
            return;
        }

        var path = File.Exists(item.OutputPath) ? item.OutputPath : item.InputPath;
        if (File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                ShowError("Could not open this file", error.Message);
            }
        }
        RecentList.SelectedItem = null;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            SetFullscreen(!_isFullscreen);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _isFullscreen)
        {
            SetFullscreen(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F1)
        {
            Help_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.O)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                AddFolder_Click(this, new RoutedEventArgs());
            }
            else
            {
                AddFiles_Click(this, new RoutedEventArgs());
            }
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Enter)
        {
            Convert_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            && e.Key == Key.R)
        {
            ApplyNamePattern_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !_isConverting)
        {
            RemoveSelected_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void InitializeHelp()
    {
        _helpArticles.AddRange(
        [
            new("Quick start", "Add files or drop files/folders, choose a supported output format, set the save folder, then select Convert. Your original files are never changed."),
            new("Supported formats", "Windows local image codecs currently support PNG, JPEG, GIF, BMP, and TIFF conversions. WebP, HEIC, and SVG are listed but not currently converted by the bundled lightweight local engine. Text/markup conversion supports TXT, Markdown, HTML, and RTF with basic formatting only. DOC/DOCX/ODT/PPTX/XLSX need LibreOffice (installed or portable under the SDS_Convert folder) for conversion. PDF merge/split works locally."),
            new("Batch queue and retry", "Add multiple files to the queue. Conversion runs one file at a time to keep memory use predictable. Failed and canceled rows remain available to retry; completed rows are skipped."),
            new("Choose output names", "Edit the Output Name field per row, or select several rows, enter a batch pattern above the queue, and choose Apply names. Use {name} for the original base name, {format} for the selected output extension, and {date} for today's date. Existing names are preserved by adding a number instead of overwriting."),
            new("Image resize and quality", "Select one or more PNG, JPEG, GIF, BMP, or TIFF rows, expand Image settings, then set width/height (0 keeps that dimension), JPEG quality, rotation, or flips. Resizing keeps aspect ratio. Source and resized output frames over 50 megapixels are rejected to protect laptop memory. JPEG quality controls lossy compression; metadata may not survive transformation."),
            new("PDF merge and split", "Select two or more PDF rows and choose Merge PDFs to combine them. Select one PDF row and choose Split PDF to save each page as a separate file. Outputs go to the selected destination folder."),
            new("Privacy and online conversions", "This version uses local converters only. It has no online conversion provider configured and never uploads your files. Office conversion requires LibreOffice, which can be installed normally or kept as a portable copy inside the SDS_Convert folder."),
            new("Keyboard shortcuts and accessibility", "Ctrl+O adds files, Ctrl+Shift+O opens a folder picker, Ctrl+Enter starts conversion, Ctrl+Shift+R applies the batch naming pattern, Delete removes selected queue rows, F1 opens Help, F11 toggles full screen, and Escape exits full screen. Drag the title bar to move the window or its edges to resize it. Tab/arrow navigation and visible focus are available."),
            new("Troubleshooting", "If an Office file is not convertible, confirm that LibreOffice is installed or that the portable LibreOffice folder is beside SDS_Convert.exe, then restart the app. If an image codec is unavailable, install a trusted Windows codec for that format. Close files that another app has locked, check destination permissions/free space, then retry."),
            new("Performance", "The queue is virtualized and conversions run sequentially. Previews are loaded only when selected, and the app does not scan folders in the background. Dropping a folder scans only that folder when requested. Text input is capped at 64 MB and PDF merge/split at 500 MB to limit memory use."),
        ]);
        FilterHelp();
    }

    private void FilterHelp()
    {
        if (HelpList is null)
        {
            return;
        }
        var selectedTitle = (HelpList.SelectedItem as HelpArticle)?.Title;
        var query = HelpSearchBox.Text.Trim();
        var filteredArticles = _helpArticles.Where(article =>
            string.IsNullOrEmpty(query)
            || article.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || article.Body.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        HelpList.ItemsSource = filteredArticles;
        HelpList.SelectedItem = filteredArticles.FirstOrDefault(article => article.Title == selectedTitle)
            ?? filteredArticles.FirstOrDefault();
        HelpEmptyState.Visibility = filteredArticles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DisplayHelpArticle(HelpList.SelectedItem as HelpArticle);
    }

    private void DisplayHelpArticle(HelpArticle? article)
    {
        HelpArticleTitle.Text = article?.Title ?? "Choose a guide";
        HelpArticleBody.Text = article?.Body ?? "Select a topic from the list to see step-by-step guidance.";
    }

    private void LoadRecent()
    {
        try
        {
            var file = RecentFilePath;
            if (!File.Exists(file))
            {
                return;
            }
            var content = File.ReadAllText(file);
            var entries = JsonSerializer.Deserialize<List<RecentConversion>>(content) ?? [];
            foreach (var item in entries.Take(RecentLimit))
            {
                _recent.Add(item);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            StatusText.Text = "Recent history could not be loaded; conversions are still available.";
        }
    }

    private void AddRecent(RecentConversion item)
    {
        _recent.Insert(0, item);
        while (_recent.Count > RecentLimit)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }
        UpdateRecentEmptyState();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFilePath)!);
            File.WriteAllText(RecentFilePath, JsonSerializer.Serialize(_recent.ToList()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "File converted, but recent history could not be saved.";
        }
    }

    private void UpdateRecentEmptyState()
    {
        if (RecentEmptyState is not null)
        {
            RecentEmptyState.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static string RecentFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDS_Convert",
        "recent.json");

    private void RefreshQueueCount()
    {
        QueueCount.Text = $"  ·  {_jobs.Count} file{(_jobs.Count == 1 ? "" : "s")}";
        var showDropZone = _jobs.Count == 0;
        HeroPanel.Visibility = showDropZone ? Visibility.Visible : Visibility.Collapsed;
        DropZone.Visibility = showDropZone ? Visibility.Visible : Visibility.Collapsed;
        HeroSpacerRow.Height = showDropZone ? new GridLength(12) : new GridLength(0);
        DropZoneRow.Height = showDropZone ? new GridLength(148) : new GridLength(0);
    }

    private void SetBusy(bool busy, string message)
    {
        _isConverting = busy;
        ConvertButton.IsEnabled = !busy;
        CancelButton.Visibility = Visibility.Collapsed;
        SetQueueEditingEnabled(!busy);
        StatusText.Text = message;
    }

    private void SetQueueEditingEnabled(bool enabled)
    {
        QueueGrid.IsEnabled = enabled;
        BatchNamePatternBox.IsEnabled = enabled;
        OutputFolderBox.IsEnabled = enabled;
        ApplyNamesButton.IsEnabled = enabled;
        ChooseOutputButton.IsEnabled = enabled;
        SetSelectedOutputButton.IsEnabled = enabled;
        UseBatchOutputButton.IsEnabled = enabled;
        ImageOptions.IsEnabled = enabled;
    }

    private static bool IsRaster(string path) => new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void ShowError(string title, string message) =>
        System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
