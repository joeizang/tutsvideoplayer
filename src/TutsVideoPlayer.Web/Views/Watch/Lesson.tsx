import type { ViewProps } from "dotnet:rendering";
import type { WatchLessonModel } from "dotnet:types/TutsVideoPlayer/Web/Models";
import { LessonRail } from "../Shared/LessonRail.tsx";
import "../Shared/app.css";
import { formatDuration } from "../Shared/format.ts";

export const head = (model: WatchLessonModel) => ({
    title: `${model.lesson.title} · ${model.rail.courseTitle}`,
    links: [{ rel: "icon", href: "/favicon.svg", type: "image/svg+xml" }]
});

export default function Lesson({ model }: ViewProps<WatchLessonModel>) {
    const lesson = model.lesson;
    const missing = lesson.availability !== "Available";

    return (
        <main className="watch-shell">
            <header className="watch-header">
                <a href="/" className="back-link">Library</a>
                <span className="watch-course">{model.rail.courseTitle}</span>
            </header>

            <div className="watch-body">
                <LessonRail rail={model.rail} currentLessonId={lesson.id} />

                <section className="watch-main" aria-labelledby="lesson-heading">
                    <h1 id="lesson-heading">{lesson.title}</h1>

                    {missing ? (
                        <p role="alert" className="missing-note">
                            The source file is unavailable. <a href="/">Refresh library</a> to look for it again.
                        </p>
                    ) : (
                        <div className="player-placeholder" role="img" aria-label="Playback arrives in milestone 2">
                            <p>Playback arrives in milestone 2.</p>
                        </div>
                    )}

                    <dl className="info-grid">
                        <dt>Filename</dt>
                        <dd>{lesson.filename}</dd>
                        <dt>Duration</dt>
                        <dd>{formatDuration(lesson.durationMs)}</dd>
                        {lesson.probe ? (
                            <>
                                <dt>Media</dt>
                                <dd>
                                    {lesson.probe.videoCodec}
                                    {lesson.probe.audioCodec ? ` + ${lesson.probe.audioCodec}` : ""}
                                    {lesson.probe.width && lesson.probe.height ? ` at ${lesson.probe.width}×${lesson.probe.height}` : ""}
                                </dd>
                            </>
                        ) : null}
                        <dt>Subtitles</dt>
                        <dd>
                            {lesson.subtitleCandidateCount > 0
                                ? `${lesson.subtitleCandidateCount} candidate file(s) in this course`
                                : "No subtitle files found in this course"}
                        </dd>
                    </dl>

                    <div className="lesson-nav">
                        {lesson.previousLessonId ? (
                            <a className="lesson-nav-link" href={`/watch/${lesson.previousLessonId}`}>← Previous</a>
                        ) : <span />}
                        {lesson.nextLessonId ? (
                            <a className="lesson-nav-link" href={`/watch/${lesson.nextLessonId}`}>Next →</a>
                        ) : <span />}
                    </div>
                </section>
            </div>
        </main>
    );
}
