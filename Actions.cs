using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TangerineWin;

public sealed record ActionResult(string Message, string? Path);

/// <summary>Convert = Shift wheel (change format). Tools = Ctrl wheel (compress, clean, zip...).</summary>
public enum WheelMode { Convert, Tools }

public sealed class ActionDef
{
    /// <summary>Optional emoji shown above the label. Empty for plain-text wedges (format names).</summary>
    public string Icon { get; }
    public string Label { get; }
    public Func<string, bool> Supports { get; }
    public Func<IReadOnlyList<string>, Task<ActionResult>> Run { get; }

    /// <summary>True when the action opens its own window (no "working..." notification needed).</summary>
    public bool Interactive { get; }

    public ActionDef(string icon, string label, Func<string, bool> supports,
                     Func<IReadOnlyList<string>, Task<ActionResult>> run, bool interactive = false)
    {
        Icon = icon;
        Label = label;
        Supports = supports;
        Run = run;
        Interactive = interactive;
    }
}

public static class ActionCatalog
{
    // ---- convert wheel (Shift) --------------------------------------------------------------

    private static readonly ActionDef[] ImageConvert =
    {
        ImgConvert(ImgFmt.Png, "PNG"),
        ImgConvert(ImgFmt.Jpg, "JPG"),
        ImgConvert(ImgFmt.Bmp, "BMP"),
        ImgConvert(ImgFmt.Tiff, "TIFF"),
        ImgConvert(ImgFmt.Gif, "GIF"),
        new("", "PDF", FileKinds.IsImage,
            files => PerFile(files, FileKinds.IsImage, p => Task.FromResult(ImageOps.ToPdf(p)))),
    };

    private static readonly ActionDef[] PdfConvert =
    {
        new("", "Word", FileKinds.IsPdf,
            files => PerFile(files, FileKinds.IsPdf, OfficeOps.PdfToWord)),
        new("", "صور PNG", FileKinds.IsPdf,
            files => PerFile(files, FileKinds.IsPdf, PdfOps.ToImages)),
    };

    private static readonly ActionDef[] OfficeConvert =
    {
        new("", "PDF", FileKinds.IsOffice,
            files => PerFile(files, FileKinds.IsOffice, OfficeOps.ToPdf)),
    };

    // ---- tools wheel (Ctrl) -----------------------------------------------------------------

    private static readonly ActionDef Zip =
        new("📦", "ZIP", FileKinds.Exists, ZipAll);

    private static readonly ActionDef[] ImageTools =
    {
        new("📉", "ضغط", FileKinds.IsImage,
            files => PerFile(files, FileKinds.IsImage, p => Task.FromResult(ImageOps.Shrink(p)))),
        new("✂️", "قص", FileKinds.IsImage, CropImage, interactive: true),
        new("🏷️", "Metadata", FileKinds.IsImage,
            files => PerFile(files, FileKinds.IsImage, p => Task.FromResult(ImageOps.StripMetadata(p)))),
        Zip,
    };

    private static readonly ActionDef[] PdfTools =
    {
        new("📉", "ضغط PDF", FileKinds.IsPdf, CompressPdfs),
        Zip,
    };

    private static readonly ActionDef[] OtherTools = { Zip };

    /// <summary>The wedges to show for the dragged files (the first file decides the wheel).</summary>
    public static IReadOnlyList<ActionDef> For(WheelMode mode, IReadOnlyList<string> files)
    {
        string? first = files.FirstOrDefault(FileKinds.Exists);
        if (first == null) return Array.Empty<ActionDef>();

        FileKind kind = FileKinds.KindOf(first);
        IEnumerable<ActionDef> list = mode == WheelMode.Convert
            ? kind switch
            {
                FileKind.Image => ImageConvert,
                FileKind.Pdf => PdfConvert,
                FileKind.Office => OfficeConvert,
                _ => Array.Empty<ActionDef>(),
            }
            : kind switch
            {
                FileKind.Image => ImageTools,
                FileKind.Pdf => PdfTools,
                _ => OtherTools,
            };

        // hide wedges that make no sense for these files (e.g. "PNG" for a file that is already PNG)
        return list.Where(a => files.Any(a.Supports)).ToList();
    }

    // ---- implementations --------------------------------------------------------------------

    private static ActionDef ImgConvert(ImgFmt fmt, string label)
    {
        Func<string, bool> ok = p => FileKinds.IsImage(p) && !ImageOps.IsFormat(p, fmt);
        return new ActionDef("", label, ok,
            files => PerFile(files, ok, p => Task.FromResult(ImageOps.Convert(p, fmt))));
    }

    private static async Task<ActionResult> PerFile(
        IReadOnlyList<string> files,
        Func<string, bool> accepts,
        Func<string, Task<string>> work)
    {
        string? last = null;
        int ok = 0, skipped = 0;
        var errors = new List<string>();

        foreach (string f in files)
        {
            if (!accepts(f)) { skipped++; continue; }
            try
            {
                last = await Task.Run(() => work(f));
                ok++;
            }
            catch (Exception ex)
            {
                errors.Add(System.IO.Path.GetFileName(f) + ": " + ex.Message);
            }
        }

        string msg = ok > 0 ? $"تم تحويل {ok} ملف" : "ما انعمل تحويل";
        if (skipped > 0) msg += $" (تخطّيت {skipped} نوع غير مدعوم)";
        if (errors.Count > 0) msg += "\n" + string.Join("\n", errors.Take(3));
        return new ActionResult(msg, last);
    }

    /// <summary>Opens the crop editor for the first image. Must run on the UI thread (the drop handler does).</summary>
    private static Task<ActionResult> CropImage(IReadOnlyList<string> files)
    {
        string? src = files.FirstOrDefault(FileKinds.IsImage);
        if (src == null) return Task.FromResult(new ActionResult("ما في صورة", null));

        var tcs = new TaskCompletionSource<ActionResult>();
        var win = new CropWindow(src);
        win.Finished += r => tcs.TrySetResult(r);
        win.Topmost = true;
        win.Show();
        win.Activate();
        win.Topmost = false;
        return tcs.Task;
    }

    private static async Task<ActionResult> CompressPdfs(IReadOnlyList<string> files)
    {
        string? gs = PdfOps.FindGhostscript();
        if (gs == null)
            return new ActionResult("ضغط PDF يحتاج Ghostscript منزّل. أو استعمل ZIP.", null);

        string? last = null;
        int ok = 0;
        var errors = new List<string>();
        foreach (string f in files.Where(FileKinds.IsPdf))
        {
            try { last = await PdfOps.Compress(gs, f); ok++; }
            catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(f) + ": " + ex.Message); }
        }

        string msg = ok > 0 ? $"تم ضغط {ok} ملف PDF" : "ما انضغط شي";
        if (errors.Count > 0) msg += "\n" + string.Join("\n", errors.Take(3));
        return new ActionResult(msg, last);
    }

    private static async Task<ActionResult> ZipAll(IReadOnlyList<string> files)
    {
        var items = files.Where(FileKinds.Exists).ToList();
        if (items.Count == 0) return new ActionResult("ما في ملفات", null);

        string zip = await Task.Run(() => ZipOps.ZipItems(items));
        return new ActionResult("تم إنشاء " + System.IO.Path.GetFileName(zip), zip);
    }
}
