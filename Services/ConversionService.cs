using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SDS_Convert.Models;

namespace SDS_Convert.Services;

public sealed class ConversionService
{
    private const long MaxImagePixels = 50_000_000;
    private const long MaxTextBytes = 64 * 1024 * 1024;
    private const long MaxPdfBatchBytes = 500 * 1024 * 1024;
    private static readonly string[] RasterInputs = ["png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff"];
    private static readonly string[] RasterOutputs = ["png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff"];
    private static readonly string[] TextFormats = ["txt", "md", "html", "htm", "rtf"];
    private static readonly string[] OfficeFormats = ["doc", "docx", "odt", "pptx", "xlsx"];
    private static readonly string[] OfficeTargets = ["pdf", "doc", "docx", "odt", "pptx", "xlsx", "txt", "html", "rtf"];
    private static readonly string[] AllFormats = ["pdf", "docx", "doc", "txt", "md", "html", "rtf", "odt", "pptx", "xlsx", "png", "jpg", "jpeg", "webp", "gif", "bmp", "tif", "tiff", "svg", "heic"];

    public string? LibreOfficePath { get; } = FindLibreOffice();
    public bool HasLibreOffice => LibreOfficePath is not null;

    public IReadOnlyList<string> SupportedInputs => AllFormats;

    public IReadOnlyList<string> GetAvailableTargets(string inputPath)
    {
        var extension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        if (extension == "markdown")
        {
            extension = "md";
        }
        if (RasterInputs.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return RasterOutputs;
        }
        if (TextFormats.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return TextFormats;
        }
        if (OfficeFormats.Contains(extension, StringComparer.OrdinalIgnoreCase) && HasLibreOffice)
        {
            return OfficeTargets;
        }
        return [];
    }

    public string GetCompatibilityMessage(string inputPath, string target)
    {
        var ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        if (ext == "markdown")
        {
            ext = "md";
        }
        if (RasterInputs.Contains(ext, StringComparer.OrdinalIgnoreCase)
            && RasterOutputs.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            return target is "jpg" or "jpeg"
                ? "JPEG is lossy; transparent pixels become a solid background. Animation and some metadata may not carry over."
                : "Local Windows imaging codecs. Metadata preservation depends on the source codec.";
        }
        if (TextFormats.Contains(ext, StringComparer.OrdinalIgnoreCase)
            && TextFormats.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            return "Text and basic formatting only; complex page layout is not preserved.";
        }
        if (OfficeFormats.Contains(ext, StringComparer.OrdinalIgnoreCase) && !HasLibreOffice)
        {
            return "Install LibreOffice to convert Office documents locally. No upload is made.";
        }
        if (OfficeFormats.Contains(ext, StringComparer.OrdinalIgnoreCase) && HasLibreOffice)
        {
            return "Uses local LibreOffice; complex layout or unsupported features may change.";
        }
        if (target is "webp" or "heic" or "svg")
        {
            return "This Windows codec is not included in the lightweight local engine.";
        }
        return "This input/output pair is not supported by the local engine.";
    }

    public async Task<string> ConvertAsync(
        ConversionJob job,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetDirectory = string.IsNullOrWhiteSpace(job.OutputDirectory) ? destination : job.OutputDirectory;
        Directory.CreateDirectory(targetDirectory);
        var outputPath = CreateOutputPath(job, targetDirectory);
        var inputExtension = Path.GetExtension(job.InputPath).TrimStart('.').ToLowerInvariant();
        if (inputExtension == "markdown")
        {
            inputExtension = "md";
        }
        var target = job.OutputExtension.TrimStart('.').ToLowerInvariant();

        if (RasterInputs.Contains(inputExtension, StringComparer.OrdinalIgnoreCase)
            && RasterOutputs.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            await Task.Run(() => ConvertImage(job, outputPath, progress, cancellationToken), cancellationToken);
        }
        else if (TextFormats.Contains(inputExtension, StringComparer.OrdinalIgnoreCase)
            && TextFormats.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            await Task.Run(() => ConvertText(job.InputPath, outputPath, target, cancellationToken), cancellationToken);
            progress?.Report(100);
        }
        else if (OfficeFormats.Contains(inputExtension, StringComparer.OrdinalIgnoreCase)
            && HasLibreOffice)
        {
            await ConvertWithLibreOfficeAsync(job.InputPath, outputPath, cancellationToken);
            progress?.Report(100);
        }
        else
        {
            throw new NotSupportedException(GetCompatibilityMessage(job.InputPath, target));
        }

        return outputPath;
    }

    public async Task<IReadOnlyList<string>> MergePdfsAsync(
        IReadOnlyList<string> sources,
        string destination,
        string outputName,
        CancellationToken cancellationToken)
    {
        if (sources.Count < 2)
        {
            throw new InvalidOperationException("Select at least two PDF files to merge.");
        }
        if (sources.Sum(path => new FileInfo(path).Length) > MaxPdfBatchBytes)
        {
            throw new InvalidOperationException("For this low-memory mode, merge PDFs totaling no more than 500 MB at a time.");
        }

        Directory.CreateDirectory(destination);
        var output = GetUniquePath(Path.Combine(destination, SanitizeFileName(outputName) + ".pdf"));
        await Task.Run(() =>
        {
            using var merged = new PdfDocument();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
                for (var index = 0; index < input.PageCount; index++)
                {
                    merged.AddPage(input.Pages[index]);
                }
            }
            using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            merged.Save(outputStream);
        }, cancellationToken);
        return [output];
    }

    public async Task<IReadOnlyList<string>> SplitPdfAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(source).Length > MaxPdfBatchBytes)
        {
            throw new InvalidOperationException("For this low-memory mode, split PDFs no larger than 500 MB.");
        }
        Directory.CreateDirectory(destination);
        return await Task.Run<IReadOnlyList<string>>(() =>
        {
            using var input = PdfReader.Open(source, PdfDocumentOpenMode.Import);
            var outputs = new List<string>(input.PageCount);
            for (var index = 0; index < input.PageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pageFile = new PdfDocument();
                pageFile.AddPage(input.Pages[index]);
                var name = $"{Path.GetFileNameWithoutExtension(source)}_page_{index + 1:000}.pdf";
                var output = GetUniquePath(Path.Combine(destination, name));
                using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                pageFile.Save(outputStream);
                outputs.Add(output);
            }
            return outputs;
        }, cancellationToken);
    }

    public static string FormatPatternPreview(string pattern, string sourcePath, string target)
    {
        return pattern
            .Replace("{name}", Path.GetFileNameWithoutExtension(sourcePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{format}", target.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static void ConvertImage(
        ConversionJob job,
        string outputPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var input = new FileStream(job.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);

        BitmapEncoder encoder = Path.GetExtension(outputPath).ToLowerInvariant() switch
        {
            ".png" => new PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = job.Quality },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => throw new NotSupportedException($"The Windows codec for {job.OutputExtension.ToUpperInvariant()} is unavailable."),
        };

        for (var index = 0; index < decoder.Frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = decoder.Frames[index];
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxImagePixels)
            {
                throw new InvalidOperationException("This image exceeds the 50-megapixel low-memory processing limit.");
            }
            BitmapSource transformed = frame;
            if (job.Width > 0 || job.Height > 0)
            {
                var scaleX = job.Width > 0 ? (double)job.Width / frame.PixelWidth : double.NaN;
                var scaleY = job.Height > 0 ? (double)job.Height / frame.PixelHeight : double.NaN;
                var scale = double.IsNaN(scaleX) ? scaleY : double.IsNaN(scaleY) ? scaleX : Math.Min(scaleX, scaleY);
                if (!double.IsFinite(scale) || scale <= 0)
                {
                    throw new InvalidOperationException("Enter a valid image width or height.");
                }
                transformed = new TransformedBitmap(transformed, new ScaleTransform(scale, scale));
            }
            var angle = job.Rotation;
            if (angle != 0)
            {
                transformed = new TransformedBitmap(transformed, new RotateTransform(angle));
            }
            if (job.FlipHorizontal || job.FlipVertical)
            {
                transformed = new TransformedBitmap(
                    transformed,
                    new ScaleTransform(job.FlipHorizontal ? -1 : 1, job.FlipVertical ? -1 : 1));
            }
            if ((long)transformed.PixelWidth * transformed.PixelHeight > MaxImagePixels)
            {
                throw new InvalidOperationException("The resized image would exceed the 50-megapixel low-memory processing limit.");
            }

            encoder.Frames.Add(BitmapFrame.Create(transformed));
            progress?.Report((index + 1d) / decoder.Frames.Count * 95);
        }

        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private static void ConvertText(string inputPath, string outputPath, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        if (input == "markdown")
        {
            input = "md";
        }
        if (new FileInfo(inputPath).Length > MaxTextBytes)
        {
            throw new InvalidOperationException("This text or markup file is larger than the 64 MB local-processing limit.");
        }
        if (input == target)
        {
            File.Copy(inputPath, outputPath, overwrite: false);
            return;
        }
        var sourceText = File.ReadAllText(inputPath);
        var text = input switch
        {
            "rtf" => ConvertBasicRtfToText(sourceText),
            "html" or "htm" when target is "md" or "markdown" => ConvertBasicHtmlToMarkdown(sourceText),
            "html" or "htm" => StripHtml(sourceText),
            _ => sourceText,
        };

        if (target is "html" or "htm" && input is not "html" and not "htm")
        {
            var encoded = System.Net.WebUtility.HtmlEncode(text);
            text = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{Path.GetFileNameWithoutExtension(inputPath)}</title></head><body><pre>{encoded}</pre></body></html>";
        }
        else if (target == "rtf" && input != "rtf")
        {
            WriteNewText(outputPath, ConvertPlainTextToRtf(text));
            return;
        }

        WriteNewText(outputPath, text);
    }

    private static void WriteNewText(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string StripHtml(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"</(p|div|h[1-6]|li|br|tr)\s*>", Environment.NewLine, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text);
    }

    private static string ConvertBasicRtfToText(string rtf)
    {
        var text = Regex.Replace(rtf, @"\\'[0-9a-fA-F]{2}", match =>
        {
            var value = byte.Parse(match.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Encoding.Latin1.GetString([value]);
        });
        text = Regex.Replace(text, @"\\(par|line)\b ?", Environment.NewLine, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\\tab\b ?", "\t", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\\u(-?\d+)\??", match =>
        {
            var code = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return ((char)(ushort)code).ToString();
        });
        text = Regex.Replace(text, @"\\[a-zA-Z]+-?\d* ?", "");
        text = Regex.Replace(text, @"\\([{}\\])", "$1");
        text = Regex.Replace(text, @"\\[\r\n]+", "");
        text = text.Replace("{", "").Replace("}", "");
        return text.Trim();
    }

    private static string ConvertPlainTextToRtf(string text)
    {
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal)
            .Replace("\r\n", "\\par\n", StringComparison.Ordinal)
            .Replace("\n", "\\par\n", StringComparison.Ordinal);
        return "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Segoe UI;}}\\f0\\fs22 " + escaped + "}";
    }

    private static string ConvertBasicHtmlToMarkdown(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<h1\b[^>]*>(.*?)</h1\s*>", "# $1" + Environment.NewLine, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<h2\b[^>]*>(.*?)</h2\s*>", "## $1" + Environment.NewLine, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<(strong|b)\b[^>]*>(.*?)</\1\s*>", "**$2**", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<(em|i)\b[^>]*>(.*?)</\1\s*>", "*$2*", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<a\b[^>]*href\s*=\s*['""]([^'""]+)['""][^>]*>(.*?)</a\s*>", "[$2]($1)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"</(p|div|li|br)\s*>", Environment.NewLine, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private async Task ConvertWithLibreOfficeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        if (LibreOfficePath is null)
        {
            throw new InvalidOperationException("LibreOffice is required for this local conversion. Install it, then restart SDS_Convert. Your file was not uploaded.");
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "SDS_Convert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = LibreOfficePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            var profileDirectory = Path.Combine(tempDirectory, "profile");
            Directory.CreateDirectory(profileDirectory);
            info.ArgumentList.Add($"-env:UserInstallation={new Uri(profileDirectory).AbsoluteUri}");
            info.ArgumentList.Add("--headless");
            info.ArgumentList.Add("--convert-to");
            info.ArgumentList.Add(Path.GetExtension(outputPath).TrimStart('.'));
            info.ArgumentList.Add("--outdir");
            info.ArgumentList.Add(tempDirectory);
            info.ArgumentList.Add(inputPath);

            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start LibreOffice.");
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            });
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), standardOutput, standardError);
            if (process.ExitCode != 0)
            {
                var error = await standardError;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "LibreOffice could not convert this file." : error.Trim());
            }

            var converted = Path.Combine(tempDirectory, Path.GetFileNameWithoutExtension(inputPath) + Path.GetExtension(outputPath));
            if (!File.Exists(converted))
            {
                throw new InvalidOperationException("LibreOffice did not produce the expected output file.");
            }
            File.Copy(converted, outputPath, overwrite: false);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string CreateOutputPath(ConversionJob job, string destination)
    {
        var baseName = FormatPatternPreview(job.OutputNamePattern, job.InputPath, job.OutputExtension);
        return GetUniquePath(Path.Combine(destination, SanitizeFileName(baseName) + "." + job.OutputExtension.ToLowerInvariant()));
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Converted file" : sanitized;
    }

    private static string? FindLibreOffice()
    {
        var candidates = new List<string>();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0;
             directory is not null && depth <= 5;
             directory = directory.Parent, depth++)
        {
            candidates.Add(Path.Combine(directory.FullName, "LibreOfficePortable", "App", "libreoffice", "program", "soffice.com"));
            candidates.Add(Path.Combine(directory.FullName, "LibreOfficePortable", "App", "libreoffice", "program", "soffice.exe"));
            candidates.Add(Path.Combine(directory.FullName, "App", "libreoffice", "program", "soffice.com"));
            candidates.Add(Path.Combine(directory.FullName, "App", "libreoffice", "program", "soffice.exe"));
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        candidates.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.com"));
        candidates.Add(Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.com"));
        candidates.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe"));
        candidates.Add(Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.exe"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => new[]
            {
                Path.Combine(directory, "soffice.com"),
                Path.Combine(directory, "soffice.exe"),
            }));
        return candidates.FirstOrDefault(File.Exists);
    }
}
