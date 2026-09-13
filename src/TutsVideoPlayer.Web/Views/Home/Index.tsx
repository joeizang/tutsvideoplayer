import { useState } from "react";
import type { ViewProps } from "dotnet:rendering";
import type { LibraryHomeModel, CourseSummaryModel, ScanStatusModel } from "dotnet:types/TutsVideoPlayer/Web/Models";
import { RefreshLibraryButton } from "../Shared/RefreshLibraryButton.tsx";
import "../Shared/app.css";

export const head = (model: LibraryHomeModel) => ({
    title: model.summary.libraryName,
    links: [{ rel: "icon", href: "/favicon.svg", type: "image/svg+xml" }]
});

function scanLabel(scan: ScanStatusModel | null | undefined): string | null {
    if (!scan) return null;
    if (scan.state === "Running") return "Scanning library…";
    if (scan.state === "Failed") return "Last scan failed";
    if (scan.state === "Canceled") return "Last scan was canceled";
    return `Library scanned (${scan.discoveredCount} lessons)`;
}

export default function Index({ model }: ViewProps<LibraryHomeModel>) {
    const [summary] = useState(model.summary);
    const courses = model.courses;
    const query = model.searchQuery;
    const scanNotice = scanLabel(summary.latestScan);
    const lastPage = Math.max(1, Math.ceil(model.matchingCourseCount / model.pageSize));

    return (
        <main className="app-shell">
            <header className="library-header">
                <div>
                    <h1>{summary.libraryName}</h1>
                    <p className="subtitle">
                        {summary.courseCount} courses · {summary.availableLessonCount} lessons available
                        {summary.missingLessonCount > 0 ? ` · ${summary.missingLessonCount} missing` : ""}
                    </p>
                </div>
                <RefreshLibraryButton
                    initialScanState={summary.latestScan?.state ?? null}
                    initialScanRunId={summary.latestScan?.scanRunId ?? null}
                />
            </header>

            {scanNotice ? <p className={summary.latestScan?.state === "Failed" ? "scan-error" : "scan-notice"}>{scanNotice}</p> : null}

            {summary.courseCount === 0 ? (
                <section className="empty-state" aria-live="polite">
                    <h2>No courses yet</h2>
                    <p>
                        The library is scanned on startup and on refresh. Each top-level folder inside the
                        configured library root that contains discoverable videos becomes a course.
                    </p>
                </section>
            ) : (
                <section aria-labelledby="courses-heading">
                    <div className="section-header">
                        <h2 id="courses-heading">Courses</h2>
                        <form className="search-form" method="get" action="/" role="search">
                            <input
                                type="search"
                                name="q"
                                defaultValue={query ?? ""}
                                placeholder="Search courses and lessons"
                                aria-label="Search courses and lessons"
                            />
                            <button type="submit">Search</button>
                        </form>
                    </div>

                    {query !== null && courses.length === 0 ? (
                        <p className="empty-search">
                            No courses or lessons match “{query}”. <a href="/">Clear search</a>
                        </p>
                    ) : (
                        <>
                            <ul className="course-list">
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
    // Built by hand rather than with URLSearchParams, which the server-side render host
    // does not provide.
    const parameters: string[] = [];
    if (query) {
        parameters.push(`q=${encodeURIComponent(query)}`);
    }
    if (page > 1) {
        parameters.push(`page=${page}`);
    }
    parameters.push(`pageSize=${pageSize}`);

    return `/?${parameters.join("&")}`;
}

function CoursePager({
    page,
    pageSize,
    lastPage,
    total,
    query
}: {
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
        <nav className="course-pager" aria-label="Course pages">
            {page > 1 ? (
                <a rel="prev" href={coursesHref(page - 1, pageSize, query)}>
                    Previous
                </a>
            ) : (
                <span className="course-pager-disabled">Previous</span>
            )}
            <span className="course-pager-status">
                {`Page ${page} of ${lastPage} · ${total} courses`}
            </span>
            {page < lastPage ? (
                <a rel="next" href={coursesHref(page + 1, pageSize, query)}>
                    Next
                </a>
            ) : (
                <span className="course-pager-disabled">Next</span>
            )}
        </nav>
    );
}

function CourseRow({ course }: { course: CourseSummaryModel }) {
    return (
        <li className={course.available ? "course-row" : "course-row course-missing"}>
            <span className="course-title">
                {/* Course and lesson IDs are independent: the entry route resolves a lesson
                    that belongs to this course instead of reusing the course ID as one. */}
                <a href={`/courses/${course.id}`}>{course.title}</a>
            </span>
            <span className="course-counts">
                {course.availableLessonCount} of {course.lessonCount} lessons available
                {course.missingLessonCount > 0 ? ` · ${course.missingLessonCount} missing` : ""}
            </span>
        </li>
    );
}
