# Media performance in 0.3.1

The old 0.2 loop imposed a 250-ms delay **after** capture/encode/send and rendered on an 8-FPS UI timer. It was a view-only baseline, not smooth media. 0.3.1 removes those deliberate ceilings:

- native GDI downscale before WPF/WIC, cached bitmap/DC per geometry;
- binary JPEG envelope instead of base64 JSON;
- 5/15/20/24/30 FPS target with work time subtracted from the frame period;
- serialized bounded writes; skip media when the writer is busy with control/files;
- a one-frame DropOldest channel, off-UI decode and frozen images, no accumulating stale-frame queue;
- inactive tabs request 2 FPS and skip decode, selected tabs display actual received FPS;
- Fast 960px/JPEG40, Balanced 1440px/55, Sharp 1920px/80; no upscaling beyond source resolution; max height 1200.

Fit/Actual size/Stretch alter display geometry, not permission. Mouse mapping excludes letterbox areas and normalizes back to the primary display. Animation suppression is separate, temporary Host-approved Windows policy, not a secret optimization switch.

`run/Performance.txt` (when generated) records a small capture/downscale/JPEG benchmark on the Windows CI runner. It is not physical LAN end-to-end latency, GPU certification or proof of 30 FPS on all PCs. The multi-tab UI test exercises the production Host runtime with real captures and waits for repeated frames. Measure capture/encode cost, actual receive FPS, bandwidth, input round-trip, RAM/GC/handles and one-hour multi-session behavior on target hardware.

JPEG/TCP still has bandwidth and head-of-line limitations. H.264/Media Foundation hardware encoding, DXGI/Windows Graphics Capture and WebRTC's low-latency/adaptive transport remain future work. Do not promise 4K60, gaming-grade latency, smooth protected/HDR/UAC capture or smoothness independent of hardware/network/load.
