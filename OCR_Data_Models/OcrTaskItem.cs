using System;
using System.Collections.Generic;
using System.Text;

namespace OCR_Data_Models
{
    public record OcrTaskItem(ImageGroup Group, string ImagePath, int PageIndex);
}
