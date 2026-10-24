using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Typeless
{
    // Builds a pptx of picture slides with DocumentFormat.OpenXml, no Office needed.
    // Kept apart from PdfBackend so the WinRT render file stays small and OpenXml-free.
    public static class PptxWriter
    {
        private const long LongSide = 9144000; // 10 in
        private const long MinSide = 914400;   // 1 in, the smallest slide PowerPoint accepts

        // One slide per png, in order. Slide size follows the first image's aspect,
        // other aspects are fitted inside and centered.
        public static void WritePictureDeck(IReadOnlyList<string> pngs, string output)
        {
            if (pngs.Count == 0) throw new InvalidOperationException("The PDF has no pages.");

            var (w0, h0) = PixelSize(pngs[0]);
            long cx = w0 >= h0 ? LongSide : Math.Max(MinSide, (long)Math.Round(LongSide * (double)w0 / h0));
            long cy = w0 >= h0 ? Math.Max(MinSide, (long)Math.Round(LongSide * (double)h0 / w0)) : LongSide;

            using PresentationDocument doc = PresentationDocument.Create(output, PresentationDocumentType.Presentation);
            PresentationPart presentationPart = doc.AddPresentationPart();

            // Master, blank layout, and theme, linked both ways as PowerPoint expects.
            SlideMasterPart masterPart = presentationPart.AddNewPart<SlideMasterPart>();
            SlideLayoutPart layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
            layoutPart.AddPart(masterPart);
            ThemePart themePart = masterPart.AddNewPart<ThemePart>();
            presentationPart.AddPart(themePart);

            themePart.Theme = BuildTheme();
            layoutPart.SlideLayout = new P.SlideLayout(
                new P.CommonSlideData(EmptyShapeTree()) { Name = "Blank" },
                new P.ColorMapOverride(new A.MasterColorMapping()))
            { Type = P.SlideLayoutValues.Blank, Preserve = true };
            masterPart.SlideMaster = new P.SlideMaster(
                new P.CommonSlideData(EmptyShapeTree()),
                new P.ColorMap
                {
                    Background1 = A.ColorSchemeIndexValues.Light1,
                    Text1 = A.ColorSchemeIndexValues.Dark1,
                    Background2 = A.ColorSchemeIndexValues.Light2,
                    Text2 = A.ColorSchemeIndexValues.Dark2,
                    Accent1 = A.ColorSchemeIndexValues.Accent1,
                    Accent2 = A.ColorSchemeIndexValues.Accent2,
                    Accent3 = A.ColorSchemeIndexValues.Accent3,
                    Accent4 = A.ColorSchemeIndexValues.Accent4,
                    Accent5 = A.ColorSchemeIndexValues.Accent5,
                    Accent6 = A.ColorSchemeIndexValues.Accent6,
                    Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                    FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
                },
                new P.SlideLayoutIdList(
                    new P.SlideLayoutId { Id = 2147483649U, RelationshipId = masterPart.GetIdOfPart(layoutPart) }),
                new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));

            var slideIds = new P.SlideIdList();
            for (int i = 0; i < pngs.Count; i++)
            {
                var (w, h) = i == 0 ? (w0, h0) : PixelSize(pngs[i]);
                double scale = Math.Min((double)cx / w, (double)cy / h);
                long pw = Math.Min(cx, (long)Math.Round(w * scale));
                long ph = Math.Min(cy, (long)Math.Round(h * scale));

                SlidePart slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.AddPart(layoutPart);
                ImagePart imagePart = slidePart.AddImagePart(ImagePartType.Png);
                using (var fs = new FileStream(pngs[i], FileMode.Open, FileAccess.Read, FileShare.Read))
                    imagePart.FeedData(fs);

                P.ShapeTree tree = EmptyShapeTree();
                tree.Append(new P.Picture(
                    new P.NonVisualPictureProperties(
                        new P.NonVisualDrawingProperties { Id = 2U, Name = $"Page {i + 1}" },
                        new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.BlipFill(
                        new A.Blip { Embed = slidePart.GetIdOfPart(imagePart) },
                        new A.Stretch(new A.FillRectangle())),
                    new P.ShapeProperties(
                        new A.Transform2D(
                            new A.Offset { X = (cx - pw) / 2, Y = (cy - ph) / 2 },
                            new A.Extents { Cx = pw, Cy = ph }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })));
                slidePart.Slide = new P.Slide(
                    new P.CommonSlideData(tree),
                    new P.ColorMapOverride(new A.MasterColorMapping()));

                slideIds.Append(new P.SlideId { Id = (uint)(256 + i), RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }

            presentationPart.Presentation = new P.Presentation(
                new P.SlideMasterIdList(
                    new P.SlideMasterId { Id = 2147483648U, RelationshipId = presentationPart.GetIdOfPart(masterPart) }),
                slideIds,
                new P.SlideSize { Cx = (int)cx, Cy = (int)cy, Type = P.SlideSizeValues.Custom },
                new P.NotesSize { Cx = 6858000, Cy = 9144000 },
                new P.DefaultTextStyle())
            { SaveSubsetFonts = true };
        }

        private static (int W, int H) PixelSize(string png)
        {
            using var stream = new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            return (Math.Max(1, frame.PixelWidth), Math.Max(1, frame.PixelHeight));
        }

        // Group root every cSld needs: id 1, empty transform.
        private static P.ShapeTree EmptyShapeTree() => new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup(
                new A.Offset { X = 0, Y = 0 },
                new A.Extents { Cx = 0, Cy = 0 },
                new A.ChildOffset { X = 0, Y = 0 },
                new A.ChildExtents { Cx = 0, Cy = 0 })));

        // Minimal complete Office theme: 12 colors, latin fonts, 3 of each style.
        private static A.Theme BuildTheme()
        {
            static A.SolidFill Ph() => new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor });
            static A.Outline Line(int width) => new A.Outline(Ph(), new A.PresetDash { Val = A.PresetLineDashValues.Solid })
            {
                Width = width,
                CapType = A.LineCapValues.Flat,
                CompoundLineType = A.CompoundLineValues.Single,
                Alignment = A.PenAlignmentValues.Center,
            };
            static A.EffectStyle Effect() => new A.EffectStyle(new A.EffectList());

            return new A.Theme(
                new A.ThemeElements(
                    new A.ColorScheme(
                        new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                        new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                        new A.Dark2Color(new A.RgbColorModelHex { Val = "44546A" }),
                        new A.Light2Color(new A.RgbColorModelHex { Val = "E7E6E6" }),
                        new A.Accent1Color(new A.RgbColorModelHex { Val = "4472C4" }),
                        new A.Accent2Color(new A.RgbColorModelHex { Val = "ED7D31" }),
                        new A.Accent3Color(new A.RgbColorModelHex { Val = "A5A5A5" }),
                        new A.Accent4Color(new A.RgbColorModelHex { Val = "FFC000" }),
                        new A.Accent5Color(new A.RgbColorModelHex { Val = "5B9BD5" }),
                        new A.Accent6Color(new A.RgbColorModelHex { Val = "70AD47" }),
                        new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }),
                        new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" }))
                    { Name = "Office" },
                    new A.FontScheme(
                        new A.MajorFont(
                            new A.LatinFont { Typeface = "Calibri Light" },
                            new A.EastAsianFont { Typeface = "" },
                            new A.ComplexScriptFont { Typeface = "" }),
                        new A.MinorFont(
                            new A.LatinFont { Typeface = "Calibri" },
                            new A.EastAsianFont { Typeface = "" },
                            new A.ComplexScriptFont { Typeface = "" }))
                    { Name = "Office" },
                    new A.FormatScheme(
                        new A.FillStyleList(Ph(), Ph(), Ph()),
                        new A.LineStyleList(Line(6350), Line(12700), Line(19050)),
                        new A.EffectStyleList(Effect(), Effect(), Effect()),
                        new A.BackgroundFillStyleList(Ph(), Ph(), Ph()))
                    { Name = "Office" }),
                new A.ObjectDefaults(),
                new A.ExtraColorSchemeList())
            { Name = "Office Theme" };
        }
    }
}
