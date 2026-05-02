using OCR_Services;
using System.Diagnostics;

namespace Ocr_2_PDF_Tests_Large
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            // Schedules the lambda on a thread pool thread.Bypasses the current SynchronizationContext.
            Task.Run(async () => await TestMe());
            // Captures and uses the current SynchronizationContext for continuations after await
            //_ = TestMe(); // Fire and forget
        }

        private async Task TestMe()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            stopwatch.Start();

            string inputRoot = @"D:\OOOTEST\OcrInput";
            string outputRoot = @"D:\OOOTEST\OcrOutput";

            using var ocrService = new PpOcrV5Service();
            var pdfBuilder = new SearchablePdfBuilder();
            var pipeline = new SafeOcrPipeline(ocrService, pdfBuilder);
            var processor = new BatchDirectoryProcessor();

            using var cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg =>
               Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"));

            // // Lazy streaming
            //[Start] 
            //   ↓
            //[Initialize: state = 0, currentBatch = empty]
            //   ↓
            //[Get first directory from GetDirectoriesWithImages()]
            //   ↓
            //[Add to batch: 1 / 10]
            //   ↓ [Continue until 10 / 10]
            //[YIELD return batch] ────────→ [Main code processes batch]
            //   ↓                              ↓
            //[FREEZE HERE] [Batch processing complete]
            //   ↓                              ↓
            //[RESUME with new empty batch] ←── [MoveNext() called]
            //   ↓
            //[Continue from next directory]
            //   ↓
            //[YIELD next batch] ───────────→ [Main code processes next batch]
            //   ↓
            //[Continue until no more directories]
            //   ↓
            //[YIELD final partial batch]
            //   ↓
            //[End -MoveNext() returns false]
            //foreach (var batch in processor.GetBatches(inputRoot, outputRoot, 10))
            //{
            //    await pipeline.ProcessGroupsAsync(batch, progress, cts.Token);
            //    GC.Collect(); // Force garbage collection if needed
            //}

            foreach (var batch in processor.GetBatches(inputRoot, outputRoot, 10))
            {
                await pipeline.ProcessGroupsAsync(batch, progress, cts.Token);
                //GC.Collect(); // Force garbage collection if needed
            }

            stopwatch.Stop();
            label1.Invoke((MethodInvoker)(() => label1.Text = $"Total Time: {stopwatch.Elapsed}"));
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            label1.Text = "";
        }
    }
}
