import { useState } from "react";
import type { ViewProps } from "dotnet:rendering";
import type { SystemInfoModel } from "dotnet:types/TutsVideoPlayer/Web/Models";
import "./Index.css";

export const head = {
    title: "Tuts Video Player",
    links: [{ rel: "icon", href: "/favicon.svg", type: "image/svg+xml" }]
};

export default function Index({ model }: ViewProps<SystemInfoModel>) {
    const [info, setInfo] = useState<SystemInfoModel>(model);
    const [refreshCount, setRefreshCount] = useState(0);
    const [fetching, setFetching] = useState(false);
    const [loadError, setLoadError] = useState<string | null>(null);

    const refresh = async () => {
        setFetching(true);
        setLoadError(null);
        try {
            const response = await fetch("/api/v1/system/info");
            if (!response.ok) {
                throw new Error(`Request failed with status ${response.status}`);
            }
            setInfo(await response.json());
            setRefreshCount((count) => count + 1);
        } catch (error) {
            setLoadError(error instanceof Error ? error.message : "Request failed");
        } finally {
            setFetching(false);
        }
    };

    return (
        <main className="app-shell">
            <header>
                <h1>Tuts Video Player</h1>
                <p className="subtitle">Milestone 0: stack validation</p>
            </header>

            <section aria-labelledby="system-heading">
                <h2 id="system-heading">Host information</h2>
                <dl className="info-grid">
                    <dt>Application</dt>
                    <dd>{info.applicationName}</dd>
                    <dt>Runtime</dt>
                    <dd>{info.runtimeDescription}</dd>
                    <dt>Operating system</dt>
                    <dd>{info.operatingSystem}</dd>
                    <dt>Fixture size</dt>
                    <dd>{info.fixtureAvailable ? `${info.fixtureSizeBytes} bytes` : "fixture missing"}</dd>
                    <dt>Same-origin fetches</dt>
                    <dd>{refreshCount}</dd>
                </dl>
                <button type="button" onClick={refresh} disabled={fetching}>
                    {fetching ? "Fetching…" : "Fetch from API"}
                </button>
                {loadError ? (
                    <p role="alert" className="error">{loadError}</p>
                ) : null}
            </section>

            <section aria-labelledby="playback-heading">
                <h2 id="playback-heading">Native playback and byte-range seeking</h2>
                {info.fixtureAvailable ? (
                    <video className="fixture-video" controls preload="metadata" src={info.fixtureMediaUrl} />
                ) : (
                    <p role="alert">The demo fixture is not available on this host.</p>
                )}
                <p className="hint">Seek in the video: the browser issues Range requests the host answers with 206 partial content.</p>
            </section>
        </main>
    );
}
