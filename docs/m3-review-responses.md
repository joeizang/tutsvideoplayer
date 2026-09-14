# PR #4: responses to the M3 inline review comments

PR: [M3 Milestone 3 Subtitles](https://github.com/joeizang/tutsvideoplayer/pull/4)  
Reviewed head: `8186843495c1`

All seven inline comments are addressed. 61 Core and 110 integration tests pass (5 added).
A new migration, `SubtitleAvailabilityAndNormalizationSource`, adds `SubtitleTracks.Availability`,
`NormalizedSourceLengthBytes` and `NormalizedSourceModifiedUtcMs`, and changes the
`SubtitleAssociations → SubtitleTracks` foreign key from Cascade to Restrict.

| Comment | Change | Covering test |
| --- | --- | --- |
| Replace the association instead of changing its primary key | The head commit had already moved from mutating `SubtitleTrackId` to delete-and-insert; the two halves now commit inside one transaction, so a failed insert cannot leave the lesson with no preference, and the requested regression exists. | `SwitchingBetweenTwoExistingTracksReplacesTheAssociation` |
| Invalidate normalized captions when the source changes | Each conversion records the size and modification time of the file it read. Delivery compares the live file against those values before serving a cached artifact, and scanning resets the parse state when they no longer match. A recorded failure is likewise tied to the bytes that failed, so a corrected file recovers on its next request. | `EditingASubtitleSourceInvalidatesTheNormalizedCaptions` (covers both the edited-between-scans and the rescanned paths) |
| Resolve a track when returning to Automatic selection | Automatic resolution is now computed independently of the manual preference and exposed as `resolvedId`. The player re-reads candidates after choosing Automatic and adopts the resolved track instead of clearing the rendered text track. | `ReturningToAutomaticResolvesATrack` |
| Restore the resolved track when switching subtitles back on | The manifest carries the resolved selection regardless of the global Off switch; visibility is the client's concern. Turning subtitles on therefore restores the saved association or exact automatic match without a reload. | `GlobalOffOverridesSelectionAndSurvivesLessonChanges` (assertion inverted to the corrected behaviour) |
| Use strict decoding for malformed subtitle bytes | Decoding no longer uses the replacement fallback. A byte-order mark selects UTF-16/UTF-32 explicitly; everything else must be valid UTF-8, and a file that is not is reported. | `UndecodableBytesAreReportedRatherThanReplaced` (byte-level fixture containing `0xFF`) |
| Preserve manual preference when its sidecar disappears | The scanner marks a vanished track missing instead of deleting it, and the association's foreign key is Restrict rather than Cascade. The preference is kept, the candidate is listed as missing and unselectable, and the explanation is returned instead of another file being substituted. | `AManualPreferenceSurvivesItsSidecarDisappearing` |
| Surface failures from lazy subtitle normalization | The `track` element has an error handler that re-reads the candidate list and shows the recorded parse error, keeping the selection and the menu so another file can be chosen. Candidates now carry a per-track `message`. | Browser behaviour; the server side is covered by the `Failed` state and message assertions in `UndecodableBytesAreReportedRatherThanReplaced` |

## Verification notes

Each new test was confirmed to fail against the unfixed behaviour before being accepted:
reverting to replacement-fallback decoding, to a cache check that ignores source identity, and
to the original `existing.SubtitleTrackId = trackId` key mutation (which reproduced the
reported 200-then-404 exactly) each failed the matching test.

Limits: the track-element error path and the Subtitles menu are client-only behaviour with no
automated browser test in this round. Linux deployment was not re-tested, and neither the real
library nor its subtitle sources were modified.
