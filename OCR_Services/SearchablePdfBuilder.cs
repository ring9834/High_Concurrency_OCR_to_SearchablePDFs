using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using OCR_Data_Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace OCR_Services
{
    public sealed class SearchablePdfBuilder
    {
        public async Task BuildAsync(
            ImageGroup group,
            IReadOnlyList<OcrPageResult> pages,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(group.OutputPdfPath)!);

            await Task.Run(() =>
            {
                using var writer = new PdfWriter(group.OutputPdfPath);
                using var pdf = new PdfDocument(writer);

                foreach (var page in pages.OrderBy(p => p.PageIndex))
                {
                    ct.ThrowIfCancellationRequested();
                    AddPageToPdf(pdf, page);
                }
            }, ct);
        }

        private static void AddPageToPdf(PdfDocument pdf, OcrPageResult pageResult)
        {
            // Load the original image
            var imgData = ImageDataFactory.Create(pageResult.ImagePath);
            float imgW = imgData.GetWidth();
            float imgH = imgData.GetHeight();

            // Create PDF page matching image dimensions (in points; 1pt = 1px at 72dpi)
            var pdfPage = pdf.AddNewPage(new iText.Kernel.Geom.PageSize(imgW, imgH));
            var canvas = new PdfCanvas(pdfPage);

            // Draw the image as background
            canvas.AddImageFittedIntoRectangle(
                imgData,
                new iText.Kernel.Geom.Rectangle(0, 0, imgW, imgH),
                false);

            // Overlay invisible text for searchability
            // var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var font = LoadChineseFont();  // Use a CJK font for Chinese text
            canvas.BeginText();
            canvas.SetFontAndSize(font, 12);       // tiny font
            canvas.SetTextRenderingMode(3);       // 3 = invisible

            foreach (var block in pageResult.Blocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text) || block.BoundingBox?.Length < 2) continue;

                //float x = block.BoundingBox[0].X;
                //float yTop = block.BoundingBox[0].Y;           // assuming top-left origin
                //float boxHeight = block.BoundingBox[2].Y - yTop;

                //float pdfY = imgH - yTop;                      // Convert to PDF bottom-left

                //// Scale font roughly to box height (be careful with very small boxes)
                //float fontSize = Math.Max(6f, boxHeight * 0.95f);

                //canvas.SetFontAndSize(font, fontSize);

                //// Position text: move to bottom-left of the box
                //canvas.SetTextMatrix(1, 0, 0, 1, x, pdfY - boxHeight);

                //canvas.ShowText(block.Text);

                // Assuming BoundingBox[0] = top-left, BoundingBox[2] = bottom-right (common in PaddleOCR)
                float right = block.BoundingBox[0].X;
                float bottom = block.BoundingBox[0].Y;
                float left = block.BoundingBox[2].X;
                float top = block.BoundingBox[2].Y;

                float boxWidth = right - left;
                float boxHeight = bottom - top;

                float centerX = left + boxWidth / 2f;
                float centerY = imgH - (top + boxHeight / 2f);   // convert to PDF space

                float fontSize = Math.Max(8f, boxHeight * 1.08f);

                canvas.SetFontAndSize(font, fontSize);

                // For invisible text, approximate centering is usually good enough
                // Move to approximate left side, but slightly adjusted
                float startX = centerX - (boxWidth * 0.50f);   // rough adjustment

                canvas.SetTextMatrix(1, 0, 0, 1, startX, centerY - (fontSize * 0.35f));
                canvas.ShowText(block.Text);
            }

            canvas.EndText();
            canvas.Release(); // Optional
        }

        private static PdfFont LoadChineseFont()
        {
            // Relative to the executable — works in any OS
            var bundledPath = Path.Combine(
                AppContext.BaseDirectory, "fonts", "NotoSansCJKsc-Regular.otf");

            if (!File.Exists(bundledPath))
                throw new FileNotFoundException(
                    $"Bundled CJK font not found at: {bundledPath}");

            return PdfFontFactory.CreateFont(
                bundledPath,
                PdfEncodings.IDENTITY_H,
                PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
        }
    }
}
