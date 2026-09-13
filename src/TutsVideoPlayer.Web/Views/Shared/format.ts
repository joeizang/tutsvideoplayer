export function formatDuration(durationMs: number | null | undefined): string {
    if (durationMs === null || durationMs === undefined || durationMs <= 0) {
        return "unknown";
    }

    const totalSeconds = Math.round(durationMs / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    const padded = (value: number) => value.toString().padStart(2, "0");

    return hours > 0
        ? `${hours}:${padded(minutes)}:${padded(seconds)}`
        : `${minutes}:${padded(seconds)}`;
}
