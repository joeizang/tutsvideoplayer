import type { ViewProps } from "dotnet:rendering";
import type { WatchLessonModel } from "dotnet:types/TutsVideoPlayer/Web/Models";
import { useEffect, useState } from "react";
import { LessonRail } from "../Shared/LessonRail.tsx";
import { VideoPlayer } from "../Shared/VideoPlayer.tsx";
import "/css/app.css";

export const head = (model: WatchLessonModel) => ({
    title: `${model.lesson.title} · ${model.rail.courseTitle}`,
    links: [{ rel: "icon", href: "/favicon.svg", type: "image/svg+xml" }]
});

export default function Lesson({ model }: ViewProps<WatchLessonModel>) {
    const lesson = model.lesson;
    const missing = lesson.availability !== "Available";
    const [manifest, setManifest] = useState(model.manifest);
    const playable = manifest != null && manifest.readyDefaultRenditionId != null;
    const preparingJob = manifest?.renditions.find((rendition) => rendition.preparationJobId !== null);
    const preparing = !playable && preparingJob != null && preparingJob.state !== "Failed"
        && (preparingJob.state === "Queued" || preparingJob.state === "Running"
            || preparingJob.state === "Validating" || preparingJob.state === "Publishing");
    const preparingLabel = preparingJob?.state === "Queued"
        ? "Queued for preparation. You can prioritize it from the library queue."
        : preparingJob?.state === "Blocked" || preparingJob?.state === "Failed"
            ? preparingJob.userMessage ?? "Preparation is not possible for this lesson right now."
            : "Preparing this lesson for playback.";

    // Polling follows the preparation, not playability. In the ordinary case the original
    // is already playable while a better quality is still encoding, and stopping here left
    // the finished version invisible until a manual reload. The manifest is swapped in
    // place: the player keeps its current rendition and session, and the new one simply
    // appears in the quality menu.
    const preparingJobId = preparingJob?.preparationJobId ?? null;
    const preparationActive = preparingJobId !== null
        && (preparingJob!.state === "Queued" || preparingJob!.state === "Running"
            || preparingJob!.state === "Validating" || preparingJob!.state === "Publishing");

    useEffect(() => {
        if (missing || !preparationActive) {
            return;
        }

        const timer = window.setInterval(async () => {
            try {
                const response = await fetch(`/api/v1/lessons/${lesson.id}/playback`);
                if (response.ok) {
                    setManifest(await response.json());
                }
            } catch {
                return;
            }
        }, 2000);

        return () => window.clearInterval(timer);
    }, [missing, preparationActive, preparingJobId, lesson.id]);

    return (
        <main className="flex min-h-screen flex-col bg-canvas text-ink dark:bg-neutral-900 dark:text-neutral-100">
            <header className="flex items-center gap-4 border-b border-ink/10 bg-surface px-5 py-3 dark:border-neutral-700 dark:bg-neutral-800">
                <a href="/" className="font-semibold text-action">Library</a>
                <span className="text-ink-soft dark:text-neutral-400">{model.rail.courseTitle}</span>
            </header>

            <div className="flex flex-1 flex-col lg:flex-row">
                <LessonRail rail={model.rail} currentLessonId={lesson.id} />

                <section className="min-w-0 flex-1 p-6 lg:p-8" aria-labelledby="lesson-heading">
                    <h1 id="lesson-heading" className="mb-4 text-xl font-semibold">{lesson.title}</h1>

                    {missing ? (
                        <p role="alert" className="mb-4 rounded-lg border border-danger/30 bg-surface p-4 text-danger dark:bg-neutral-800">
                            The source file is unavailable. <a href="/" className="underline">Refresh library</a> to look for it again.
                        </p>
                    ) : !playable && preparingJob && !preparing ? (
                        <div role="alert" className="rounded-lg bg-black p-6 text-neutral-300">
                            <p>{preparingJob.userMessage ?? `Preparation is ${preparingJob.state.toLowerCase()}.`}</p>
                            <a href="/" className="text-action">Open the queue to retry or inspect this job</a>
                        </div>
                    ) : preparing ? (
                        <div className="flex aspect-video w-full flex-col items-center justify-center gap-3 rounded-lg bg-black text-center text-sm text-neutral-300">
                            <span>{preparingLabel}</span>
                            <div className="h-1.5 w-56 overflow-hidden rounded bg-neutral-700">
                                <div
                                    className="h-full bg-action transition-all"
                                    style={{ width: `${Math.round((preparingJob?.progress ?? 0) * 100)}%` }}
                                />
                            </div>
                            <a href="/" className="text-action">View library</a>
                        </div>
                    ) : playable && manifest ? (
                        <VideoPlayer
                            manifest={manifest}
                            nextLessonId={lesson.nextLessonId ?? null}
                        />
                    ) : (
                        <div className="flex aspect-video w-full items-center justify-center rounded-lg bg-black p-6 text-center text-sm text-neutral-400">
                            This lesson has no ready playable rendition. Refresh the library or inspect its preparation in the library queue.
                        </div>
                    )}

                    <dl className="mt-6 grid max-w-2xl grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
                        <dt className="font-semibold text-ink-soft dark:text-neutral-400">Filename</dt>
                        <dd className="break-all">{lesson.filename}</dd>
                        <dt className="font-semibold text-ink-soft dark:text-neutral-400">Duration</dt>
                        <dd>{lesson.durationMs ? `${Math.round(lesson.durationMs / 1000)} s` : "unknown"}</dd>
                        {lesson.probe ? (
                            <>
                                <dt className="font-semibold text-ink-soft dark:text-neutral-400">Media</dt>
                                <dd>
                                    {lesson.probe.videoCodec}
                                    {lesson.probe.audioCodec ? ` + ${lesson.probe.audioCodec}` : ""}
                                    {lesson.probe.width && lesson.probe.height ? ` at ${lesson.probe.width}×${lesson.probe.height}` : ""}
                                </dd>
                            </>
                        ) : null}
                        <dt className="font-semibold text-ink-soft dark:text-neutral-400">Subtitles</dt>
                        <dd>
                            {lesson.subtitleCandidateCount > 0
                                ? `${lesson.subtitleCandidateCount} candidate file(s) in this course`
                                : "No subtitle files found in this course"}
                        </dd>
                    </dl>

                    <div className="mt-6 flex max-w-2xl justify-between">
                        {lesson.previousLessonId ? (
                            <a className="font-semibold text-action" href={`/watch/${lesson.previousLessonId}`}>← Previous</a>
                        ) : <span />}
                        {lesson.nextLessonId ? (
                            <a className="font-semibold text-action" href={`/watch/${lesson.nextLessonId}`}>Next →</a>
                        ) : <span />}
                    </div>
                </section>
            </div>
        </main>
    );
}