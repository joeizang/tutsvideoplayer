import { useCallback, useEffect, useRef, useState } from "react";
import type { PlaybackManifestModel } from "dotnet:types/TutsVideoPlayer/Web/Models";
import { formatDuration } from "./format.ts";

const HEARTBEAT_MS = 10_000;
const PROGRESS_SAVE_MS = 10_000;

type StaleState = "none" | "unsaved" | "taken-over";

export function VideoPlayer({
    manifest,
    nextLessonId,
    autoplayNext,
    onSettingsChanged
}: {
    manifest: PlaybackManifestModel;
    nextLessonId: string | null;
    autoplayNext: boolean;
    onSettingsChanged: () => void;
}) {
    const videoRef = useRef<HTMLVideoElement>(null);
    const containerRef = useRef<HTMLDivElement>(null);
    const sessionRef = useRef<string | null>(null);
    const sequenceRef = useRef(manifest.progress.revision >= 0 ? 0 : 0);

    const [rendition] = useState(
        manifest.renditions.find((candidate) => candidate.id === manifest.readyDefaultRenditionId && candidate.mediaUrl)
        ?? manifest.renditions.find((candidate) => candidate.mediaUrl));
    const [savedPositionMs, setSavedPositionMs] = useState(manifest.progress.positionMs);
    const [playing, setPlaying] = useState(false);
    const [buffering, setBuffering] = useState(false);
    const [currentTimeMs, setCurrentTimeMs] = useState(manifest.progress.positionMs);
    const [durationMs, setDurationMs] = useState(manifest.durationMs ?? 0);
    const [completed, setCompleted] = useState(manifest.progress.effectiveCompletion);
    const [speed, setSpeed] = useState(manifest.preferences.speed);
    const [fit, setFit] = useState<"Contain" | "Fill">(manifest.preferences.fitMode === "Fill" ? "Fill" : "Contain");
    const [muted, setMuted] = useState(false);
    const [autoplay, setAutoplay] = useState(manifest.preferences.autoplay);
    const [needsGesture, setNeedsGesture] = useState(false);
    const [stale, setStale] = useState<StaleState>("none");
    const [staleMessage, setStaleMessage] = useState<string | null>(null);

    const currentMediaUrl = rendition?.mediaUrl ?? null;

    const writeProgress = useCallback(async (options: { isPlaying: boolean; ended?: boolean; keepalive?: boolean }) => {
        const video = videoRef.current;
        const sessionId = sessionRef.current;
        if (!video || !sessionId) {
            return;
        }

        sequenceRef.current += 1;
        const body = {
            sequence: sequenceRef.current,
            sourceGeneration: manifest.sourceGeneration,
            positionMs: Math.round(video.currentTime * 1000),
            isPlaying: options.isPlaying,
            ended: options.ended ?? false
        };

        try {
            const response = await fetch(`/api/v1/playback-sessions/${sessionId}/progress`, {
                method: "PUT",
                headers: { "Content-Type": "application/json", "X-TutsVideoPlayer-Request": "same-origin" },
                body: JSON.stringify(body),
                keepalive: options.keepalive ?? false
            });

            if (response.ok) {
                setStale("none");
                setStaleMessage(null);
                const result = await response.json();
                setCompleted(result.effectiveCompletion);
                if (options.ended) {
                    setSavedPositionMs(result.acceptedPositionMs);
                }
            } else if (response.status === 409) {
                setStale("taken-over");
                setStaleMessage("Progress is being recorded from another session.");
            } else {
                setStale("unsaved");
                setStaleMessage(`Position could not be saved (status ${response.status}).`);
            }
        } catch {
            setStale("unsaved");
            setStaleMessage("Your latest position could not be saved.");
        }
    }, [manifest.sourceGeneration]);

    const startSession = useCallback(async () => {
        try {
            const response = await fetch(`/api/v1/lessons/${manifest.lessonId}/playback-sessions`, {
                method: "POST",
                headers: { "X-TutsVideoPlayer-Request": "same-origin" }
            });
            if (!response.ok) {
                setStaleMessage("A playback session could not be started. Progress will not be saved.");
                return;
            }

            const started = await response.json();
            sessionRef.current = started.sessionId;
            sequenceRef.current = started.lastSequence;
            setSavedPositionMs(started.savedPositionMs);
            const video = videoRef.current;
            if (video && started.savedPositionMs > 0 && video.readyState >= 1) {
                resumeOnceRef.current = false;
                video.currentTime = Math.min(started.savedPositionMs / 1000, Math.max(0, video.duration - 1));
                resumeOnceRef.current = true;
            }
            setStale("none");
            setStaleMessage(null);
        } catch {
            setStaleMessage("A playback session could not be started. Progress will not be saved.");
        }
    }, [manifest.lessonId]);

    const closeSession = useCallback((keepalive: boolean) => {
        const sessionId = sessionRef.current;
        const video = videoRef.current;
        if (!sessionId || !video) {
            return;
        }

        const body = JSON.stringify({
            sequence: sequenceRef.current + 1,
            positionMs: Math.round(video.currentTime * 1000)
        });
        if (keepalive) {
            navigator.sendBeacon?.(
                `/api/v1/playback-sessions/${sessionId}/close`,
                new Blob([body], { type: "application/json" }));
        } else {
            fetch(`/api/v1/playback-sessions/${sessionId}/close`, {
                method: "POST",
                headers: { "Content-Type": "application/json", "X-TutsVideoPlayer-Request": "same-origin" },
                body,
                keepalive: true
            }).catch(() => undefined);
        }
        sessionRef.current = null;
    }, []);

    useEffect(() => {
        void startSession();
        return () => closeSession(true);
    }, [startSession, closeSession]);

    useEffect(() => {
        const heartbeat = window.setInterval(() => {
            const sessionId = sessionRef.current;
            if (!sessionId || document.hidden) {
                return;
            }

            fetch(`/api/v1/playback-sessions/${sessionId}/heartbeat`, {
                method: "POST",
                headers: { "Content-Type": "application/json", "X-TutsVideoPlayer-Request": "same-origin" },
                body: JSON.stringify({ activeRenditionId: rendition?.id ?? null })
            }).catch(() => undefined);
        }, HEARTBEAT_MS);

        return () => window.clearInterval(heartbeat);
    }, [rendition?.id]);

    useEffect(() => {
        const progressTimer = window.setInterval(() => {
            if (playing && !document.hidden && stale !== "taken-over") {
                void writeProgress({ isPlaying: true });
            }
        }, PROGRESS_SAVE_MS);

        const onHidden = () => {
            if (document.hidden && stale !== "taken-over") {
                void writeProgress({ isPlaying: false, keepalive: true });
            }
        };

        document.addEventListener("visibilitychange", onHidden);
        return () => {
            window.clearInterval(progressTimer);
            document.removeEventListener("visibilitychange", onHidden);
        };
    }, [playing, stale, writeProgress]);

    useEffect(() => {
        const video = videoRef.current;
        if (video) {
            video.playbackRate = speed;
        }
    }, [speed]);

    const resumeOnceRef = useRef(false);
    const resumeToSavedPosition = useCallback(() => {
        const video = videoRef.current;
        if (!video || resumeOnceRef.current || savedPositionMs <= 0 || video.duration <= 0) {
            return;
        }

        resumeOnceRef.current = true;
        video.currentTime = Math.min(savedPositionMs / 1000, Math.max(0, video.duration - 1));
    }, [savedPositionMs]);

    useEffect(() => {
        const video = videoRef.current;
        if (video && video.readyState >= 1) {
            setDurationMs(video.duration > 0 ? Math.round(video.duration * 1000) : 0);
            resumeToSavedPosition();
        }
    }, [resumeToSavedPosition]);

    const onLoadedMetadata = () => {
        const video = videoRef.current;
        if (!video) {
            return;
        }

        setDurationMs(video.duration > 0 ? Math.round(video.duration * 1000) : 0);
        resumeToSavedPosition();
    };

    const onPlay = () => {
        setPlaying(true);
        setNeedsGesture(false);
    };

    const onPause = () => {
        setPlaying(false);
        void writeProgress({ isPlaying: false });
    };

    const onEnded = () => {
        setPlaying(false);
        void writeProgress({ isPlaying: false, ended: true }).then(() => {
            if (autoplayNext && nextLessonId) {
                window.location.href = `/watch/${nextLessonId}`;
            }
        });
    };

    const seekBy = (seconds: number) => {
        const video = videoRef.current;
        if (!video) {
            return;
        }

        video.currentTime = Math.max(0, video.currentTime + seconds);
        void writeProgress({ isPlaying: !video.paused });
    };

    const togglePlay = async () => {
        const video = videoRef.current;
        if (!video) {
            return;
        }

        try {
            if (video.paused) {
                await video.play();
            } else {
                video.pause();
            }
        } catch {
            setNeedsGesture(true);
        }
    };

    const setSpeedAndPersist = async (value: number) => {
        setSpeed(value);
        await fetch("/api/v1/settings", {
            method: "PUT",
            headers: { "Content-Type": "application/json", "X-TutsVideoPlayer-Request": "same-origin" },
            body: JSON.stringify({ playbackSpeed: value })
        }).catch(() => undefined);
        onSettingsChanged();
    };

    const setFitAndPersist = async (value: "Contain" | "Fill") => {
        setFit(value);
        await fetch("/api/v1/settings", {
            method: "PUT",
            headers: { "Content-Type": "application/json", "X-TutsVideoPlayer-Request": "same-origin" },
            body: JSON.stringify({ fitMode: value })
        }).catch(() => undefined);
        onSettingsChanged();
    };

    const setAutoplayAndPersist = async (value: boolean) => {
        setAutoplay(value);
        await fetch("/api/v1/settings", {
            method: "PUT",
            headers: { "Content-Type": "application/json", "X-TutsVideoPlayer-Request": "same-origin" },
            body: JSON.stringify({ autoplay: value })
        }).catch(() => undefined);
        onSettingsChanged();
    };

    const markCompleted = async () => {
        const response = await fetch(`/api/v1/lessons/${manifest.lessonId}/completion`, {
            method: "PUT",
            headers: { "Content-Type": "application/json", "X-TutsVideoPlayer-Request": "same-origin" },
            body: JSON.stringify({ choice: "Completed" })
        });
        if (response.ok) {
            const result = await response.json();
            setCompleted(result.effectiveCompletion);
        } else if (response.status === 412) {
            window.location.reload();
        }
    };

    const useAutomatic = async () => {
        const response = await fetch(`/api/v1/lessons/${manifest.lessonId}/completion`, {
            method: "PUT",
            headers: { "Content-Type": "application/json", "X-TutsVideoPlayer-Request": "same-origin" },
            body: JSON.stringify({ choice: "Automatic" })
        });
        if (response.ok) {
            const result = await response.json();
            setCompleted(result.effectiveCompletion);
        }
    };

    const onKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
        const target = event.target as HTMLElement;
        if (target.tagName === "INPUT" || target.tagName === "SELECT" || target.tagName === "TEXTAREA") {
            return;
        }

        switch (event.key) {
            case " ":
            case "k":
                event.preventDefault();
                void togglePlay();
                break;
            case "ArrowLeft":
                event.preventDefault();
                seekBy(-10);
                break;
            case "ArrowRight":
                event.preventDefault();
                seekBy(10);
                break;
            case "f":
                if (document.fullscreenElement) {
                    void document.exitFullscreen();
                } else {
                    void containerRef.current?.requestFullscreen();
                }
                break;
            case "m": {
                const video = videoRef.current;
                if (video) {
                    video.muted = !video.muted;
                    setMuted(video.muted);
                }
                break;
            }
        }
    };

    if (!currentMediaUrl) {
        return (
            <div className="flex aspect-video w-full items-center justify-center rounded-lg bg-black text-sm text-neutral-400">
                No playable rendition is ready for this lesson yet.
            </div>
        );
    }

    return (
        <div
            ref={containerRef}
            tabIndex={0}
            onKeyDown={onKeyDown}
            className="outline-none focus-visible:ring-2 focus-visible:ring-action rounded-lg"
        >
            <div className="relative overflow-hidden rounded-lg bg-black">
                <video
                    ref={videoRef}
                    src={currentMediaUrl}
                    preload="metadata"
                    className={fit === "Fill" ? "h-[70vh] w-full object-cover" : "h-[70vh] w-full object-contain"}
                    onLoadedMetadata={onLoadedMetadata}
                    onPlay={onPlay}
                    onPause={onPause}
                    onEnded={onEnded}
                    onWaiting={() => setBuffering(true)}
                    onPlaying={() => setBuffering(false)}
                    onSeeked={() => void writeProgress({ isPlaying: playing })}
                    onTimeUpdate={(event) => setCurrentTimeMs(Math.round(event.currentTarget.currentTime * 1000))}
                />
                {buffering ? (
                    <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
                        <span className="animate-pulse text-sm text-neutral-300">Buffering…</span>
                    </div>
                ) : null}
                {needsGesture ? (
                    <button
                        type="button"
                        onClick={() => void togglePlay()}
                        className="absolute inset-0 flex items-center justify-center bg-black/50 text-lg font-semibold text-white"
                    >
                        ▶ Play
                    </button>
                ) : null}
            </div>

            <div className="mt-2 flex flex-wrap items-center gap-2">
                <button type="button" onClick={() => void togglePlay()} className="rounded-md border border-ink/15 bg-surface px-3 py-1.5 text-sm font-semibold text-ink hover:bg-canvas">
                    {playing ? "Pause" : "Play"}
                </button>
                <button type="button" onClick={() => seekBy(-10)} className="rounded-md border border-ink/15 bg-surface px-2.5 py-1.5 text-sm text-ink hover:bg-canvas">-10</button>
                <button type="button" onClick={() => seekBy(10)} className="rounded-md border border-ink/15 bg-surface px-2.5 py-1.5 text-sm text-ink hover:bg-canvas">+10</button>
                <span className="ml-1 text-sm tabular-nums text-ink-soft">
                    {formatDuration(currentTimeMs)} / {formatDuration(durationMs)}
                </span>
                <input
                    type="range"
                    min={0}
                    max={durationMs > 0 ? durationMs : 1}
                    step={1000}
                    value={Math.min(currentTimeMs, durationMs > 0 ? durationMs : currentTimeMs)}
                    onChange={(event) => {
                        const video = videoRef.current;
                        if (video) {
                            video.currentTime = Number(event.target.value) / 1000;
                        }
                    }}
                    aria-label="Seek"
                    className="mx-2 h-1.5 flex-1 min-w-[8rem] accent-action"
                />
                <select
                    value={speed}
                    onChange={(event) => void setSpeedAndPersist(Number(event.target.value))}
                    aria-label="Playback speed"
                    className="rounded-md border border-ink/15 bg-surface px-2 py-1.5 text-sm text-ink"
                >
                    {[0.5, 0.75, 1, 1.25, 1.5, 1.75, 2].map((value) => (
                        <option key={value} value={value}>{value}×</option>
                    ))}
                </select>
                <button
                    type="button"
                    onClick={() => void setFitAndPersist(fit === "Contain" ? "Fill" : "Contain")}
                    className="rounded-md border border-ink/15 bg-surface px-2.5 py-1.5 text-sm text-ink hover:bg-canvas"
                    title={fit === "Contain" ? "Contain shows the whole picture" : "Fill crops the picture"}
                >
                    {fit === "Contain" ? "Fit" : "Fill"}
                </button>
                <button
                    type="button"
                    onClick={() => {
                        const video = videoRef.current;
                        if (video) {
                            video.muted = !video.muted;
                            setMuted(video.muted);
                        }
                    }}
                    className="rounded-md border border-ink/15 bg-surface px-2.5 py-1.5 text-sm text-ink hover:bg-canvas"
                >
                    {muted ? "Unmute" : "Mute"}
                </button>
                <button
                    type="button"
                    onClick={() => {
                        if (document.fullscreenElement) {
                            void document.exitFullscreen();
                        } else {
                            void containerRef.current?.requestFullscreen();
                        }
                    }}
                    className="rounded-md border border-ink/15 bg-surface px-2.5 py-1.5 text-sm text-ink hover:bg-canvas"
                >
                    Fullscreen
                </button>
                <label className="ml-auto flex items-center gap-1.5 text-sm text-ink-soft">
                    <input
                        type="checkbox"
                        checked={autoplay}
                        onChange={(event) => void setAutoplayAndPersist(event.target.checked)}
                    />
                    Autoplay next
                </label>
            </div>

            <div className="mt-2 flex flex-wrap items-center gap-2 text-sm">
                {completed ? (
                    <span className="inline-flex items-center gap-1 rounded-md bg-completion/10 px-2 py-1 text-completion">
                        ✓ Completed
                        <button type="button" onClick={() => void useAutomatic()} className="underline hover:no-underline">
                            Use automatic
                        </button>
                    </span>
                ) : (
                    <button
                        type="button"
                        onClick={() => void markCompleted()}
                        className="rounded-md border border-completion/40 px-2.5 py-1 text-completion hover:bg-completion/10"
                    >
                        Mark completed
                    </button>
                )}
                {stale === "unsaved" ? (
                    <span role="alert" className="inline-flex items-center gap-2 text-danger">
                        {staleMessage}
                        <button
                            type="button"
                            onClick={() => void writeProgress({ isPlaying: playing })}
                            className="rounded border border-danger/40 px-2 py-0.5"
                        >
                            Retry
                        </button>
                    </span>
                ) : null}
                {stale === "taken-over" ? (
                    <span role="alert" className="inline-flex items-center gap-2 text-danger">
                        {staleMessage}
                        <button
                            type="button"
                            onClick={() => void startSession()}
                            className="rounded border border-danger/40 px-2 py-0.5"
                        >
                            Take over progress here
                        </button>
                    </span>
                ) : null}
                {stale === "none" && staleMessage === null && savedPositionMs > 0 && !playing ? (
                    <span className="text-ink-soft">Resumed at {formatDuration(savedPositionMs)}</span>
                ) : null}
            </div>
        </div>
    );
}