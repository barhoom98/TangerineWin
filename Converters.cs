using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using SharpDocument = PdfSharp.Pdf.PdfDocument;
using SharpPage = PdfSharp.Pdf.PdfPage;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfPage = Windows.Data.Pdf.PdfPage;
using WinPdfPageRenderOptions = Windows.Data.Pdf.PdfPageRenderOptions;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TangerineWin;

internal static class FileKinds
{
    private static readonly HashSet<string> Img = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif" };
    private static readonly HashSet<string> Word = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".rtf", ".odt" };
    private static readonly HashSet<string> Excel = new(StringComparer.OrdinalIgnoreCase)
        { ".xls", ".xlsx", ".xlsm", ".csv", ".ods" };
    private static readonly HashSet<string> Ppt = new(StringComparer.OrdinalIgnoreCase)
        { ".ppt", ".pptx", ".odp" };

    public static bool IsImage(string p) => File.Exists(p) && Img.Contains(Path.GetExtension(p));
    public static bool IsWord(string p) => File.Exists(p) && Word.Contains(Path.GetExtension(p));
    public static bool IsExcel(string p) => File.Exists(p) && Excel.Contains(Path.GetExtension(p));
    public static bool IsPpt(string p) => File.Exists(p) && Ppt.Contains(Path.GetExtension(p));
    public static bool IsOffice(string p) => IsWord(p) || IsExcel(p) || IsPpt(p);
    public static bool IsPdf(string p) => File.Exists(p) && Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public static bool IsJpeg(string p)
    {
        string e = Path.GetExtension(p);
        return e.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsPng(string p) => Path.GetExtension(p).Equals(".png", StringComparison.OrdinalIgnoreCase);
    public static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    public static FileKind KindOf(string p) =>
        IsImage(p) ? FileKind.Image :
        IsPdf(p) ? FileKind.Pdf :
        IsOffice(p) ? FileKind.Office :
        FileKind.Other;
}

internal enum FileKind { Image, Pdf, Office, Other }

internal enum ImgFmt { Png, Jpg, Bmp, Tiff, Gif }

internal static class Paths
{
    public static string Unique(string dir, string name, string ext)
    {
        string p = Path.Combine(dir, name + ext);
        int i = 2;
        while (File.Exists(p) || Directory.Exists(p))
            p = Path.Combine(dir, $"{name} ({i++}){ext}");
        return p;
    }

    public static string? FindExe(params string[] candidates)
    {
        string[] pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string c in candidates)
        {
            if (Path.IsPathRooted(c))
            {
                if (File.Exists(c)) return c;
                continue;
            }
            foreach (string d in pathDirs)
            {
                try
                {
                    string full = Path.Combine(d.Trim('"'), c);
                    if (File.Exists(full)) return full;
                }
                catch { /* ignore bad PATH entries */ }
            }
        }
        return null;
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(string exe, IEnumerable<string> args, int timeoutMs = 180_000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start " + exe);
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderr = proc.StandardError.ReadToEndAsync();
        using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException("العملية أخذت وقت طويل");
        }
        return (proc.ExitCode, (await stdout) + (await stderr));
    }
}

/// <summary>Image conversion / resizing using WPF imaging (WIC).</summary>
internal static class ImageOps
{
    private static int ReadOrientation(string path)
    {
        try
        {
            BitmapFrame f = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (f.Metadata is BitmapMetadata m && m.ContainsQuery("System.Photo.Orientation")
                && m.GetQuery("System.Photo.Orientation") is ushort o)
                return o;
        }
        catch { /* no metadata */ }
        return 1;
    }

    /// <summary>Decodes the image and applies the EXIF orientation so phone photos don't come out sideways.</summary>
    internal static BitmapSource Load(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();

        double angle = ReadOrientation(path) switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (angle == 0) return bi;

        var tb = new TransformedBitmap(bi, new RotateTransform(angle));
        tb.Freeze();
        return tb;
    }

    private static BitmapSource Render(BitmapSource src, int w, int h, bool whiteBackground)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (DrawingContext dc = dv.RenderOpen())
        {
            if (whiteBackground) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void Save(BitmapSource bs, BitmapEncoder enc, string path)
    {
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static bool IsFormat(string p, ImgFmt f)
    {
        string e = Path.GetExtension(p).ToLowerInvariant();
        return f switch
        {
            ImgFmt.Png => e == ".png",
            ImgFmt.Jpg => e == ".jpg" || e == ".jpeg",
            ImgFmt.Bmp => e == ".bmp",
            ImgFmt.Tiff => e == ".tif" || e == ".tiff",
            ImgFmt.Gif => e == ".gif",
            _ => false,
        };
    }

    public static string Convert(string src, ImgFmt fmt)
    {
        BitmapSource bmp = Load(src);
        BitmapEncoder enc;
        string ext;
        bool flatten = false; // formats without good alpha support get a white background
        switch (fmt)
        {
            case ImgFmt.Png: enc = new PngBitmapEncoder(); ext = ".png"; break;
            case ImgFmt.Jpg: enc = new JpegBitmapEncoder { QualityLevel = 90 }; ext = ".jpg"; flatten = true; break;
            case ImgFmt.Bmp: enc = new BmpBitmapEncoder(); ext = ".bmp"; flatten = true; break;
            case ImgFmt.Tiff: enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip }; ext = ".tif"; break;
            default: enc = new GifBitmapEncoder(); ext = ".gif"; flatten = true; break;
        }
        if (flatten) bmp = Render(bmp, bmp.PixelWidth, bmp.PixelHeight, true);

        string dst = Paths.Unique(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src), ext);
        Save(bmp, enc, dst);
        return dst;
    }

    /// <summary>Saves the selected region next to the original as "name_crop" (same format for PNG/JPG, otherwise PNG).</summary>
    public static string SaveCropped(string src, BitmapSource full, Int32Rect rect)
    {
        var cropped = new CroppedBitmap(full, rect);
        cropped.Freeze();

        string name = Path.GetFileNameWithoutExtension(src) + "_crop";
        string dir = Path.GetDirectoryName(src)!;
        if (FileKinds.IsJpeg(src))
        {
            string dst = Paths.Unique(dir, name, ".jpg");
            Save(cropped, new JpegBitmapEncoder { QualityLevel = 92 }, dst);
            return dst;
        }
        else
        {
            string dst = Paths.Unique(dir, name, ".png");
            Save(cropped, new PngBitmapEncoder(), dst);
            return dst;
        }
    }

    /// <summary>Re-renders the pixels into a fresh bitmap, which drops EXIF/GPS/camera metadata.</summary>
    public static string StripMetadata(string src)
    {
        BitmapSource bmp = Load(src);
        bool jpeg = FileKinds.IsJpeg(src);
        BitmapSource clean = Render(bmp, bmp.PixelWidth, bmp.PixelHeight, jpeg);
        string name = Path.GetFileNameWithoutExtension(src) + "_clean";
        string dir = Path.GetDirectoryName(src)!;
        if (jpeg)
        {
            string dst = Paths.Unique(dir, name, ".jpg");
            Save(clean, new JpegBitmapEncoder { QualityLevel = 92 }, dst);
            return dst;
        }
        else
        {
            string dst = Paths.Unique(dir, name, ".png");
            Save(clean, new PngBitmapEncoder(), dst);
            return dst;
        }
    }

    /// <summary>Downscale so the longest side is at most 1920px and recompress (JPEG q72 / PNG).</summary>
    public static string Shrink(string src)
    {
        BitmapSource bmp = Load(src);
        double scale = Math.Min(1.0, 1920.0 / Math.Max(bmp.PixelWidth, bmp.PixelHeight));
        int w = Math.Max(1, (int)Math.Round(bmp.PixelWidth * scale));
        int h = Math.Max(1, (int)Math.Round(bmp.PixelHeight * scale));
        string name = Path.GetFileNameWithoutExtension(src) + "_small";
        string dir = Path.GetDirectoryName(src)!;
        if (FileKinds.IsPng(src))
        {
            string dst = Paths.Unique(dir, name, ".png");
            Save(Render(bmp, w, h, false), new PngBitmapEncoder(), dst);
            return dst;
        }
        else
        {
            string dst = Paths.Unique(dir, name, ".jpg");
            Save(Render(bmp, w, h, true), new JpegBitmapEncoder { QualityLevel = 72 }, dst);
            return dst;
        }
    }

    public static string ToPdf(string src)
    {
        // Re-encode to PNG/JPEG in memory first so any WIC-readable format (HEIC, WebP...) works.
        BitmapSource bmp = Load(src);
        BitmapSource flat = Render(bmp, bmp.PixelWidth, bmp.PixelHeight, true);
        string tmp = Path.Combine(Path.GetTempPath(), "tangerine_" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            Save(flat, new JpegBitmapEncoder { QualityLevel = 92 }, tmp);

            using var doc = new SharpDocument();
            SharpPage page = doc.AddPage();
            using XImage img = XImage.FromFile(tmp);

            double w = img.PointWidth, h = img.PointHeight;
            double fit = Math.Min(1.0, 842.0 / Math.Max(w, h));
            page.Width = XUnit.FromPoint(w * fit);
            page.Height = XUnit.FromPoint(h * fit);
            using (XGraphics gfx = XGraphics.FromPdfPage(page))
                gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);

            string dst = Paths.Unique(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src), ".pdf");
            doc.Save(dst);
            return dst;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}

/// <summary>Word / Excel / PowerPoint conversions: Microsoft Office via COM, falling back to LibreOffice.</summary>
internal static class OfficeOps
{
    public static async Task<string> ToPdf(string src)
    {
        string dir = Path.GetDirectoryName(src)!;
        string dst = Paths.Unique(dir, Path.GetFileNameWithoutExtension(src), ".pdf");

        string progId = FileKinds.IsWord(src) ? "Word.Application"
                      : FileKinds.IsExcel(src) ? "Excel.Application"
                      : "PowerPoint.Application";

        if (Type.GetTypeFromProgID(progId) != null)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (FileKinds.IsWord(src)) WordSave(src, dst, 17);        // wdFormatPDF
                    else if (FileKinds.IsExcel(src)) ExcelToPdf(src, dst);
                    else PowerPointToPdf(src, dst);
                });
                if (File.Exists(dst)) return dst;
            }
            catch (COMException)
            {
                // fall through to LibreOffice
            }
        }

        string? soffice = FindSoffice();
        if (soffice == null)
            throw new InvalidOperationException("لازم يكون عندك Microsoft Office أو LibreOffice لتحويل هالملف");

        return await LibreOffice(soffice, src, dir, "pdf", null, dst);
    }

    public static async Task<string> PdfToWord(string src)
    {
        string dir = Path.GetDirectoryName(src)!;
        string dst = Paths.Unique(dir, Path.GetFileNameWithoutExtension(src), ".docx");

        if (Type.GetTypeFromProgID("Word.Application") != null)
        {
            try
            {
                await Task.Run(() => WordSave(src, dst, 16)); // wdFormatDocumentDefault (.docx)
                if (File.Exists(dst)) return dst;
            }
            catch (COMException)
            {
                // fall through to LibreOffice
            }
        }

        string? soffice = FindSoffice();
        if (soffice == null)
            throw new InvalidOperationException("تحويل PDF إلى Word يحتاج Microsoft Word أو LibreOffice");

        return await LibreOffice(soffice, src, dir, "docx:MS Word 2007 XML", "writer_pdf_import", dst);
    }

    private static string? FindSoffice() => Paths.FindExe(
        "soffice.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"));

    private static async Task<string> LibreOffice(string soffice, string src, string dir, string target, string? inFilter, string dst)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "tangerine_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var args = new List<string> { "--headless" };
            if (inFilter != null) args.Add("--infilter=" + inFilter);
            args.AddRange(new[] { "--convert-to", target, "--outdir", tmp, src });
            var (code, output) = await Paths.RunAsync(soffice, args);

            string? produced = Directory.EnumerateFiles(tmp).FirstOrDefault();
            if (code != 0 || produced == null)
                throw new InvalidOperationException("فشل التحويل عبر LibreOffice: " + output.Trim());

            File.Move(produced, dst);
            return dst;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static void WordSave(string src, string dst, int format)
    {
        Type t = Type.GetTypeFromProgID("Word.Application")!;
        dynamic app = Activator.CreateInstance(t)!;
        try
        {
            app.Visible = false;
            app.DisplayAlerts = 0;
            dynamic doc = app.Documents.Open(src, false, true); // FileName, ConfirmConversions, ReadOnly
            try { doc.SaveAs2(dst, format); }
            finally { doc.Close(false); }
        }
        finally
        {
            try { app.Quit(false); } catch { }
        }
    }

    private static void ExcelToPdf(string src, string dst)
    {
        Type t = Type.GetTypeFromProgID("Excel.Application")!;
        dynamic app = Activator.CreateInstance(t)!;
        try
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            dynamic wb = app.Workbooks.Open(src, 0, true); // Filename, UpdateLinks, ReadOnly
            try { wb.ExportAsFixedFormat(0, dst); }        // xlTypePDF
            finally { wb.Close(false); }
        }
        finally
        {
            try { app.Quit(); } catch { }
        }
    }

    private static void PowerPointToPdf(string src, string dst)
    {
        Type t = Type.GetTypeFromProgID("PowerPoint.Application")!;
        dynamic app = Activator.CreateInstance(t)!;
        try
        {
            // Presentations.Open(FileName, ReadOnly, Untitled, WithWindow)
            dynamic pres = app.Presentations.Open(src, -1, 0, 0);
            try { pres.SaveAs(dst, 32); } // ppSaveAsPDF
            finally { pres.Close(); }
        }
        finally
        {
            try { app.Quit(); } catch { }
        }
    }
}

internal static class PdfOps
{
    /// <summary>Renders every page of a PDF to PNG files in a new "&lt;name&gt;_pages" folder (uses the built-in Windows PDF renderer).</summary>
    public static async Task<string> ToImages(string src)
    {
        string dir = Paths.Unique(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + "_pages", "");
        Directory.CreateDirectory(dir);

        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(src));
        WinPdfDocument doc = await WinPdfDocument.LoadFromFileAsync(file);

        for (uint i = 0; i < doc.PageCount; i++)
        {
            using WinPdfPage page = doc.GetPage(i);
            using var ras = new InMemoryRandomAccessStream();
            var opts = new WinPdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * 2) };
            await page.RenderToStreamAsync(ras, opts);
            ras.Seek(0);
            using Stream s = ras.AsStreamForRead();
            using FileStream fs = File.Create(Path.Combine(dir, $"page_{i + 1:000}.png"));
            await s.CopyToAsync(fs);
        }
        return dir;
    }

    public static string? FindGhostscript()
    {
        string? found = Paths.FindExe("gswin64c.exe", "gswin32c.exe", "gs.exe");
        if (found != null) return found;

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string gsDir = Path.Combine(root, "gs");
            if (!Directory.Exists(gsDir)) continue;
            foreach (string v in Directory.EnumerateDirectories(gsDir).OrderByDescending(x => x))
            {
                foreach (string exe in new[] { "gswin64c.exe", "gswin32c.exe" })
                {
                    string p = Path.Combine(v, "bin", exe);
                    if (File.Exists(p)) return p;
                }
            }
        }
        return null;
    }

    public static async Task<string> Compress(string gs, string src)
    {
        string dst = Paths.Unique(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + "_small", ".pdf");
        var (code, output) = await Paths.RunAsync(gs, new[]
        {
            "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.4", "-dPDFSETTINGS=/ebook",
            "-dNOPAUSE", "-dQUIET", "-dBATCH", "-sOutputFile=" + dst, src,
        });
        if (code != 0 || !File.Exists(dst))
            throw new InvalidOperationException("فشل ضغط PDF: " + output.Trim());

        // If the "compressed" file ended up bigger, keep the original size info honest: drop it.
        if (new FileInfo(dst).Length >= new FileInfo(src).Length)
        {
            File.Delete(dst);
            throw new InvalidOperationException("الملف مضغوط أصلاً ولا يمكن تصغيره أكثر");
        }
        return dst;
    }
}

internal static class ZipOps
{
    public static string ZipItems(IReadOnlyList<string> items)
    {
        string first = items[0].TrimEnd('\\', '/');
        string dir = Path.GetDirectoryName(first)!;
        string name = items.Count == 1
            ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
            : "Archive";
        string zipPath = Paths.Unique(dir, name, ".zip");

        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (string raw in items)
            {
                string item = raw.TrimEnd('\\', '/');
                if (File.Exists(item))
                {
                    zip.CreateEntryFromFile(item, Path.GetFileName(item), CompressionLevel.SmallestSize);
                }
                else if (Directory.Exists(item))
                {
                    AddDirectory(zip, item, Path.GetFileName(item));
                }
            }
        }
        return zipPath;
    }

    /// <summary>Adds a folder recursively. Junctions / symlinks to folders are skipped so a link can never lead off the allowed drive.</summary>
    private static void AddDirectory(ZipArchive zip, string dir, string entryPrefix)
    {
        foreach (string f in Directory.EnumerateFiles(dir))
            zip.CreateEntryFromFile(f, entryPrefix + "/" + Path.GetFileName(f), CompressionLevel.SmallestSize);

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
            AddDirectory(zip, sub, entryPrefix + "/" + Path.GetFileName(sub));
        }
    }
}
