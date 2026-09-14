import { useState } from "react";
import type { ViewProps } from "dotnet:rendering";
import type {
    LibraryHomeModel,
    CourseSummaryModel,
    ContinueLearningEntryModel,
    ScanStatusModel
} from "dotnet:types/TutsVideoPlayer/Web/Models";
import { RefreshLibraryButton } from "../Shared/RefreshLibraryButton.tsx";
import "/css/app.css";

export const head = (model: LibraryHomeModel) => ({
    title: model.summary.libraryName,
    links: [{ rel: "icon", href: "/favicon.svg", type: "image/svg+xml" }]
});

function scanLabel(scan: ScanStatusModel | null | undefined): string | null {
    if (!scan) return null;
    if (scan.state === "Running") return "Scanning library…";
    if (scan.state === "Failed") return "Last scan failed";
    if (scan.state === "Canceled") return "Last scan was canceled";
    return null;
}

function formatClock(durationMs: number | null | undefined): string | null {
    if (!durationMs || durationMs <= 0) return null;
    const totalSeconds = Math.round(durationMs / 1000);
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${seconds.toString().padStart(2, "0")}`;
}

export default function Index({ model }: ViewProps<LibraryHomeModel>) {
    const [summary] = useState(model.summary);
    const courses = model.courses;
    const query = model.searchQuery;
    const continueEntries = model.continueEntries ?? [];
    const scanNotice = scanLabel(summary.latestScan);
    const lastPage = Math.max(1, Math.ceil(model.matchingCourseCount / model.pageSize));

    return (
        <main className="mx-auto min-h-screen max-w-5xl bg-canvas px-5 py-8 pb-12 text-ink dark:bg-neutral-900 dark:text-neutral-100">
            <header className="mb-6 flex flex-wrap items-start justify-between gap-4">
                <div>
                    <h1 className="m-0 mb-1 text-2xl font-semibold">{summary.libraryName}</h1>
                    <p className="m-0 text-sm text-ink-soft dark:text-neutral-400">
                        {summary.courseCount} courses · {summary.availableLessonCount} lessons available
                        {summary.missingLessonCount > 0 ? ` · ${summary.missingLessonCount} missing` : ""}
                    </p>
                </div>
                <RefreshLibraryButton
                    initialScanState={summary.latestScan?.state ?? null}
                    initialScanRunId={summary.latestScan?.scanRunId ?? null}
                />
            </header>

            {scanNotice ? (
                <p className={`mb-4 rounded-lg border px-3 py-2 text-sm ${summary.latestScan?.state === "Failed" ? "border-danger/30 text-danger dark:text-red-400" : "border-ink/10 bg-surface text-ink-soft dark:border-neutral-700 dark:bg-neutral-800 dark:text-neutral-400"}`}>
                    {scanNotice}
                </p>
            ) : null}

            {continueEntries.length > 0 ? (
                <section aria-labelledby="continue-heading" className="mb-6 rounded-xl border border-ink/10 bg-surface p-4 dark:border-neutral-700 dark:bg-neutral-800">
                    <h2 id="continue-heading" className="m-0 mb-3 text-base font-semibold">Continue learning</h2>
                    <ul className="m-0 list-none space-y-2 p-0">
                        {continueEntries.map((entry) => (
                            <ContinueRow key={entry.courseId} entry={entry} />
                        ))}
                    </ul>
                </section>
            ) : null}

            {summary.courseCount === 0 ? (
                <section aria-live="polite" className="rounded-xl border border-ink/10 bg-surface p-5 dark:border-neutral-700 dark:bg-neutral-800">
                    <h2 className="m-0 mb-2 text-base font-semibold">No courses yet</h2>
                    <p className="m-0 text-sm text-ink-soft dark:text-neutral-400">
                        The library is scanned on startup and on refresh. Each top-level folder inside the
                        configured library root that contains discoverable videos becomes a course.
                    </p>
                </section>
            ) : (
                <section aria-labelledby="courses-heading" className="rounded-xl border border-ink/10 bg-surface p-4 dark:border-neutral-700 dark:bg-neutral-800">
                    <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
                        <h2 id="courses-heading" className="m-0 text-base font-semibold">Courses</h2>
                        <form className="flex gap-2" method="get" action="/" role="search">
                            <input
                                type="search"
                                name="q"
                                defaultValue={query ?? ""}
                                placeholder="Search courses and lessons"
                                aria-label="Search courses and lessons"
                                className="min-w-64 rounded-lg border border-ink/15 bg-canvas px-3 py-1.5 text-sm dark:border-neutral-600 dark:bg-neutral-900"
                            />
                            <button type="submit" className="rounded-lg border border-ink/15 px-3 py-1.5 text-sm font-semibold hover:bg-canvas dark:border-neutral-600 dark:hover:bg-neutral-700">
                                Search
                            </button>
                        </form>
                    </div>

                    {query != null && courses.length === 0 ? (
                        <p className="m-0 text-sm text-ink-soft dark:text-neutral-400">
                            No courses or lessons match “{query}”. <a href="/" className="text-action">Clear search</a>
                        </p>
                    ) : (
                        <>
                            <ul className="m-0 list-none p-0">
                                {courses.map((course) => (
                                    <CourseRow key={course.id} course={course} />
                                ))}
                            </ul>
                            <CoursePager
                                page={model.page}
                                pageSize={model.pageSize}
                                lastPage={lastPage}
                                total={model.matchingCourseCount}
                                query={query}
                            />
                        </>
                    )}
                </section>
            )}
        </main>
    );
}

function coursesHref(page: number, pageSize: number, query: string | null | undefined): string {
    const parameters: string[] = [];
    if (query) {
        parameters.push(`q=${encodeURIComponent(query)}`);
    }
    if (page > 1) {
        parameters.push(`page=${page}`);
    }
    // The effective page size has to travel with the link, or Next lands on a page the
    // controller sizes with its own default and quietly skips or repeats courses.
    parameters.push(`pageSize=${pageSize}`);

    return `/?${parameters.join("&")}`;
}

function CoursePager({ page, pageSize, lastPage, total, query }: {
    page: number;
    pageSize: number;
    lastPage: number;
    total: number;
    query: string | null | undefined;
}) {
    if (lastPage <= 1) {
        return null;
    }

    return (
        <nav className="mt-3 flex items-center justify-center gap-3 text-sm text-ink-soft dark:text-neutral-400" aria-label="Course pages">
            {page > 1 ? (
                <a rel="prev" href={coursesHref(page - 1, pageSize, query)} className="font-semibold text-action">Previous</a>
            ) : (
                <span>Previous</span>
            )}
            <span>{`Page ${page} of ${lastPage} · ${total} courses`}</span>
            {page < lastPage ? (
                <a rel="next" href={coursesHref(page + 1, pageSize, query)} className="font-semibold text-action">Next</a>
            ) : (
                <span>Next</span>
            )}
        </nav>
    );
}

function continueHref(entry: ContinueLearningEntryModel): string {
    // Replay must start the lesson from the beginning. Without an explicit intent the player
    // restores the saved position, which for a course completed by playing to the end means
    // opening each lesson a second before it finishes.
    return entry.recommendation === "replay"
        ? `/watch/${entry.recommendedLessonId}?replay=1`
        : `/watch/${entry.recommendedLessonId}`;
}

function continueNote(entry: ContinueLearningEntryModel): string | null {
    if (entry.recommendation === "unavailable") {
        return "The source file is missing and no other lesson in this course is available.";
    }

    if (!entry.lessonAvailable) {
        return "That lesson's source file is missing; continuing with the next available lesson.";
    }

    if (entry.sourceChanged) {
        return "The source changed since you watched; earlier progress is kept but not used.";
    }

    return null;
}

function ContinueRow({ entry }: { entry: ContinueLearningEntryModel }) {
    const clock = formatClock(entry.durationMs);
    const note = continueNote(entry);
    const label = entry.recommendation === "replay"
        ? "Replay course"
        : entry.recommendation === "next"
            ? "Start next"
            : entry.recommendation === "unavailable"
                ? "Unavailable"
                : "Resume";

    return (
        <li className="flex flex-wrap items-center justify-between gap-2 rounded-lg px-2 py-2 hover:bg-canvas dark:hover:bg-neutral-700">
            <div className="min-w-0">
                <div className="truncate text-sm font-semibold">
                    {entry.recommendation === "unavailable" ? (
                        <span>{entry.courseTitle}</span>
                    ) : (
                        <a href={continueHref(entry)} className="text-action">
                            {entry.recommendation === "replay" ? "Replay " : "Continue "}
                            {entry.courseTitle}
                        </a>
                    )}
                </div>
                <div className="truncate text-xs text-ink-soft dark:text-neutral-400">
                    {entry.lessonTitle}
                    {entry.positionMs > 0 && clock ? ` · at ${formatClock(entry.positionMs)}${entry.durationMs ? ` of ${clock}` : ""}` : ""}
                    {` · ${entry.completedLessons} of ${entry.totalLessons} complete`}
                </div>
                {note ? <div className="truncate text-xs text-danger dark:text-red-400">{note}</div> : null}
            </div>
            {entry.recommendation === "unavailable" ? (
                <span className="rounded-lg border border-ink/15 px-3 py-1.5 text-xs font-semibold text-ink-soft dark:border-neutral-600 dark:text-neutral-400">
                    {label}
                </span>
            ) : (
                <a
                    href={continueHref(entry)}
                    className="rounded-lg bg-action px-3 py-1.5 text-xs font-semibold text-white hover:opacity-90"
                >
                    {label}
                </a>
            )}
        </li>
    );
}

function CourseRow({ course }: { course: CourseSummaryModel }) {
    return (
        <li className={`flex items-baseline justify-between gap-4 border-b border-ink/10 px-1 py-2.5 last:border-b-0 dark:border-neutral-700`}>
            <span className="min-w-0 truncate text-sm font-semibold">
                <a href={`/courses/${course.id}`} className={course.available ? "text-ink hover:text-action dark:text-neutral-100" : "text-ink-soft dark:text-neutral-400"}>
                    {course.title}
                </a>
            </span>
            <span className="whitespace-nowrap text-xs text-ink-soft dark:text-neutral-400">
                {course.completedLessonCount > 0 ? `${course.completedLessonCount} done · ` : ""}
                {course.availableLessonCount} of {course.lessonCount} available
                {course.missingLessonCount > 0 ? ` · ${course.missingLessonCount} missing` : ""}
            </span>
        </li>
    );
}