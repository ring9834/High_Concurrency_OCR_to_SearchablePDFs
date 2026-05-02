using OCR_Data_Models;
using OCR_Services.OCR_Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Channels;

namespace OCR_Services
{
    public sealed class SafeOcrPipelineThreadLocal
    {
        private readonly PpOcrV5ServiceThreadLocal _ocr;
        private readonly SearchablePdfBuilder _pdfBuilder;

        // Must match (or be <= to) the number of threads the .NET thread-pool
        // will actually assign to the consumer Tasks. With ThreadLocal<T>, each
        // unique thread gets its own PaddleOCR engine, so concurrency ==
        // engine count. Keep this number deliberate — more consumers means more
        // engines in memory simultaneously.
        private readonly int _maxConcurrency = 6;
        private readonly int _queueCapacity = 30;

        public SafeOcrPipelineThreadLocal(PpOcrV5ServiceThreadLocal ocr, SearchablePdfBuilder pdfBuilder)
        {
            _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
            _pdfBuilder = pdfBuilder ?? throw new ArgumentNullException(nameof(pdfBuilder));
        }

        public async Task ProcessGroupsAsync(
            IEnumerable<ImageGroup> groups,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            // WarmupAsync() pre-creates one PaddleOCR engine per thread-pool thread on _maxConcurrency dedicated threads, so the first real
            // OCR call on each consumer thread hits an already-warm engine rather than paying the cold-start cost mid-pipeline.
            await _ocr.WarmupAsync(warmupThreadCount: _maxConcurrency);

            var channel = Channel.CreateBounded<OcrTaskItem>(
                new BoundedChannelOptions(_queueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,

                    // A single dedicated writer thread is sufficient; this avoids
                    // unnecessary synchronisation overhead on the producer side.
                    SingleWriter = true,

                    // Multiple consumer tasks will read concurrently.
                    SingleReader = false
                });

            var results = new ConcurrentDictionary<string, ConcurrentBag<OcrPageResult>>();

            // PRODUCER
            // Runs on one thread-pool thread. Does NOT access _ocr, so it never
            // triggers ThreadLocal engine creation — good.
            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var group in groups)
                    {
                        ct.ThrowIfCancellationRequested();

                        progress?.Report(
                            $"[QUEUE] Group '{group.GroupId}' ({group.ImagePaths.Count} pages)");

                        for (int i = 0; i < group.ImagePaths.Count; i++)
                        {
                            await channel.Writer.WriteAsync(
                                new OcrTaskItem(group, group.ImagePaths[i], i), ct);
                        }
                    }
                }
                finally
                {
                    // Always complete the writer so consumers can drain and exit,
                    // even if the producer throws or is cancelled.
                    channel.Writer.Complete();
                }
            }, ct);

            // CONSUMERS
            // Each Task.Run() is scheduled onto a thread-pool thread.
            // ***ThreadLocal<T> ensures*** each thread owns exactly one PaddleOCR engine — no sharing, no locking, no pool gymnastics.
            //
            // IMPORTANT: avoid using async lambdas that yield across threads (like, ConfigureAwait(false) on hot paths) — an await inside the
            // consumer *can* resume on a different thread-pool thread, which would access a different ThreadLocal engine. OcrImageAsync()
            // handles this correctly by pinning the engine reference before any await, so the engine retrieved is always the one that belongs to
            // the thread that started the call.
            var consumers = Enumerable.Range(0, _maxConcurrency)
                .Select(workerId => Task.Run(() =>
                {
                    var reader = channel.Reader;
                    // Because await inside the consumer lambda (especially ReadAllAsync or inside OcrImageAsync) can cause the continuation to run on a different thread-pool thread.
                    // This means: we may get engine mismatch; we may create more engines than _maxConcurrency.
                    // So, we don't use await foreach here, we use while and TryRead in a loop to ensure we stay on the same thread.
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Wait for next item (this is the only blocking point)
                        if (!channel.Reader.TryRead(out var item))
                        {
                            // Check if channel is completed
                            if (channel.Reader.Completion.IsCompleted)
                                break;

                            // Small backoff to avoid tight CPU loop
                            Thread.Sleep(5);
                            continue;
                        }

                        try
                        {
                            // OcrImageAsync() internally does:
                            //   var engine = _threadEngine.Value   - pinned here
                            //   await Task.Run(() => engine.Run(…)) - safe, engine is captured
                            var result = _ocr.OcrImage(item.ImagePath, item.PageIndex);

                            results
                                .GetOrAdd(item.Group.GroupId, _ => new ConcurrentBag<OcrPageResult>())
                                .Add(result);
                        }
                        catch (OperationCanceledException)
                        {
                            throw; // Bubble up so Task.WhenAll sees the cancellation
                        }
                        catch (Exception ex)
                        {
                            progress?.Report(
                                $"[ERROR] Worker {workerId} — OCR failed: {item.ImagePath} → {ex.Message}");
                        }
                    }
                }, ct))
                .ToArray();

            // Wait for the producer and all consumers together. If any task faults, WhenAll surfaces the first exception.
            await Task.WhenAll(consumers.Prepend(producer));

            // Log final engine stats now that all OCR work is done.
            var (created, reused) = _ocr.GetPoolStats();
            progress?.Report($"[STATS] Engines created: {created} | Reused: {reused}");

            // PDF BUILD STAGE
            // CPU/IO work, not OCR — no ThreadLocal involvement here.
            var buildTasks = groups.Select(async group =>
            {
                try
                {
                    if (!results.TryGetValue(group.GroupId, out var bag))
                    {
                        progress?.Report($"[WARN] No OCR results for group '{group.GroupId}'");
                        return;
                    }

                    var ordered = bag.OrderBy(p => p.PageIndex).ToArray();
                    await _pdfBuilder.BuildAsync(group, ordered, ct);
                    progress?.Report($"[DONE] '{group.GroupId}' → {group.OutputPdfPath}");
                }
                catch (Exception ex)
                {
                    progress?.Report(
                        $"[ERROR] PDF build failed for '{group.GroupId}': {ex.Message}");
                }
            });

            await Task.WhenAll(buildTasks);
        }
    }
}