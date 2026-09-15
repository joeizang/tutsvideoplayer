# M4 review corrections

Review target: PR #5, original head `c4e0f87427d9`. These corrections are in the working tree and have not been pushed.

## Inline findings

| Finding | Correction | Verification |
| --- | --- | --- |
| Installation lock never starts | Register the lock as the first hosted service. Maintenance migration also takes ownership. | Hosted-service order, second-owner rejection, release/reacquisition; temporary Development migration. |
| Invalid recovery/failure transitions | Requeue in-flight work through Interrupted; allow blocking, interrupted failure and verified adoption. Recover each job independently. Retry counts remain bounded. | All four recoverable states with and without published output; disk block; three transient attempts; recorded temporary cleanup including cancellation followed by restart. |
| Adoption without provenance | Require matching lesson, generation, recipe, full source-set fingerprint, output name, length and SHA-256 before publishing. Preserve unverified files and sidecars. | Missing provenance, wrong output digest, valid existing output, Prepared recovery, invalid recovery preservation. |
| Missing permanent outputs never rebuild | Reconcile available output/manifest metadata after scans. Mark absent copies Stale and requeue completed jobs with a fresh attempt budget. Existing unverified files remain preserved and reported, never overwritten. | Remove a completed output and reschedule; assert Stale rendition and Queued job. |
| Explicit MP4/MKV fallback rejected | Apply automatic exemptions in scheduling only. Explicit requests may remux compatible codecs or use the supported H.264/AAC recipe. | MP4/MKV recipe selection and existing end-to-end encode/remux tests. |
| Extra automatic exemptions | Automatically consider M4V and WEBM as well as the other non-MP4/MKV sources. Unsupported recipes are reported as Blocked. | Scheduling tests for M4V and WEBM. |
| Configured reserve ignored | Inject the configured `IOptions<PreparationOptions>` instance. | Non-default reserve injection. |
| Wrong destination volume | Select the longest containing mount with a path-segment boundary. | Linux/macOS mount examples and similarly named sibling directory. |
| Progress invisible during encode | Await throttled progress writes on the encoder output reader; the executor does not use its DbContext concurrently. Renew the lease along with progress. | Paced real FFmpeg encode observed through a second DbContext before completion. |
| Concurrent creation returns 500 | Use SQLite `INSERT ... ON CONFLICT(DedupKey) DO NOTHING` and return the winning job for API and scan scheduling. | Eight simultaneous first creators converge on one persisted ID. |
| Blocked/failed watch feedback absent | Include failed jobs in playback manifests and render terminal/block explanations with a queue link. Queue controls reflect successful responses and report failures. | Manual browser checks of disk blocking on watch and home, pause, retry while paused. |
| Filename pattern hides originals | Exclude outputs only when a structurally valid provenance sidecar matches output name/length; exclude their sidecars too. A `.tvp-` substring alone has no ownership meaning. | Scanner retains an ordinary `intro.tvp-demo.wmv`; existing managed-output rescan test. |

## Related review notes

The chosen recipe is persisted on the job and rendition. Split-stream recipes reject unsupported video codecs before invoking stream copy. Library-relative output paths use the same resolved root as delivery. Post-commit scheduling errors cannot change a successful scan into a failed scan. Invalid outputs retain their provenance. Manifest writes flush before rename. Source identities use UTF-8, source bytes are rehashed before publication, and encoder diagnostics use a bounded tail. Worker startup waits for schema readiness. Queue pause uses 428/412 and supplies the current ETag.

Output discovery checks ownership metadata rather than repeatedly hashing the whole converted library. **Adoption/recovery performs full digest validation.** Originals and unverified collisions are never deleted or overwritten. Missing outputs can be regenerated; occupied but invalid output paths require inspection rather than automatic destructive replacement.

## Validation

- Full solution tests: 61 Core and 147 integration cases; one browser placeholder remains skipped.
- 32 added integration cases cover the regressions and related boundaries.
- Manual browser: a forced disk-reserve block displays its explanation and queue link; the home queue shows the same reason; pause and retry leave the job queued until resumed.
- Temporary fixtures and data directories only. The real library and existing application data were not modified. No Linux container deployment or whole-library conversion was rerun.
- Browser checks are manual; this change does not add an automated Playwright suite.
