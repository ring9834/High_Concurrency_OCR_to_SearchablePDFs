using System;
using System.Collections.Generic;
using System.Text;

namespace OCR_Data_Models
{
    public record OcrTextBlock(
     string Text,
     float Confidence,
     // Bounding box corners in image pixels (x,y), clockwise from top-left
     System.Drawing.PointF[] BoundingBox
 );
}
