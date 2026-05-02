using OCR_Services;
using OCR_Services.OCR_Services;
using System.Diagnostics;

namespace Ocr_2_PDF_Tests_Large_Channel_ThreadLocal
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Task.Run(async () => await TestMe());
        }

        private async Task TestMe()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            string inputRoot = @"D:\OOOTEST\OcrInput";
            string outputRoot = @"D:\OOOTEST\OcrOutput";

            using var ocrService = new PpOcrV5ServiceThreadLocal();
            var pdfBuilder = new SearchablePdfBuilder();
            var pipeline = new SafeOcrPipelineThreadLocal(ocrService, pdfBuilder);
            var processor = new BatchDirectoryProcessor();

            using var cts = new CancellationTokenSource();
            var progress = new Progress<string>(msg =>
               Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"));

            foreach (var batch in processor.GetBatches(inputRoot, outputRoot, 10))
            {
                await pipeline.ProcessGroupsAsync(batch, progress, cts.Token);
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
