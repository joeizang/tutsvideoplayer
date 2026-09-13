# Verification and acceptance matrix

Status: planned checks, not executed application tests. The documentation phase validates document consistency and links only.

## Test strategy

Use focused unit tests for domain rules, real SQLite integration tests for persistence/concurrency/migrations, ASP.NET integration tests for routing/contracts/ranges, and browser tests for watching flows. Use small generated or redistributable media fixtures with known properties; do not commit the learner's tutorial files. Browser tests should use Playwright for .NET where practical, with additional real-browser codec validation.

The EF in-memory provider is not an acceptable substitute for SQLite SQL translation, uniqueness, migrations, or locking behavior. In-memory SQLite is suitable for some fast relational tests, but file-backed SQLite is required for WAL/restart/upgrade/recovery scenarios. Abstract process/filesystem boundaries only where needed for deterministic failure injection; real FFmpeg fixtures remain necessary.

## Fixture catalogue

- Numbered nested course folders with spaces, punctuation, mixed case, Unicode composed/decomposed names, and long filenames.
- MP4 H.264/AAC 720p; MP4 1080p; a higher-resolution short source; native 4:3 source.
- WMV3/WMA2 legacy sample; MP4/MKV with deliberately unsupported browser codec; ordinary playable MKV where supported.
- Video-only TS plus matching AAC, missing companion, ambiguous companion, duration mismatch, and delayed audio start.
- Adjacent SRT, valid VTT, separate Subtitle directory, language suffixes, duplicate title matches, malformed cues, unsafe text, and non-UTF-8 input.
- Same-stem unrelated MP4/WMV, duplicated content in different courses, source replaced at same path, and relocated source.
- Partial output, Prepared manifest, committed output without DB row, missing output with manifest, stale recipe/source generation.
- Permission-denied subtree, absent root, symlink escape/loop, changed file during probe/encode, and insufficient output space.

## Acceptance mapping

| Case | Requirement | Verification and expected result |
| --- | --- | --- |
| V01 | LIB-01, LIB-02, LIB-04 | Scan nested fixture twice; stable course/lesson IDs and numeric order |
| V02 | LIB-03 | TS/AAC pair appears once; archives/text/padding/subtitles are not lessons |
| V03 | LIB-05, LIB-06 | Concurrent scans converge; canceled/failed scan cannot mark whole library missing |
| V04 | LIB-08/PREP-02 | Managed copy plus original remains one lesson after rescan and catalog rebuild |
| V05 | LRN-01 | Pause/save/restart returns within two seconds of acknowledged position |
| V06 | LRN-02 | Paused seek near end does not complete; playing threshold/ended does; manual override wins |
| V07 | LRN-03 | Previous/next follows canonical tree order and stops at course boundary |
| V08 | LRN-04 | Conversion completion does not create watched progress |
| V09 | LRN-01, LRN-02 | Older session cannot overwrite current resume position or manual completion |
| V10 | PLAY-01 | GET/HEAD, valid range, suffix range, conditional range, and 416 behavior are correct |
| V11 | PLAY-02, PLAY-03 | Native 4:3/16:9, fit/fill, speed, seeking, mute, fullscreen work |
| V12 | PLAY-04 | 720p source has no upscaled 1080 option; high source defaults per ready-policy |
| V13 | PLAY-05 | Quality replacement preserves paused/playing state, time, speed, volume, subtitles |
| V14 | PLAY-06 | Autoplay off initially; rejected browser play prompt remains recoverable |
| V15 | SUB-01, SUB-02 | Adjacent/separate-folder tracks match conservatively and render valid timing |
| V16 | SUB-03 | Off survives navigation/restart; ambiguous track needs explicit association |
| V17 | PREP-01, PREP-06 | Non-MP4/non-MKV recognized videos auto-queue; MP4/MKV only explicit compatibility |
| V18 | PREP-03, PREP-05 | WMV and TS/AAC yield correct compatible MP4s with original hashes unchanged |
| V19 | PREP-04 | One encoder, durable priority, queue pause lets current finish, restart recovery |
| V20 | PREP-07 | Kill at each publication boundary; reconcile ready output or restart safely |
| V21 | OPS-05, OPS-06 | Cache eviction never deletes source/permanent/active files; reservations obey limit |
| V22 | OPS-07/LRN-05 | Transfer across absolute roots; preview conflicts/duplicates; stale apply rejected |
| V23 | OPS-01, OPS-04 | Linux image serves UI/assets/media and retains state on container replacement |
| V24 | OPS-02 | Loopback default; configured LAN access works; unrecognized hosts/origins rejected |
| V25 | OPS-03, OPS-08 | Second process cannot run same data; interrupted leases recovered once |
| V26 | OPS-09 | Missing FFmpeg/root/read-only mount produces accurate degraded behavior |
| V27 | OPS-10 | Before/after hashes verify originals and subtitles unchanged across all workflows |
| V28 | Data integrity | Real SQLite constraints/revisions, dedup races, migration upgrades, backup restoration |
| V29 | Frontend | Keyboard focus, tree navigation, menus, screen reader, reduced motion, long names |
| V30 | Local runtime | Browser network log has no required CDN/fonts/scripts after installation |
| V31 | LIB-07, LRN-01 | Search course/lesson titles including punctuation, clear empty results, and Continue learning after restart; fully completed course offers replay |

## Crash injection checklist

Inject termination after job claim, during encode, after validation, after Prepared manifest, after final rename, after committed manifest, before DB ready commit, after DB commit, and during cache eviction. In each case, restart and assert exactly one logical lesson, no partial ready rendition, correct job result, retained originals, and no redundant successful re-encode.

Do not simulate recovery only by manually setting a final state in a unit test. Use real files and a restartable host for publication boundaries, with deterministic injected checkpoints in test configuration.

## Performance targets

These are provisional engineering targets to measure, not benchmark claims. Record hardware, browser, dataset, warm/cold state, and concurrent work with every result.

| Operation | Initial target |
| --- | --- |
| Warm course/search/tree API on ~1,031 lessons | p95 below 300 ms on local host without encoding; below 750 ms during one encode |
| Direct compatible playback startup | Typically within 2 seconds on local SSD/LAN after user action, excluding browser policy prompts |
| Resume accuracy | Within 2 seconds of last server-acknowledged position |
| Progress durability interval | Ten-second periodic saves plus event flushes; document possible last-interval loss on abrupt exit |
| Job/scan status freshness | Within roughly 3 seconds on a visible active page |
| Media memory | Bounded buffers; not proportional to full video file size |
| Query count | Within the constant bounds in persistence query catalogue |
| Tree scaling | Usable full course below safety threshold; folder expansion above it |

Scan and encode duration have no promised absolute SLA before measurement. Report discovery time separately from hashing/probing/preparation. Test a synthetic 10,000-lesson catalog to identify accidental N+1 or whole-library graph loading; do not broaden the product into a distributed media system on that basis alone.

## Browser matrix

Primary: current stable Safari and Chrome on the Mac; Chrome/Chromium and Firefox on a desktop/laptop Linux client or a laptop connecting to the Linux server. Record exact versions at implementation time. Browser codec support depends on browser/OS/build; an automated Chromium pass does not establish Safari/Firefox support. Phones are excluded.

Exercise fullscreen, rate, subtitle rendering, ranges, source switching, and unsupported-codec fallback in each supported browser. Automated WebKit is useful but does not replace final Safari verification on the Mac. Cross-browser differences must become documented supported behavior or a compatible preparation path.

## Review evidence to retain

Store verification reports under `docs/implementation` with date, commit/build identity once available, pass/fail cases, observed limitations, and links to artifacts. Keep executable test code and fixture-generation code under `src/Tests`. Avoid committing large copyrighted media or logs containing unnecessary host details.

The release review includes representative screenshots, actual generated SQL/query counts, migration review evidence, codec fixture results, original hash comparisons, and restart recovery outcomes. Only mark requirements complete when their behavior is demonstrated.
