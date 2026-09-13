import { useState } from "react";
import type { ScanStatusModel } from "dotnet:types/TutsVideoPlayer/Web/Models";

export function RefreshLibraryButton({ initialScanState }: { initialScanState: string | null }) {
    const [scanState, setScanState] = useState(initialScanState);
    const [error, setError] = useState<string | null>(null);
    const scanning = scanState === "Running";

    const refresh = async () => {
        setError(null);
        try {
            const response = await fetch("/api/v1/library/scans", { method: "POST" });
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
        <span className="refresh-control">
            <button type="button" onClick={refresh} disabled={scanning}>
                {scanning ? "Scanning…" : "Refresh library"}
            </button>
            {error ? (
                <span role="alert" className="error">{error}</span>
            ) : null}
        </span>
    );
}
