import { useCallback, useEffect, useRef, useState } from "react";
import type { PlaybackManifestModel } from "dotnet:types/TutsVideoPlayer/Web/Models";
import { formatDuration } from "./format.ts";

const HEARTBEAT_MS = 10_000;
const PROGRESS_SAVE_MS = 10_000;
const REQUEST_HEADERS = {
    "Content-Type": "application/json",
    "X-TutsVideoPlayer-Request": "same-origin"
};

type StaleState = "none" | "unsaved" | "taken-over" | "no-session";
type ManualCompletion = "Completed" | "Incomplete" | null;

/**
 * Navigation intent carried in the query string. `autoplay=1` is set when the previous
 * lesson ended with Autoplay next enabled; `replay=1` is set by Replay course, which must
 * start at the beginning rather than restoring a position at the very end of the video.
 */
function readIntent(): { autoplay: boolean; replay: boolean } {
    if (typeof window === "undefined") {
        return { autoplay: false, replay: false };
    }

    const search = new URLSearchParams(window.location.search);
    return { autoplay: search.get("autoplay") === "1", replay: search.get("replay") === "1" };
}

export function VideoPlayer({
    manifest,
    nextLessonId,
    onSettingsChanged
}: {
    manifest: PlaybackManifestModel;
    nextLessonId: string | null;
    onSettingsChanged?: () => void;
}) {
    const videoRef = useRef<HTMLVideoElement>(null);
    const containerRef = useRef<HTMLDivElement>(null);
    const sessionRef = useRef<string | null>(null);
    const sequenceRef = useRef(0);
    const closingRef = useRef(false);
    const resumeOnceRef = useRef(false);
    const replayRef = useRef(false);
    const playIntentRef = useRef(false);
    const autoplayRef = useRef(manifest.preferences.autoplay);

    const [rendition] = useState(
        manifest.renditions.find((candidate) => candidate.id === manifest.readyDefaultRenditionId && candidate.mediaUrl)
        ?? manifest.renditions.find((candidate) => candidate.mediaUrl));
    const [savedPositionMs, setSavedPositionMs] = useState(manifest.progress.positionMs);
    const [playing, setPlaying] = useState(false);
    const [buffering, setBuffering] = useState(false);
    const [currentTimeMs, setCurrentTimeMs] = useState(manifest.progress.positionMs);
    const [durationMs, setDurationMs] = useState(manifest.durationMs ?? 0);
    const [completed, setCompleted] = useState(manifest.progress.effectiveCompletion);
    const [manual, setManual] = useState<ManualCompletion>(
        manifest.progress.manualCompletion === "Completed" || manifest.progress.manualCompletion === "Incomplete"
            ? manifest.progress.manualCompletion
            : null);
    const [completionRevision, setCompletionRevision] = useState(manifest.progress.revision);
    const [completionMessage, setCompletionMessage] = useState<string | null>(null);
    const [settingsRevision, setSettingsRevision] = useState(manifest.preferences.revision);
    const [subtitlesEnabled, setSubtitlesEnabled] = useState(manifest.selection.subtitlesEnabled);
    const [selectedSubtitleId, setSelectedSubtitleId] = useState(manifest.selection.selectedSubtitleId);
    const [subtitlesMenuOpen, setSubtitlesMenuOpen] = useState(false);
    // Candidates are refreshed in place: normalization happens lazily when the browser first
    // requests a track, so states and messages change after this page was rendered.
    const [subtitleCandidates, setSubtitleCandidates] = useState(manifest.subtitles);
    const [subtitleMessage, setSubtitleMessage] = useState<string | null>(null);
    const [speed, setSpeed] = useState(manifest.preferences.speed);
    const [fit, setFit] = useState<"Contain" | "Fill">(manifest.preferences.fitMode === "Fill" ? "Fill" : "Contain");
    const [muted, setMuted] = useState(false);
    const [autoplay, setAutoplay] = useState(manifest.preferences.autoplay);
    const [needsGesture, setNeedsGesture] = useState(false);
    const [stale, setStale] = useState<StaleState>("none");
    const [staleMessage, setStaleMessage] = useState<string | null>(null);

    const currentMediaUrl = rendition?.mediaUrl ?? null;

    // Read once on mount: the server render has no location, so the intent cannot be a prop.
    useEffect(() => {
        const intent = readIntent();
        replayRef.current = intent.replay;
        playIntentRef.current = intent.autoplay || intent.replay;
        if (intent.replay) {
            // Suppress the saved-position restore without touching stored completion history.
            resumeOnceRef.current = true;
            setSavedPositionMs(0);
            const video = videoRef.current;
            if (video) {
                video.currentTime = 0;
            }
        }
    }, []);

    useEffect(() => {
        autoplayRef.current = autoplay;
    }, [autoplay]);

    const attemptIntentPlay = useCallback(() => {
        if (!playIntentRef.current) {
            return;
        }

        playIntentRef.current = false;
        const video = videoRef.current;
        if (!video) {
            return;
        }

        // Browsers may refuse programmatic playback that is not tied to a gesture. The
        // rejection is surfaced as the overlay play button rather than a silently paused page.
        video.play().catch(() => setNeedsGesture(true));
    }, []);

    const writeProgress = useCallback(async (options: { isPlaying: boolean; ended?: boolean; keepalive?: boolean }) => {
        const video = videoRef.current;
        const sessionId = sessionRef.current;
        if (!video) {
            return;
        }

        if (!sessionId) {
            // Silently returning here is what allowed playback to continue for an entire
            // lesson without a single position ever being saved.
            setStale("no-session");
            setStaleMessage("No playback session is active, so your position is not being saved.");
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
                headers: REQUEST_HEADERS,
                body: JSON.stringify(body),
                keepalive: options.keepalive ?? false
            });

            if (response.ok) {
                setStale("none");
                setStaleMessage(null);
                const result = await response.json();
                setCompleted(result.effectiveCompletion);
                // Progress writes share the lesson's revision, so the completion precondition
                // has to follow along or the next manual choice would look stale.
                setCompletionRevision(result.revision);
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
                headers: REQUEST_HEADERS
            });
            if (!response.ok) {
                sessionRef.current = null;
                setStale("no-session");
                setStaleMessage(`A playback session could not be started (status ${response.status}). Your position is not being saved.`);
                return;
            }

            const started = await response.json();
            sessionRef.current = started.sessionId;
            sequenceRef.current = started.lastSequence;
            closingRef.current = false;
            setCompletionRevision(started.progressRevision);

            if (replayRef.current) {
                setSavedPositionMs(0);
            } else {
                setSavedPositionMs(started.savedPositionMs);
                const video = videoRef.current;
                if (video && started.savedPositionMs > 0 && video.readyState >= 1) {
                    resumeOnceRef.current = false;
                    video.currentTime = Math.min(started.savedPositionMs / 1000, Math.max(0, video.duration - 1));
                    resumeOnceRef.current = true;
                }
            }

            setStale("none");
            setStaleMessage(null);
        } catch {
            sessionRef.current = null;
            setStale("no-session");
            setStaleMessage("A playback session could not be started. Your position is not being saved.");
        }
    }, [manifest.lessonId]);

    /**
     * Final flush for navigation and page lifecycle. A keepalive fetch is used rather than
     * sendBeacon because a beacon cannot carry the custom same-origin header the mutation
     * filter requires, so beacon-shaped closes were simply rejected. The server treats an
     * already-closed session as closed, so repeating this is harmless.
     */
    const closeSession = useCallback(() => {
        const sessionId = sessionRef.current;
        if (!sessionId || closingRef.current) {
            return;
        }

        closingRef.current = true;
        const video = videoRef.current;
        sequenceRef.current += 1;
        const body = JSON.stringify({
            sequence: sequenceRef.current,
            positionMs: video ? Math.round(video.currentTime * 1000) : 0
        });

        fetch(`/api/v1/playback-sessions/${sessionId}/close`, {
            method: "POST",
            headers: REQUEST_HEADERS,
            body,
            keepalive: true
        }).catch(() => undefined);

        sessionRef.current = null;
    }, []);

    useEffect(() => {
        void startSession();
        return () => closeSession();
    }, [startSession, closeSession]);

    // Previous/Next, rail links and closing the tab are ordinary full-page navigations, which
    // React effect cleanup does not reliably observe. pagehide fires for all of them, so a
    // lesson change no longer depends on the ten-second timer having happened to fire.
    useEffect(() => {
        const onPageHide = () => closeSession();
        window.addEventListener("pagehide", onPageHide);
        return () => window.removeEventListener("pagehide", onPageHide);
    }, [closeSession]);

    useEffect(() => {
        const heartbeat = window.setInterval(() => {
            const sessionId = sessionRef.current;
            if (!sessionId || document.hidden) {
                return;
            }

            fetch(`/api/v1/playback-sessions/${sessionId}/heartbeat`, {
                method: "POST",
                headers: REQUEST_HEADERS,
                body: JSON.stringify({ activeRenditionId: rendition?.id ?? null })
            }).catch(() => undefined);
        }, HEARTBEAT_MS);

        return () => window.clearInterval(heartbeat);
    }, [rendition?.id]);

    useEffect(() => {
        const progressTimer = window.setInterval(() => {
            if (playing && !document.hidden && stale !== "taken-over" && sessionRef.current) {
                void writeProgress({ isPlaying: true });
            }
        }, PROGRESS_SAVE_MS);

        const onHidden = () => {
            if (document.hidden && stale !== "taken-over" && sessionRef.current) {
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
            attemptIntentPlay();
        }
    }, [resumeToSavedPosition, attemptIntentPlay]);

    const onLoadedMetadata = () => {
        const video = videoRef.current;
        if (!video) {
            return;
        }

        setDurationMs(video.duration > 0 ? Math.round(video.duration * 1000) : 0);
        resumeToSavedPosition();
        attemptIntentPlay();
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
            // The live preference, not the value this page was rendered with: a checkbox
            // toggled during playback has to take effect at the end of this lesson.
            if (autoplayRef.current && nextLessonId) {
                closeSession();
                window.location.href = `/watch/${nextLessonId}?autoplay=1`;
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

    const putSettings = async (update: Record<string, unknown>, revert: () => void) => {
        try {
            const response = await fetch("/api/v1/settings", {
                method: "PUT",
                headers: { ...REQUEST_HEADERS, "If-Match": `"${settingsRevision}"` },
                body: JSON.stringify(update)
            });

            if (response.ok) {
                const result = await response.json();
                setSettingsRevision(result.revision);
                onSettingsChanged?.();
                return;
            }

            if (response.status === 412 || response.status === 428) {
                // Someone else changed settings first. Adopt their values rather than
                // silently replacing them with this tab's stale view.
                const current = await fetch("/api/v1/settings").then((latest) => latest.json());
                setSettingsRevision(current.revision);
                setSpeed(current.playbackSpeed);
                setAutoplay(current.autoplay);
                setFit(current.fitMode === "Fill" ? "Fill" : "Contain");
                return;
            }

            revert();
        } catch {
            revert();
        }
    };

    const setSpeedAndPersist = async (value: number) => {
        const previous = speed;
        setSpeed(value);
        await putSettings({ playbackSpeed: value }, () => setSpeed(previous));
    };

    const setFitAndPersist = async (value: "Contain" | "Fill") => {
        const previous = fit;
        setFit(value);
        await putSettings({ fitMode: value }, () => setFit(previous));
    };

    const setAutoplayAndPersist = async (value: boolean) => {
        const previous = autoplay;
        setAutoplay(value);
        await putSettings({ autoplay: value }, () => setAutoplay(previous));
    };

    const setCompletionChoice = async (choice: "Completed" | "Incomplete" | "Automatic") => {
        setCompletionMessage(null);
        try {
            const response = await fetch(`/api/v1/lessons/${manifest.lessonId}/completion`, {
                method: "PUT",
                headers: { ...REQUEST_HEADERS, "If-Match": `"${completionRevision}"` },
                body: JSON.stringify({ choice })
            });

            if (response.ok) {
                const result = await response.json();
                setCompleted(result.effectiveCompletion);
                setCompletionRevision(result.revision);
                setManual(choice === "Automatic" ? null : choice);
                return;
            }

            if (response.status === 412 || response.status === 428) {
                // Reload the current choice and say so, instead of overwriting whatever the
                // other tab or device just decided.
                const current = await fetch(`/api/v1/lessons/${manifest.lessonId}/completion`).then((latest) => latest.json());
                setCompleted(current.effectiveCompletion);
                setCompletionRevision(current.revision);
                setManual(current.choice === "Completed" || current.choice === "Incomplete" ? current.choice : null);
                setCompletionMessage("This lesson changed elsewhere; the latest choice is shown. Apply yours again if you still want it.");
                return;
            }

            setCompletionMessage(`The completion choice could not be saved (status ${response.status}).`);
        } catch {
            setCompletionMessage("The completion choice could not be saved.");
        }
    };

    type SubtitleCandidate = (typeof manifest.subtitles)[number];

    /**
     * Re-reads the candidate list and returns it. The server owns automatic resolution, so
     * this is also how the player learns which track "Automatic" actually resolved to.
     */
    const refreshSubtitles = async (): Promise<{
        candidates: SubtitleCandidate[];
        resolvedId: string | null;
        message: string | null;
    } | null> => {
        try {
            const response = await fetch(`/api/v1/lessons/${manifest.lessonId}/subtitle-candidates`);
            if (!response.ok) {
                return null;
            }

            const latest = await response.json();
            const candidates = (latest.candidates ?? []) as SubtitleCandidate[];
            setSubtitleCandidates(candidates);
            setSubtitlesEnabled(latest.subtitlesEnabled);
            return {
                candidates,
                resolvedId: latest.resolvedId ?? null,
                message: latest.ambiguityMessage ?? null
            };
        } catch {
            return null;
        }
    };

    const toggleSubtitlesEnabled = async () => {
        try {
            const current = await fetch("/api/v1/settings");
            if (!current.ok) {
                return;
            }

            const currentSettings = await current.json();
            const response = await fetch("/api/v1/settings", {
                method: "PUT",
                headers: { ...REQUEST_HEADERS, "Content-Type": "application/json", "If-Match": `\"${currentSettings.revision}\"` },
                body: JSON.stringify({ subtitleEnabled: !subtitlesEnabled })
            });
            if (response.status === 412 || response.status === 428) {
                window.location.reload();
                return;
            }

            if (!response.ok) {
                return;
            }

            const updated = await response.json();
            setSubtitlesEnabled(updated.subtitleEnabled);
            setSettingsRevision(updated.revision);
        } catch {
            return;
        }
    };

    const selectSubtitle = async (trackId: string | null) => {
        try {
            const response = await fetch(`/api/v1/lessons/${manifest.lessonId}/subtitle-selection`, {
                method: "PUT",
                headers: { ...REQUEST_HEADERS, "Content-Type": "application/json" },
                body: JSON.stringify({ subtitleTrackId: trackId })
            });
            if (!response.ok) {
                return;
            }

            const updated = await response.json();
            setSubtitleMessage(null);

            if (!updated.automatic) {
                setSelectedSubtitleId(updated.subtitleTrackId);
                setSubtitlesMenuOpen(false);
                return;
            }

            // The automatic response carries no track id by design. Asking the server what
            // automatic resolves to keeps an unambiguous adjacent sidecar playing instead of
            // dropping the rendered text track.
            const latest = await refreshSubtitles();
            setSelectedSubtitleId(latest?.resolvedId ?? null);
            setSubtitleMessage(latest?.message ?? null);
            setSubtitlesMenuOpen(false);
        } catch {
            return;
        }
    };

    /**
     * A track element only reports that the browser could not load the cue file. Normalization
     * is lazy, so this is where a malformed sidecar first becomes visible: the candidate list
     * is re-read for the recorded reason and the selection is kept so another file can be
     * chosen from the same menu.
     */
    const onTrackError = async (trackId: string) => {
        const latest = await refreshSubtitles();
        const failed = latest?.candidates.find((candidate) => candidate.id === trackId);
        setSubtitleMessage(
            failed?.message
            ?? "This subtitle file could not be prepared. Choose another from the Subtitles menu.");
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
            case "c":
                event.preventDefault();
                void toggleSubtitlesEnabled();
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

    const secondaryAction = "rounded-md border border-ink/15 bg-surface px-2.5 py-1 text-sm text-ink hover:bg-canvas";

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
                >
                    {subtitlesEnabled && selectedSubtitleId ? (
                        subtitleCandidates
                            .filter((candidate) => candidate.id === selectedSubtitleId && candidate.trackUrl)
                            .map((candidate) => (
                                <track
                                    key={candidate.id}
                                    kind="subtitles"
                                    label={candidate.label}
                                    srcLang={candidate.language ?? undefined}
                                    src={candidate.trackUrl ?? undefined}
                                    onError={() => void onTrackError(candidate.id)}
                                    default
                                />
                            ))
                    ) : null}
                </video>
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
                <div className="relative">
                    <button
                        type="button"
                        onClick={() => setSubtitlesMenuOpen((open) => !open)}
                        className={subtitlesEnabled && selectedSubtitleId
                            ? "rounded-md border border-action/40 bg-action/10 px-2.5 py-1.5 text-sm text-action"
                            : "rounded-md border border-ink/15 bg-surface px-2.5 py-1.5 text-sm text-ink hover:bg-canvas"}
                        title="Subtitles (C)"
                    >
                        {subtitlesEnabled ? (selectedSubtitleId ? "Subtitles ✓" : "Subtitles") : "Subtitles off"}
                    </button>
                    {subtitlesMenuOpen ? (
                        <div className="absolute bottom-full z-10 mb-1 w-72 rounded-lg border border-ink/10 bg-surface p-2 text-sm shadow-lg dark:border-neutral-600 dark:bg-neutral-800">
                            <button
                                type="button"
                                onClick={() => void toggleSubtitlesEnabled()}
                                className="mb-1 w-full rounded px-2 py-1 text-left hover:bg-canvas dark:hover:bg-neutral-700"
                            >
                                {subtitlesEnabled ? "Turn subtitles off (global)" : "Turn subtitles on (global)"}
                            </button>
                            {subtitleCandidates.length === 0 ? (
                                <p className="px-2 py-1 text-ink-soft">No subtitles found for this lesson.</p>
                            ) : (
                                <ul className="m-0 max-h-64 list-none overflow-y-auto p-0">
                                    <li>
                                        <button
                                            type="button"
                                            onClick={() => void selectSubtitle(null)}
                                            className="w-full rounded px-2 py-1 text-left hover:bg-canvas dark:hover:bg-neutral-700"
                                        >
                                            Automatic selection
                                        </button>
                                    </li>
                                    {subtitleCandidates.map((candidate) => (
                                        <li key={candidate.id}>
                                            <button
                                                type="button"
                                                disabled={candidate.state === "Failed" || candidate.state === "Missing"}
                                                onClick={() => void selectSubtitle(candidate.id)}
                                                className={candidate.id === selectedSubtitleId
                                                    ? "w-full rounded bg-action/10 px-2 py-1 text-left text-action disabled:opacity-50"
                                                    : "w-full rounded px-2 py-1 text-left hover:bg-canvas disabled:opacity-50 dark:hover:bg-neutral-700"}
                                            >
                                                <span className="font-medium">{candidate.label}</span>
                                                <span className="ml-1 text-xs text-ink-soft">
                                                    {candidate.reason}
                                                    {candidate.state === "Failed" ? " · could not be converted" : ""}
                                                    {candidate.state === "Missing" ? " · no longer in the library" : ""}
                                                </span>
                                                {candidate.message ? (
                                                    <span className="block text-xs text-ink-soft">{candidate.message}</span>
                                                ) : null}
                                            </button>
                                        </li>
                                    ))}
                                </ul>
                            )}
                        </div>
                    ) : null}
                </div>
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

            {/* All three supported choices stay reachable: the automatic result and the manual
                override are independent, so neither state can hide the action that undoes it. */}
            <div className="mt-2 flex flex-wrap items-center gap-2 text-sm">
                <span
                    className={completed
                        ? "inline-flex items-center gap-1 rounded-md bg-completion/10 px-2 py-1 text-completion"
                        : "inline-flex items-center gap-1 rounded-md bg-ink/5 px-2 py-1 text-ink-soft"}
                >
                    {completed ? "✓ Completed" : "Not completed"}
                    {manual ? ` (manual: ${manual})` : " (automatic)"}
                </span>
                {manual !== "Completed" ? (
                    <button type="button" onClick={() => void setCompletionChoice("Completed")} className={secondaryAction}>
                        Mark completed
                    </button>
                ) : null}
                {manual !== "Incomplete" ? (
                    <button type="button" onClick={() => void setCompletionChoice("Incomplete")} className={secondaryAction}>
                        Mark incomplete
                    </button>
                ) : null}
                {manual !== null ? (
                    <button type="button" onClick={() => void setCompletionChoice("Automatic")} className={secondaryAction}>
                        Use automatic
                    </button>
                ) : null}
                {completionMessage ? (
                    <span role="alert" className="text-danger">{completionMessage}</span>
                ) : null}
            </div>

            <div className="mt-2 flex flex-wrap items-center gap-2 text-sm">
                {stale === "no-session" ? (
                    <span role="alert" className="inline-flex items-center gap-2 text-danger">
                        {staleMessage}
                        <button
                            type="button"
                            onClick={() => void startSession()}
                            className="rounded border border-danger/40 px-2 py-0.5"
                        >
                            Retry saving progress
                        </button>
                    </span>
                ) : null}
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
                {subtitleMessage ? (
                    <span role="alert" className="text-danger">{subtitleMessage}</span>
                ) : subtitlesEnabled && !selectedSubtitleId && subtitleCandidates.length > 0 ? (
                    <span className="text-ink-soft">No subtitle selected. Open the Subtitles menu to choose one.</span>
                ) : null}
            </div>
        </div>
    );
}
