# 🍊 TangerineWin

أداة لويندوز مستوحاة من Tangerine لماك: امسك **Shift** واسحب أي ملف في مستكشف الملفات أو سطح المكتب، فبيطلع حوالين المؤشر **menu دائري**. أفلت الملف على الخيار اللي بدك إياه وبيتنفّذ فوراً، والملف الجديد بينحفظ جنب الأصلي.

> ملاحظة: هاد مشروع جديد مكتوب من الصفر لويندوز، مش نسخة رسمية من Tangerine. ما قدرت أفتح موقع tangerineformac.com أثناء الكتابة، فالميزات مبنية على وصف الأداة من مراجعات منشورة.

## الميزات (الإصدار الأول)

| الخيار | يشتغل على | الناتج |
|---|---|---|
| 🖼️ إلى PNG | صور (JPG, BMP, GIF, TIFF, WebP*, HEIC*) | `اسم.png` |
| 📷 إلى JPG | صور | `اسم.jpg` |
| 📉 تصغير الصورة | صور | `اسم_small.jpg/png` (أقصى طول 1920px + ضغط) |
| 📄 صورة إلى PDF | صور | `اسم.pdf` |
| 📊 Word / Excel إلى PDF | doc/docx, xls/xlsx/csv, ppt/pptx | `اسم.pdf` |
| 📝 PDF إلى Word | pdf | `اسم.docx` |
| 🖼 PDF إلى صور | pdf | مجلد `اسم_pages` فيه صفحة PNG لكل صفحة |
| 📦 ضغط | أي ملف أو مجلد | `اسم.zip` (أو PDF أصغر لو Ghostscript منزّل) |

\* WebP وHEIC بدهم ملحقات الترميز من Microsoft Store (WebP Image Extensions / HEIF Image Extensions).

## الاستخدام

1. شغّل `TangerineWin.exe`. بيظهر أيقونة 🍊 في منطقة الإشعارات (بجنب الساعة).
2. في Explorer أو على سطح المكتب: **امسك Shift، واضغط على ملف، وابدأ اسحب**.
3. بيطلع الـ menu الدائري. جيب الملف فوق الخيار وأفلته.
4. بيطلع إشعار لما يخلص. اضغط عليه لفتح الملف الجديد بمكانه.

بالضغط يمين على أيقونة الـ tray: تفعيل/إيقاف، التشغيل مع ويندوز، خروج.

الخيارات اللي ما بتناسب نوع الملف بتصير باهتة.

## الحصول على البرنامج جاهز (بدون ما تثبّت شي)

المشروع فيه GitHub Actions بيبني ملف `TangerineWin.exe` لحاله على سيرفرات ويندوز.

1. أنشئ repository جديد على GitHub وارفع محتويات هاد المجلد عليه (خلّي `TangerineWin.csproj` و`.github` بجذر الـ repo).
2. افتح تبويب **Actions**. البناء بيشتغل تلقائياً مع كل push. أو اختار **Build Windows app** ثم **Run workflow**.
3. بعد ما يخلص (دقيقتين تقريباً) افتح التشغيلة ونزّل **TangerineWin-win-x64** من قسم Artifacts. جواها `TangerineWin.exe`، وما بيحتاج تثبيت .NET.

**لعمل Release رسمي بنفس الرابط الثابت:**

```bash
git tag v1.0.0
git push origin v1.0.0
```

بيظهر الملف في صفحة **Releases** للـ repo.

> هاد الكود انكتب بدون ما أقدر أبنيه أو أجرّبه على ويندوز. لو البناء الأول فشل، افتح تفاصيل الخطوة الحمرا في Actions وابعتلي رسالة الخطأ وبصلحها.

## البناء على جهازك (اختياري)

بدك Windows 10/11 و[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

الناتج: `publish/TangerineWin.exe`.

## المتطلبات الاختيارية لبعض الميزات

- **Word / Excel / PowerPoint إلى PDF، وPDF إلى Word:** Microsoft Office منزّل، أو [LibreOffice](https://www.libreoffice.org/) كبديل مجاني.
- **ضغط PDF حقيقي:** [Ghostscript](https://ghostscript.com/releases/gsdnld.html). بدونه خيار "ضغط" بيعمل ملف ZIP.
- **PDF إلى صور:** ما بيحتاج شي، بيستخدم مفسّر PDF المدمج في ويندوز.

## كيف بيشتغل

- `DragHook.cs`: mouse hook عام (WH_MOUSE_LL) بيلقط Shift + سحب فوق Explorer أو سطح المكتب.
- `Program.cs`: بيتأكد عبر UI Automation إنك بتسحب ملف فعلاً، وبيعرض الأيقونة في الـ tray.
- `RadialWindow.cs`: نافذة شفافة بدون تفعيل، كل قطاع فيها هدف إفلات (drop target). بترجّع دائماً Copy حتى Explorer ما يحذف الأصل (Shift + سحب عادةً يعني نقل).
- `Actions.cs`: تعريف الخيارات. لإضافة خيار جديد أضف `ActionDef` في القائمة.
- `Converters.cs`: التحويلات (WPF imaging، PDFsharp، Office COM، LibreOffice، Ghostscript، ZIP).

## قيود معروفة

- ما بيشتغل فوق نوافذ Explorer المرفوعة بصلاحيات Administrator.
- الـ menu عدد خياراته ثابت (8). زيادة الخيارات بتحتاج تعديل بسيط في `Actions.cs`.
- أول إصدار: ما في إعدادات للجودة أو الحجم، القيم ثابتة في `Converters.cs`.
