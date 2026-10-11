# Implementation rules

- Develop with VS Code and the .NET CLI. Do not require Visual Studio.
- Keep a fresh-clone, one-command setup working via scripts/setup.ps1. Pin the SDK and verify official download hashes; keep NuGet lock files checked in. Do not make developer-specific paths or ignored investigation tools prerequisites. Document new prerequisites and verify setup changes from an empty .tools directory.
- Use .NET 10 and C# 14. Actively use modern language features where they clarify intent: nullable annotations, records, primary constructors, collection expressions, pattern matching, spans, and async streams. Keep the SDK pinned; do not use an unbounded `preview` language version.
- Keep Windows acquisition, pure NTFS parsing, file assembly, statistics, persistence, and UI separate.
- Treat disk bytes as untrusted. Validate every length, offset, identity reference, and arithmetic operation before use. Never silently turn corruption or missing extents into zero-sized files.
- Preserve file reference sequence numbers, hard-link names, DOS aliases, and named streams. Make deduplication and capacity accounting explicit policies outside the record parser.
- The parser must not mutate caller buffers or retain pooled memory. Avoid unsafe code unless a measured requirement justifies it.
- Test valid binary fixtures, malformed structures, boundary cases, partial reads, cancellation, and deterministic mutation/fuzz input. Pure parser tests must not require elevation or a live volume.
- Use xUnit.net v3 and Microsoft.Testing.Platform. Use AAA and FIRST principles; measure line/branch coverage (targets 95%/90% for the parser) without treating the percentage as proof of correctness. Pass the xUnit test cancellation token to asynchronous work.
- Treat concurrent delete, change, and file-reference reuse as normal scan outcomes. Bound retries, preserve uncertainty, and never infer zero capacity or historical deletion from an unresolved read. Distinguish optimistic observations from point-in-time snapshots.
- Document supported formats, incomplete-file handling, and limitations. Do not claim live-volume support from synthetic tests alone.
- Use `scripts/dotnet.ps1` for local CLI work when the SDK is installed under `.tools/dotnet`. Do not commit SDKs, scan dumps, private file paths, or generated history.
