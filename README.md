# OCR to SearchPDFs with Large Concurrency
This code has passed tests and can be directly used in productions.

There are two approaches provided here to implement large concurrency when generating searchable PDFs from scanned or snapped image files:

:sparkling_heart: First one: Object Pool + Channel&lt;T>.

:sweat_drops: Second one: ThreadLocal&lt;T> + Channel&lt;T>.

The first approach is suggested because it provides explicit, bounded control over expensive OCR instances. PaddleOCR objects are heavy—they load large models, allocate significant native memory/tensors, and are not always safe for unrestricted concurrent use. An object pool lets us pre-create and reuse a fixed small number of these instances (2–4 for GPU), preventing memory explosion and OOM errors while maintaining high throughput. Paired with a bounded System.Threading.Channels Channel, producers enqueue images asynchronously and a controlled set of workers borrow/return instances, creating natural backpressure and clean decoupling of I/O from heavy inference.

In contrast, ThreadLocal + Channel ties a full OCR instance to each thread, which works for tiny fixed-thread setups but scales poorly. Dynamic threading (common with Task.Run, Parallel, or async continuations) can create many more instances than intended, driving up RAM/VRAM usage with objects that are rarely cleaned up. This leads to higher GC pressure, poorer resource utilization, and less predictability under bursty PDF batch workloads. The pooled approach delivers better memory stability, easier tuning to hardware limits, and more reliable performance overall.

## Critical Techniques
:dizzy: Object Pool, 

:dizzy: Channel&lt;T>,

:dizzy: ConcurrentDictionary, 

:dizzy: EnumerateDirectories

:dizzy: ThreadLocal&lt;T>

:dizzy: PaddleOCR
