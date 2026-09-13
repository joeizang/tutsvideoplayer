import { useEffect, useRef, useState } from "react";
import type { ScanStatusModel } from "dotnet:types/TutsVideoPlayer/Web/Models";

export function RefreshLibraryButton({
    initialScanState,
    initialScanRunId
}: {
    initialScanState: string | null;
    initialScanRunId?: string | null;
}) {
    const [scanState, setScanState] = useState(initialScanState);
    const [error, setError] = useState<string | null>(null);
    const [activeScanRunId, setActiveScanRunId] = useState(initialScanRunId);
    const observing = useRef(false);
    const scanning = scanState === "Running";

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

    const observe = async (scanRunId: string) => {
        if (observing.current) {
            return;
        }

        observing.current = true;
        setError(null);
        setScanState("Running");
        try {
            await pollUntilFinished(scanRunId);
        } catch (caught) {
            setError(caught instanceof Error ? caught.message : "The scan status could not be read.");
            // A transport failure says nothing about the server-side scan. Keep its ID
            // and enable an explicit retry rather than leaving Refresh disabled forever.
            setScanState("Unknown");
        } finally {
            observing.current = false;
        }
    };

    // A scan started by application startup, or by another browser tab, is already running
    // when this page loads. Without this the button would sit disabled at "Scanning…"
    // forever, because polling was only reachable from the disabled button's click handler.
    useEffect(() => {
        if (initialScanState === "Running" && initialScanRunId) {
            void observe(initialScanRunId);
        }
    }, [initialScanState, initialScanRunId]);

    const refresh = async () => {
        if (scanState === "Unknown" && activeScanRunId) {
            await observe(activeScanRunId);
            return;
        }

        setError(null);
        try {
            const response = await fetch("/api/v1/library/scans", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "X-TutsVideoPlayer-Request": "1"
                }
            });
            if (!response.ok) {
                throw new Error(`Request failed with status ${response.status}`);
            }

            const started = (await response.json()) as { scanRunId: string; state: string; joined: boolean };
            setActiveScanRunId(started.scanRunId);
            setScanState(started.state);
            await observe(started.scanRunId);
        } catch (caught) {
            setError(caught instanceof Error ? caught.message : "The scan could not be started.");
            setScanState(initialScanState);
        }
    };

    return (
        <span className="refresh-control">
            <button type="button" onClick={refresh} disabled={scanning}>
                {scanning ? "Scanning…" : scanState === "Unknown" ? "Retry scan status" : "Refresh library"}
            </button>
            {error ? (
                <span role="alert" className="error">{error}</span>
            ) : null}
        </span>
    );
}
