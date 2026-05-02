using System;
using System.Collections.Generic;
using System.Text;

namespace OCR_Data_Models
{
    /// <summary>OCR result for a single image</summary>
    public record OcrPageResult(
        int PageIndex,
        string ImagePath,
        IReadOnlyList<OcrTextBlock> Blocks
    );
}
