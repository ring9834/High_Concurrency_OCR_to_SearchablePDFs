using log4net;
using OCR_Data_Models;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using System;

namespace OCR_Services
{
    namespace OCR_Services
    {
        public sealed class PpOcrV5ServiceThreadLocal : IDisposable
        {
            private static readonly ILog _logger = LogManager.GetLogger(typeof(PpOcrV5ServiceThreadLocal));

            // Each thread gets its own dedicated PaddleOCR engine instance.
            // The engine is created lazily on first use per thread, and disposed when the ThreadLocal itself is disposed (via trackAllValues: true).
            private readonly ThreadLocal<PaddleOcrAll> _threadEngine;

            private bool _disposed = false;
            private volatile bool _warmupCompleted = false;

            // Logging counters
            private long _createdCount = 0;
            private long _reusedCount = 0;

            public PpOcrV5ServiceThreadLocal()
            {
                _threadEngine = new ThreadLocal<PaddleOcrAll>(
                    valueFactory: CreateEngine,
                    trackAllValues: true   // Required for proper disposal of all per-thread instances
                );
            }

            private PaddleOcrAll CreateEngine()
            {
                Interlocked.Increment(ref _createdCount);
                long created = Interlocked.Read(ref _createdCount);
                long reused = Interlocked.Read(ref _reusedCount);
                _logger.Info($"[OCR] New PaddleOCR engine created for thread {Environment.CurrentManagedThreadId}. " +
                             $"Total Created: {created} | Reused: {reused}");

                return new PaddleOcrAll(
                    LocalFullModels.ChineseV5,
                    PaddleDevice.Mkldnn())
                {
                    AllowRotateDetection = true,
                    Enable180Classification = true
                };
            }

            /// <summary>
            /// Call this method after service creation to warm up engines on a
            /// fixed set of threads so they are ready before real requests arrive.
            /// </summary>
            public async Task WarmupAsync(int warmupThreadCount = 4)
            {
                if (_warmupCompleted)
                {
                    _logger.Debug("Warmup already completed.");
                    return;
                }

                await WarmUpEngines(warmupThreadCount);
                _warmupCompleted = true;
            }

            private async Task WarmUpEngines(int threadCount)
            {
                _logger.Info($"Starting warmup across {threadCount} dedicated threads...");

                // Spin up one Task per desired thread so each gets its own ThreadLocal engine initialised and exercised before real traffic.
                var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(() =>
                {
                    try
                    {
                        // Accessing Value triggers CreateEngine() for this thread
                        var engine = _threadEngine.Value!;

                        using var dummyMat = CreateDummyMat();

                        for (int run = 0; run < 3; run++)
                            engine.Run(dummyMat);

                        _logger.Debug($"Warmed up engine on thread {Environment.CurrentManagedThreadId} (slot {i + 1}/{threadCount})");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Warmup failed on thread {Environment.CurrentManagedThreadId} (slot {i}): {ex.Message}");
                    }
                }));

                await Task.WhenAll(tasks);
                _logger.Info($"PaddleOCR warmup completed. Engines created so far: {_createdCount}");
            }

            public (long Created, long Reused) GetPoolStats() =>
                (Interlocked.Read(ref _createdCount), Interlocked.Read(ref _reusedCount));

            private static Mat CreateDummyMat() =>
                new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));

            /// <summary>
            /// Here we use synchronous processing because we are Channel consumers in which 
            /// Task.Run using together with async/await will cause critical problems.
            /// Because await inside the consumer lambda (especially ReadAllAsync or inside OcrImageAsync) can cause the continuation to run on a different thread-pool thread.
            /// This means: we may get engine mismatch; we may create more engines than _maxConcurrency.
            /// So, we don't use await foreach here, we use while and TryRead in a loop to ensure we stay on the same thread.
            /// </summary>
            /// <param name="imagePath"></param>
            /// <param name="pageIndex"></param>
            /// <returns></returns>
            /// <exception cref="InvalidOperationException"></exception>
            public OcrPageResult OcrImage(string imagePath, int pageIndex)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (!_warmupCompleted)
                    _logger.Warn("Warmup not completed yet. Consider calling WarmupAsync() after service creation.");

                // Accessing Value either returns the already-created engine for ***this thread*** (reuse) or calls CreateEngine() once (first access on thread).
                // This Thread here means the thread that is executing this method, which may be a thread pool thread or a dedicated thread depending on how the caller invokes it.
                bool isFirstAccess = !_threadEngine.IsValueCreated;
                var engine = _threadEngine.Value!;

                // Logging: Created vs Reused
                if (!isFirstAccess)
                {
                    // Engine already existed for this thread — count as reuse
                    long reused = Interlocked.Increment(ref _reusedCount);
                    long created = Interlocked.Read(ref _createdCount);

                    if (reused <= 20 || reused % 10 == 0)
                        _logger.Debug($"[OCR] Engine reused on thread {Environment.CurrentManagedThreadId}. " +
                                      $"Total Created: {created} | Reused: {reused}");
                }
                // A brand-new creation is already logged inside CreateEngine()

                try
                {
                    using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
                    if (mat.Empty())
                        throw new FileNotFoundException("Failed to load image", imagePath);

                    var result = engine.Run(mat);

                    var blocks = result.Regions
                        .Where(r => !string.IsNullOrWhiteSpace(r.Text))
                        .Select(r => new OcrTextBlock(
                            r.Text,
                            r.Score,
                            r.Rect.Points().Select(p => new System.Drawing.PointF(p.X, p.Y)).ToArray()))
                        .ToList();

                    return new OcrPageResult(pageIndex, imagePath, blocks);
                }
                catch (Exception ex)
                {
                    _logger.Error($"OCR failed for page {pageIndex}: {imagePath}", ex);
                    throw new InvalidOperationException($"OCR failed for page {pageIndex}: {imagePath}", ex);
                }
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing)
            {
                if (_disposed) return;
                _disposed = true;

                long created = Interlocked.Read(ref _createdCount);
                long reused = Interlocked.Read(ref _reusedCount);
                _logger.Info($"Disposing PpOcrV5Service. Final stats — Created: {created}, Reused: {reused}");

                if (disposing)
                {
                    // Values gives us every per-thread instance (requires trackAllValues: true)
                    if (_threadEngine.Values != null)
                    {
                        foreach (var engine in _threadEngine.Values)
                        {
                            try { engine?.Dispose(); }
                            catch (Exception ex) { _logger.Warn($"Error disposing engine: {ex.Message}"); }
                        }
                    }

                    _threadEngine.Dispose();
                    _logger.Debug("All per-thread PaddleOCR engines disposed.");
                }
            }
        }
    }
}