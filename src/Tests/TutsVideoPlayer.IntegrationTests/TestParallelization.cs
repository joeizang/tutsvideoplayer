using Xunit;

// The integration hosts share one process: parallel collections would race on
// ffmpeg, on JsxCore's staging directory, and on machine-wide resources. The
// fixture classes already isolate their own databases; sequential collections
// keep the whole suite deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TutsVideoPlayer.IntegrationTests;
