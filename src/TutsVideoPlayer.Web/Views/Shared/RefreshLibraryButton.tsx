import { useState } from "react";
import type { ScanStatusModel } from "dotnet:types/TutsVideoPlayer/Web/Models";

export function RefreshLibraryButton({ initialScanState }: { initialScanState: string | null }) {
    const [scanState, setScanState] = useState(initialScanState);
    const [error, setError] = useState<string | null>(null);
    const scanning = scanState === "Running";

    const refresh = async () => {
        setError(null);
        try {
            const response = await fetch("/api/v1/library/scans", { method: "POST", headers: { "X-TutsVideoPlayer-Request": "same-origin" } });
            if (!response.ok) {
                throw new Error(`Request failed with status ${response.status}`);
            }

            const started = (await response.json()) as { scanRunId: string; state: string; joined: boolean };
            setScanState(started.state);
            await pollUntilFinished(started.scanRunId);
        } catch (caught) {
            setError(caught instanceof Error ? caught.message : "The scan could not be started.");
            setScanState(initialScanState);
        }
    };

    const pollUntilFinished = async (scanRunId: string) => {
        while (true) {
            await new Promise((resolve) => setTimeout(resolve, 2000));
            const response = await fetch(`/api/v1/library/scans/${scanRunId}`);
            if (!response.ok) {
                throw new Error(`Request failed with status ${response.status}`);
            }

            const scan = (await response.json()) as ScanStatusModel;
            setScanState(scan.state);
            if (scan.state !== "Running") {
                window.location.reload();
                return;
            }
        }
    };

    return (
        <span className="inline-flex items-center gap-2">
            <button
                type="button"
                onClick={() => void refresh()}
                disabled={scanning}
                className="rounded-lg border border-ink/15 bg-surface px-3 py-1.5 text-sm font-semibold text-ink hover:bg-canvas disabled:opacity-60 disabled:cursor-wait dark:border-neutral-600 dark:bg-neutral-800 dark:text-neutral-100 dark:hover:bg-neutral-700"
            >
                {scanning ? "Scanning…" : "Refresh library"}
            </button>
            {error ? <span role="alert" className="text-sm text-danger dark:text-red-400">{error}</span> : null}
        </span>
    );
}