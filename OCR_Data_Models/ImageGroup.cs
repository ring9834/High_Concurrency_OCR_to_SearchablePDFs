namespace OCR_Data_Models
{
    /// <summary>
    /// One group of images → one output PDF
    /// </summary>
    public record ImageGroup(
        string GroupId,
        IReadOnlyList<string> ImagePaths,   // ordered: page 1, 2, 3...
        string OutputPdfPath
    );
}
