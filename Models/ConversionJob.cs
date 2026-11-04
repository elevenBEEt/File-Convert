using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace SDS_Convert.Models;

public sealed class ConversionJob : INotifyPropertyChanged
{
    private string _outputExtension;
    private string _outputNamePattern = "{name}";
    private string? _outputDirectory;
    private string _status = "Ready";
    private string _message = "";
    private double _progress;
    private int _quality = 90;
    private int _width;
    private int _height;
    private int _rotation;
    private bool _flipHorizontal;
    private bool _flipVertical;

    public ConversionJob(string inputPath, IReadOnlyList<string> availableFormats)
    {
        InputPath = inputPath;
        AvailableFormats = availableFormats;
        _outputExtension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        if (!availableFormats.Contains(_outputExtension, StringComparer.OrdinalIgnoreCase) && availableFormats.Count > 0)
        {
            _outputExtension = availableFormats[0];
        }
        SizeBytes = new FileInfo(inputPath).Length;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InputPath { get; }
    public string FileName => Path.GetFileName(InputPath);
    public string InputExtension => Path.GetExtension(InputPath).TrimStart('.').ToUpperInvariant();
    public long SizeBytes { get; }
    public IReadOnlyList<string> AvailableFormats { get; }
    public string SizeText => SizeBytes < 1024 * 1024
        ? $"{Math.Max(1, SizeBytes / 1024)} KB"
        : $"{SizeBytes / (1024d * 1024d):0.0} MB";
    public string OutputFolderLabel => string.IsNullOrWhiteSpace(OutputDirectory)
        ? "Batch output folder"
        : $"Output: {Path.GetFileName(OutputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";

    public string OutputExtension
    {
        get => _outputExtension;
        set => SetField(ref _outputExtension, value);
    }

    public string OutputNamePattern
    {
        get => _outputNamePattern;
        set => SetField(ref _outputNamePattern, value);
    }

    public string? OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (SetField(ref _outputDirectory, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputFolderLabel)));
            }
        }
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public int Quality
    {
        get => _quality;
        set => SetField(ref _quality, Math.Clamp(value, 1, 100));
    }

    public int Width
    {
        get => _width;
        set => SetField(ref _width, Math.Max(0, value));
    }

    public int Height
    {
        get => _height;
        set => SetField(ref _height, Math.Max(0, value));
    }

    public int Rotation
    {
        get => _rotation;
        set => SetField(ref _rotation, ((value % 360) + 360) % 360);
    }

    public bool FlipHorizontal
    {
        get => _flipHorizontal;
        set => SetField(ref _flipHorizontal, value);
    }

    public bool FlipVertical
    {
        get => _flipVertical;
        set => SetField(ref _flipVertical, value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
