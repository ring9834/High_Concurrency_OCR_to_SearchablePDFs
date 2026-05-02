using log4net;
using Microsoft.Extensions.ObjectPool;
using OCR_Data_Models;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using System;

namespace OCR_Services
{
    public sealed class PpOcrV5Service : IDisposable
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(PpOcrV5Service));
        private readonly ObjectPool<PooledPaddleOcr> _pool;
        // Can get this value from configuration or environment variable if needed
        private int PoolSize = 4;
        private bool _disposed = false;
        private volatile bool _warmupCompleted = false;

        // Logging counters
        private long _createdCount = 0;
        private long _reusedCount = 0;

        public PpOcrV5Service()
        {
            var provider = new DefaultObjectPoolProvider
            {
                // Controls how many PaddleOCR engines the pool is allowed to keep in memory when they are not being used.
                // When we all _pool.Get(), the pool gives you an engine.
                // When we call _pool.Return(engine), the engine goes back to the pool and becomes idle(sitting in the pool).
                // The pool will keep up to MaximumRetained engines in this idle state.
                // If more engines are returned than MaximumRetained, the extra ones will be discarded (disposed).
                MaximumRetained = PoolSize
            };

            _pool = provider.Create(new OcrEnginePolicy());
        }

        /// <summary>
        /// Call this method after service creation to warm up the pool asynchronously
        /// </summary>
        public async Task WarmupAsync()
        {
            if (_warmupCompleted)
            {
                _logger.Debug("Warmup already completed");
                return;
            }

            await WarmUpPool();
            _warmupCompleted = true;
        }

        private async Task WarmUpPool()
        {
            _logger.Info($"Starting warmup of {PoolSize} PaddleOCR engines...");

            for (int i = 0; i < PoolSize; i++)
            {
                var pooled = _pool.Get();

                try
                {
                    // Create dummy image for each iteration to avoid sharing
                    using var dummyMat = CreateDummyMat();

                    // Actual inference to warm up models and caches
                    var result = pooled.Engine.Run(dummyMat);

                    // Run a couple of times for better warmup
                    for (int run = 0; run < 2; run++)
                    {
                        result = pooled.Engine.Run(dummyMat);
                    }
                    _logger.Debug($"Warmed up engine {i + 1}/{PoolSize}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Warmup failed for engine {i}: {ex.Message}");
                }
                finally
                {
                    _pool.Return(pooled);
                }

                await Task.Delay(30);   // Small breathing room
            }
            _logger.Info($"PaddleOCR pool warmup completed. Engines created so far: {_createdCount}");
        }

        // Helper to get stats (we can call this from outside)
        public (long Created, long Reused) GetPoolStats()
        {
            return (Interlocked.Read(ref _createdCount), Interlocked.Read(ref _reusedCount));
        }

        private static Mat CreateDummyMat()
        {
            // Small dummy image for warmup
            return new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));
        }

        public async Task<OcrPageResult> OcrImageAsync(string imagePath, int pageIndex)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Ensure warmup is completed (non-blocking check)
            if (!_warmupCompleted)
            {
                _logger.Warn("Warmup not completed yet. Consider calling WarmupAsync() after service creation.");
            }

            var pooled = _pool.Get();
            bool isNewlyCreated = pooled.IsNewlyCreated;

            // Logging: Created vs Reused
            if (isNewlyCreated)
            {
                Interlocked.Increment(ref _createdCount);
                long created = Interlocked.Read(ref _createdCount);
                long reused = Interlocked.Read(ref _reusedCount);
                _logger.Info($"[OCR] New PaddleOCR engine created. Total Created: {created} | Reused: {reused}");
            }
            else
            {
                Interlocked.Increment(ref _reusedCount);
                long reused = Interlocked.Read(ref _reusedCount);
                long created = Interlocked.Read(ref _createdCount);

                if (reused % 10 == 0 || reused <= 20)
                {
                    _logger.Debug($"[OCR] Engine reused. Total Created: {created} | Reused: {reused}");
                }
            }

            try
            {
                using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
                if (mat.Empty())
                    throw new FileNotFoundException("Failed to load image", imagePath);

                var result = pooled.Engine.Run(mat);

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
            finally
            {
                _pool.Return(pooled);
            }
        }

        // Synchronous overload for backward compatibility
        public OcrPageResult OcrImage(string imagePath, int pageIndex)
        {
            return OcrImageAsync(imagePath, pageIndex).GetAwaiter().GetResult();
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

            _logger.Info($"Disposing PpOcrV5Service. Final stats - Created: {created}, Reused: {reused}");

            if (disposing)
            {
                // The pool will dispose its objects automatically when they're evicted
                // ObjectPool doesn't implement IDisposable, so we don't need to dispose it
                _logger.Debug("ObjectPool disposal not required - objects will be GC'd");
            }
        }

        //~PpOcrV5Service()
        //{
        //    Dispose(false);
        //}
    }

    public sealed class OcrEnginePolicy : IPooledObjectPolicy<PooledPaddleOcr>
    {
        public PooledPaddleOcr Create()
        {
            var engine = new PaddleOcrAll(
                LocalFullModels.ChineseV5,     // Ensure Sdcb.PaddleOCR.Models.LocalV5 is installed
                PaddleDevice.Mkldnn()) // PaddleDevice.Mkldnn()
            {
                AllowRotateDetection = true,
                Enable180Classification = true
            };

            return new PooledPaddleOcr(engine, isNew: true);
        }

        public bool Return(PooledPaddleOcr obj)
        {
            if (obj == null) return false;

            // Mark as reused for the next time it is taken from the pool
            obj.IsNewlyCreated = false;
            return true;
        }
    }

    public sealed class PooledPaddleOcr : IDisposable
    {
        public PaddleOcrAll Engine { get; }
        public bool IsNewlyCreated { get; internal set; }
        private bool _disposed = false;

        public PooledPaddleOcr(PaddleOcrAll engine, bool isNew)
        {
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            IsNewlyCreated = isNew;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Engine?.Dispose();
        }
    }
}