using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TangerineWin;

public sealed record ActionResult(string Message, string? Path);

public sealed class ActionDef
{
    public string Icon { get; }
    public string Label { get; }
    public Func<string, bool> Supports { get; }
    public Func<IReadOnlyList<string>, Task<ActionResult>> Run { get; }

    public ActionDef(string icon, string label, Func<string, bool> supports,
                     Func<IReadOnlyList<string>, Task<ActionResult>> run)
    {
        Icon = icon;
        Label = label;
        Supports = supports;
        Run = run;
    }
}

public static class ActionCatalog
{
    /// <summary>Wedges, clockwise starting from the top.</summary>
    public static IReadOnlyList<ActionDef> All { get; } = new List<ActionDef>
    {
        new("🖼️", "إلى PNG",
            p => FileKinds.IsImage(p) && !FileKinds.IsPng(p),
            files => PerFile(files, p => FileKinds.IsImage(p) && !FileKinds.IsPng(p), p => Task.FromResult(ImageOps.ToPng(p)))),

        new("📷", "إلى JPG",
            p => FileKinds.IsImage(p) && !FileKinds.IsJpeg(p),
            files => PerFile(files, p => FileKinds.IsImage(p) && !FileKinds.IsJpeg(p), p => Task.FromResult(ImageOps.ToJpg(p)))),

        new("📉", "تصغير الصورة",
            FileKinds.IsImage,
            files => PerFile(files, FileKinds.IsImage, p => Task.FromResult(ImageOps.Shrink(p)))),

        new("📄", "صورة إلى PDF",
            FileKinds.IsImage,
            files => PerFile(files, FileKinds.IsImage, p => Task.FromResult(ImageOps.ToPdf(p)))),

        new("📊", "Word / Excel إلى PDF",
            FileKinds.IsOffice,
            files => PerFile(files, FileKinds.IsOffice, OfficeOps.ToPdf, serial: true)),

        new("📝", "PDF إلى Word",
            FileKinds.IsPdf,
            files => PerFile(files, FileKinds.IsPdf, OfficeOps.PdfToWord, serial: true)),

        new("🖼", "PDF إلى صور",
            FileKinds.IsPdf,
            files => PerFile(files, FileKinds.IsPdf, PdfOps.ToImages)),

        new("📦", "ضغط",
            FileKinds.Exists,
            Compress),
    };

    private static async Task<ActionResult> PerFile(
        IReadOnlyList<string> files,
        Func<string, bool> accepts,
        Func<string, Task<string>> work,
        bool serial = false)
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

    /// <summary>PDFs are shrunk with Ghostscript when installed; everything else is zipped.</summary>
    private static async Task<ActionResult> Compress(IReadOnlyList<string> files)
    {
        var items = files.Where(FileKinds.Exists).ToList();
        if (items.Count == 0) return new ActionResult("ما في ملفات", null);

        if (items.All(FileKinds.IsPdf))
        {
            string? gs = PdfOps.FindGhostscript();
            if (gs != null)
            {
                string? last = null;
                int ok = 0;
                var errors = new List<string>();
                foreach (string f in items)
                {
                    try { last = await PdfOps.Compress(gs, f); ok++; }
                    catch (Exception ex) { errors.Add(System.IO.Path.GetFileName(f) + ": " + ex.Message); }
                }
                string msg = ok > 0 ? $"تم ضغط {ok} ملف PDF" : "ما انضغط شي";
                if (errors.Count > 0) msg += "\n" + string.Join("\n", errors.Take(3));
                return new ActionResult(msg, last);
            }
            // no Ghostscript: fall through to zip
        }

        string zip = await Task.Run(() => ZipOps.ZipItems(items));
        return new ActionResult("تم إنشاء " + System.IO.Path.GetFileName(zip), zip);
    }
}
