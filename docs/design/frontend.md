# Frontend design and interaction specification

## Design intent

A quiet tutorial workbench: the learner's current lesson is the main visual object, and the course sequence remains easy to scan beside it. The application should feel like a well-organized personal reference library, not a streaming-service storefront or a metrics dashboard. Use real course titles such as C# Generics and the configuration/options course during layout validation.

This document is the frontend skill's planning and critique phase. It deliberately contains no implemented UI. The visual direction below is an implementation default within the agreed workflow.

## First-pass alternatives and critique

Alternative A: a grid of large poster cards and an oversized featured course. It would waste space because the local library has meaningful filenames but no confirmed cover artwork; arbitrary generated covers would obscure the course structure.

Alternative B: a dense file-manager tree filling the entire application. It would match the storage structure but bury Continue learning and make watching feel like filesystem administration.

Chosen revision: a compact Continue learning shelf above a text-first course list, then a watch workspace with a prominent player and a persistent sequence rail. The memorable element is the course rail: genuine numbered lessons, nested folder labels, and small completion marks. Numbers come from the curriculum, not decorative design.

## Token plan

| Token | Value | Role |
| --- | --- | --- |
| Canvas | `#F3F6FA` | Cool neutral application background |
| Surface | `#FFFFFF` | Reading panels and menus |
| Ink | `#182B45` | Primary text and course titles |
| Secondary ink | `#53657B` | Supporting metadata |
| Action blue | `#245FC7` | Selected lesson, primary action, focus treatment |
| Completion green | `#23664C` | Completion icon/text with an accompanying label |

Video letterboxing uses actual black because it is media presentation, not generic dark application chrome. Derive subtle borders from ink at low opacity. Validate final text/background combinations for WCAG AA during implementation; token selection is not a completed contrast audit. Error messages use a clear icon and text plus a separately validated semantic error tone.

Typography: propose locally bundled Source Sans 3 for the interface, with `Segoe UI` and sans-serif fallbacks. Use one family rather than decorative headings. Confirm redistribution/license and bundle only required weights during implementation. Roles: 14 px compact metadata, 16 px controls/body, 20 px lesson title, 28 px course/home heading. Use 1.45–1.55 body line height, normal sentence case, and tabular numerals only for timing/alignment. Do not use monospace metadata merely because the content is technical.

Spacing follows a 4 px base with 8/12/16/24/32 px groups. Use restrained corner radii according to function: small controls, slightly larger menus, and no repeated shadowed card grid. Paragraphs stay below roughly 75 characters where practical. Long course titles wrap, and full filenames remain available without relying solely on hover.

## Home layout

```text
+-----------------------------------------------------------------------+
| Tutorial library                         [Search courses and lessons] |
|                                                   [Queue] [Settings] |
| Continue learning                                                     |
| C# Generics                       Lesson title             [Continue]|
| Last position 12:34               8 of 24 lessons complete             |
|                                                                       |
| Courses                                           [Refresh library]   |
| Course title                         Lessons     Progress              |
| C# Generics                          ...         ...                   |
| Using Configuration and Options...   ...         ...                   |
| ...                                                                   |
+-----------------------------------------------------------------------+
```

Counts above are schematic except where independently verified; the implementation must show catalog-derived values. Do not fabricate durations or progress. The Continue shelf appears only with actual viewing history. On first launch, use the space for scan status and an explanation of the discovered library, not marketing copy.

Course rows prioritize title and resume action. Show preparation status only where it affects access, not a badge for every possible metadata property. Search matches can reveal lesson hits grouped under their course; selecting a hit opens the corresponding lesson with its tree ancestors expanded.

## Watch workspace

```text
+------------------------------------------------------------------------+
| [Library]  C# Generics                            [Queue] [Settings]    |
|-----------------------+------------------------------------------------|
| Course lessons        |                                                |
| [Collapse folders]    |                 Video                          |
| v 01 Introduction     |         original proportions                   |
|   ✓ 01 Welcome        |                                                |
|   > 02 Current lesson |------------------------------------------------|
| v 02 Constraints      | [Play] -10 +10  12:34 / 28:10      [Fullscreen] |
|   o 01 ...            | Seek track                                     |
|                       | Volume  Speed  Quality  Subtitles  Fit          |
|                       | Current lesson title                           |
|                       | [Previous] [Mark completed] [Next]             |
+-----------------------+------------------------------------------------+
```

Keep text left-aligned. The lesson rail begins around 300 px and is resizable within usable limits. At laptop widths it can collapse to a drawer without interrupting playback. Do not force a 16:9 crop on 4:3 sources; default contain shows the whole picture. Fill crops and is named accordingly. No stretch control is required by the agreed plan.

The selected lesson has an explicit marker and accessible current state, not only a colored background. Completed, missing, and preparing statuses use distinct labeled icons. Preparation updates must not reorder the tree or steal focus. Scrolling the tree does not scroll the video away unnecessarily; avoid trapping keyboard navigation in nested scroll areas.

## Component boundaries

- LibraryPage: query/filter/paging state and ContinueLearningList.
- CourseList and CourseSearchResults: compact catalog projections.
- WatchPage: selected lesson routing and coordinated player/rail state.
- CourseTree: accessible hierarchy, expansion state, current/completed/missing markers.
- VideoPlayer: native media element, source-switch lifecycle, controls, playback-session adapter.
- QualityMenu and SubtitleMenu: available/preparing/error states with explicit actions.
- PreparationPanel: queue, pause/resume, retry, storage constraints.
- SettingsPage: remembered preferences, storage breakdown, progress transfer.
- ImportPreview: matched/unmatched/conflict groups and explicit resolutions.

Keep browser media event listeners in a focused hook/controller and remove them on disposal. Keep server data separate from ephemeral UI state. Local React state/context is adequate initially; do not add Redux or a global event bus without a demonstrated need. Route/shareable state belongs in URLs; volume can remain device-local while agreed speed/subtitle/autoplay preferences persist on the server.

## Player state transitions

States: loading manifest, ready paused, playing, buffering, changing quality, waiting for preparation, unavailable, and failed. Completion is learning state, not a player transport state.

On quality change, capture time, paused state, rate, volume, mute, and subtitle preference. Renew the new rendition lease, load the source, wait for metadata and a seekable range, clamp time, restore settings, and resume only if previously playing. Handle `play()` rejection as a user-action requirement. Cancel a pending switch if another lesson is selected. Suppress incidental pause/time events from overwriting progress during source replacement.

Retry direct playback conservatively: a codec hint can help select a source, but actual browser success is the final check. A network error should not automatically trigger costly transcoding. Offer compatibility preparation when evidence indicates unsupported media, and provide ordinary retry for unavailable/network errors.

## Keyboard and accessibility

Controls are native buttons, sliders, and menus where possible. Space toggles playback only when focus is within the player and not in an input/menu; Left/Right skips ten seconds under the same conditions. F toggles fullscreen, M mute, and C subtitles when focus context permits. Provide a discoverable shortcuts help panel. Respect native text editing and tree navigation; global single-letter keys must not hijack search typing.

Course tree uses the appropriate tree or nested disclosure semantics with roving focus if implementing a full tree widget. Arrow keys navigate nodes; Enter opens a lesson; disclosure controls announce expansion. Test with a screen reader, not merely automated role checks. Menus return focus to their triggers. Sliders announce values and units.

Use visible focus, at least WCAG AA text contrast, reasonably sized pointer targets, reduced-motion support, and non-color status cues. Limit live announcements to meaningful transitions; do not announce every percentage update. No auto-playing ambient animation or decorative entrance sequence. Drawer and menu motion can be brief and disabled by reduced-motion preferences.

Desktop/laptop release targets are 1280×720 and 1440×900, with usable reflow down to 1024×768 and browser zoom. Small windows should remain operable, but phone-specific layout and testing are excluded by the user's scope. Do not expand the release to phone UX because a general design skill normally recommends it.

## Microcopy and recovery

| State | Message/action |
| --- | --- |
| Waiting for WMV preparation | Preparing this lesson for playback. / View queue |
| Queue paused | Preparation is paused. The current lesson will finish preparing. / Resume queue |
| Subtitles absent | No subtitles found for this lesson. / Choose a subtitle file |
| Disk blocked | Not enough free space to prepare this version. / View storage |
| Source missing | The source file is unavailable. / Refresh library |
| Progress write failed | Your latest position could not be saved. / Retry |
| Ambiguous subtitle | More than one subtitle file matches. / Choose subtitles |
| Import conflict | This lesson has progress on both installations. / Keep local / Use imported |

Use Choose a subtitle file only for selecting a discovered course-local candidate, not an arbitrary host file picker. Keep codec names and hashes in optional details. “Clear quality cache” must state that permanent playback copies remain; it is not a generic destructive delete button.

## Visual verification before release

Capture home, watch, expanded tree, 4:3 playback, long course title, preparation failure, settings storage, and import conflict at the target viewports. Review information hierarchy, focus order, overflow, contrast, and state consistency. Remove any badge, divider, shadow, or metadata label that does not help the learner decide or navigate. The current documentation does not claim these screens have been rendered or tested.
