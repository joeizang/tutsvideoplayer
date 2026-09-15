import { useEffect, useState } from "react";
import type { PreparationJobModel } from "dotnet:types/TutsVideoPlayer/Web/Models";
import { REQUEST_HEADERS } from "./request-headers.ts";

export function QueueJobList({ initialJobs, initialPaused }: { initialJobs: PreparationJobModel[] | null; initialPaused: boolean }) {
    const [jobs, setJobs] = useState<PreparationJobModel[]>(initialJobs ?? []);

    useEffect(() => {
        if (initialJobs !== null) {
            return;
        }

        void (async () => {
            try {
                const response = await fetch("/api/v1/preparations?limit=50");
                if (response.ok) setJobs(await response.json());
                const queue = await fetch("/api/v1/preparations/queue");
                if (queue.ok) setPaused((await queue.json()).paused);
            } catch {
                return;
            }
        })();
    }, [initialJobs]);
    const [paused, setPaused] = useState(initialPaused);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        const timer = window.setInterval(async () => {
            try {
                const response = await fetch("/api/v1/preparations?limit=50");
                if (response.ok) setJobs(await response.json());
                const queue = await fetch("/api/v1/preparations/queue");
                if (queue.ok) setPaused((await queue.json()).paused);
            } catch {
                return;
            }
        }, 2000);

        return () => window.clearInterval(timer);
    }, []);

    const act = async (path: string, method: string, after: () => void) => {
        setBusy(true);
        setError(null);
        try {
            const action = await fetch(path, { method, headers: REQUEST_HEADERS });
            if (!action.ok) { setError("The job changed or the action failed. Refresh the queue and try again."); return; }
            after();
            const response = await fetch("/api/v1/preparations?limit=50");
            if (response.ok) {
                setJobs(await response.json());
            }
        } catch {
            setError("The queue could not be reached. Try again.");
        } finally {
            setBusy(false);
        }
    };

    const togglePause = async () => {
        try {
            const current = await (await fetch("/api/v1/preparations/queue")).json();
            const changed = await fetch("/api/v1/preparations/queue", {
                method: "PUT",
                headers: { ...REQUEST_HEADERS, "Content-Type": "application/json", "If-Match": `"${current.revision}"` },
                body: JSON.stringify({ paused: !current.paused })
            });
            if (!changed.ok) { setError("Queue settings changed. Try again."); return; }
            setError(null);
            setPaused((await changed.json()).paused);
            const response = await fetch("/api/v1/preparations?limit=50");
            if (response.ok) {
                setJobs(await response.json());
            }
        } catch {
            return;
        }
    };

    return (
        <section aria-labelledby="queue-heading" className="rounded-xl border border-ink/10 bg-surface p-4 dark:border-neutral-700 dark:bg-neutral-800">
            <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                <h2 id="queue-heading" className="m-0 text-base font-semibold">Preparation queue</h2>
                <button
                    type="button"
                    onClick={() => void togglePause()}
                    className="rounded-lg border border-ink/15 px-3 py-1.5 text-sm font-semibold hover:bg-canvas dark:border-neutral-600 dark:hover:bg-neutral-700"
                >
                    {paused ? "Resume queue" : "Pause queue"}
                </button>
            </div>

            {error ? <p role="alert" className="text-danger">{error}</p> : null}
            {paused ? (
                <p className="mt-0 text-sm text-ink-soft dark:text-neutral-400">
                    Preparation is paused. Any encode already running will finish; no new jobs start until you resume.
                </p>
            ) : null}

            {jobs.length === 0 ? (
                <p className="m-0 text-sm text-ink-soft dark:text-neutral-400">
                    No preparation jobs. Lessons that need a compatible copy are queued here automatically.
                </p>
            ) : (
                <ul className="m-0 list-none p-0">
                    {jobs.map((job) => (
                        <li key={job.id} className="flex flex-wrap items-center justify-between gap-2 border-b border-ink/10 px-1 py-2 last:border-b-0 dark:border-neutral-700">
                            <span className="min-w-0 truncate text-sm font-semibold">{job.lessonTitle}</span>
                            <span className="text-xs text-ink-soft dark:text-neutral-400">
                                {job.state === "Running" && job.progress != null
                                    ? `Preparing (${Math.round((job.progress ?? 0) * 100)}%)`
                                    : job.state}
                                {job.userMessage ? ` · ${job.userMessage}` : ""}
                            </span>
                            <span className="flex gap-1.5">
                                {job.state === "Queued" || job.state === "Blocked" ? (
                                    <button
                                        type="button"
                                        disabled={busy}
                                        onClick={() => void act(`/api/v1/preparations/${job.id}/prioritize`, "POST", () => undefined)}
                                        className="rounded border border-ink/15 px-2 py-0.5 text-xs dark:border-neutral-600"
                                    >
                                        Prioritize
                                    </button>
                                ) : null}
                                {job.state === "Failed" || job.state === "Blocked" || job.state === "Interrupted" ? (
                                    <button
                                        type="button"
                                        disabled={busy}
                                        onClick={() => void act(`/api/v1/preparations/${job.id}/retry`, "POST", () => undefined)}
                                        className="rounded border border-ink/15 px-2 py-0.5 text-xs dark:border-neutral-600"
                                    >
                                        Retry
                                    </button>
                                ) : null}
                            </span>
                        </li>
                    ))}
                </ul>
            )}
        </section>
    );
}