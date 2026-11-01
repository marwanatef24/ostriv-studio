# Contributing and releases

Build and run the simulated-memory tests as described in the README. Keep research helpers, original-editor binaries, decompiled code, personal profiles, saved games and backup files out of commits and release packages. Do not enable writes for a new game executable without independently observing its layouts and validating it in a disposable settlement; a similar version string or a pattern match is insufficient.

For a release, update the version in `OstrivStudio/OstrivStudio.csproj`, add its notes to `CHANGELOG.md`, update compatibility evidence as needed, and merge the verified changes into `main`. Push a matching tag such as `v0.1.0`. The release workflow checks that the tag matches the project version, runs the tests, builds a self-contained Windows x64 installer with Velopack, and publishes the installer and update-feed files as a GitHub pre-release. The installer and packages are unsigned unless a maintainer configures a trusted signing process.

The updater reads the release feed and displays package notes before the user chooses to download and restart. A release must retain its `releases.win.json` and full `.nupkg` assets for updates to work. If a build or release verification fails, investigate before creating another tag or retrying an upload.
