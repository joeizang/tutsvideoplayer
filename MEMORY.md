# Project memory

## Pull request reviews

- For this project, a request to review a GitHub PR includes posting all actionable findings as inline review comments on that PR, unless the user explicitly requests a different destination or a private review. Do not stop at a chat-only review or ask again for permission to post.

## Media preparation

- Frame-timing checks must allow container timestamp quantization and floating-point rounding; verify CFR behavior with MP4, MKV, and WebM fixtures.
- Playback leases must protect both file lifetime and media availability during rendition transitions, including subsequent range requests.
- Refreshing probe metadata must reuse an established source digest when size/mtime hints still match; metadata upgrades alone must not trigger a full-library rehash.
