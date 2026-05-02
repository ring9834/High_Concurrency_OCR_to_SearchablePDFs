using OCR_Data_Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Channels;

namespace OCR_Services
{
    public sealed class SafeOcrPipeline
    {
        private readonly PpOcrV5Service _ocr;
        private readonly SearchablePdfBuilder _pdfBuilder;

        // Tune these carefully
        private readonly int _maxConcurrency = 4;
        private readonly int _queueCapacity = 30;

        public SafeOcrPipeline(PpOcrV5Service ocr, SearchablePdfBuilder pdfBuilder)
        {
            _ocr = ocr;
            _pdfBuilder = pdfBuilder;
        }

        public async Task ProcessGroupsAsync(
            IEnumerable<ImageGroup> groups,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            // Ensure Paddle is fully initialized BEFORE concurrency
            await _ocr.WarmupAsync();

            var channel = Channel.CreateBounded<OcrTaskItem>(
                new BoundedChannelOptions(_queueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait
                });

            var results = new ConcurrentDictionary<string, ConcurrentBag<OcrPageResult>>();

            // PRODUCER
            var producer = Task.Run(async () =>
            {
                foreach (var group in groups)
                {
                    progress?.Report($"[QUEUE] Group '{group.GroupId}' ({group.ImagePaths.Count} pages)");

                    for (int i = 0; i < group.ImagePaths.Count; i++)
                    {
                        await channel.Writer.WriteAsync(
                            new OcrTaskItem(group, group.ImagePaths[i], i),
                            ct);
                    }
                }

                channel.Writer.Complete();
            }, ct);

            // CONSUMERS (bounded concurrency)
            var consumers = Enumerable.Range(0, _maxConcurrency)
                .Select(workerId => Task.Run(async () =>
                {
                    await foreach (var item in channel.Reader.ReadAllAsync(ct))
                    {
                        try
                        {
                            var result = await _ocr.OcrImageAsync(item.ImagePath, item.PageIndex);

                            var bag = results.GetOrAdd(item.Group.GroupId,
                                _ => new ConcurrentBag<OcrPageResult>());

                            bag.Add(result);
                        }
                        catch (Exception ex)
                        {
                            progress?.Report(
                                $"[ERROR] OCR failed: {item.ImagePath} → {ex.Message}");
                        }
                    }
                }, ct))
                .ToArray();

            await Task.WhenAll(consumers.Prepend(producer));

            // BUILD PDFs (safe stage)
            var buildTasks = groups.Select(async group =>
            {
                try
                {
                    if (!results.TryGetValue(group.GroupId, out var bag))
                    {
                        progress?.Report($"[WARN] No results for group '{group.GroupId}'");
                        return;
                    }

                    var ordered = bag.OrderBy(p => p.PageIndex).ToArray();

                    await _pdfBuilder.BuildAsync(group, ordered, ct);

                    progress?.Report($"[DONE] Group '{group.GroupId}' → {group.OutputPdfPath}");
                }
                catch (Exception ex)
                {
                    progress?.Report($"[ERROR] PDF build failed for '{group.GroupId}': {ex.Message}");
                }
            });

            await Task.WhenAll(buildTasks);
        }
    }
}
