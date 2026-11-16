# QingSnap release conventions

- Public release titles must use only `QingSnap vX.Y.Z`, matching previous releases; do not append Chinese or other feature subtitles.
- Every public release must include at least one feature illustration or animated GIF in the release notes. Create it from built-in demo content or synthetic samples, never from private screenshots, clipboard contents, or screenshot history.
- Include the visual in the repository and attach it to the GitHub Release. Use an absolute, version-pinned URL in GitHub Release notes so the image renders there.
- Preserve the existing separate-language README pages; update their latest download links and concise release highlights.
- Build the entire solution in Release, run relevant regression tests, sign with the existing certificate, and provide SHA-256 for the ZIP. State test exclusions honestly.
- A local trial is not authorization to publish. When the user asks to wait for “确认发布”, do not push, tag, publish, or replace public release assets until that confirmation is explicitly given.
- After implementing changes, start the newest local build for the user, preserving settings/history/OCR data and retaining a recoverable executable backup when replacing the running version.
- Use `tools/Start-LocalTrial.ps1` for independent local launches. A scheduled task must explicitly use Priority 4 (Normal); the default 7 runs as a background process. Verify actual Normal/base 8 and, when available, normal memory priority 5 before comparing latency.
- Resolve workspace junctions and record the real storage medium for cold-start investigations. Do not compare a background-priority HDD trial against a normally launched benchmark as if only the application code differed. Keep trial binaries separate from user data and never move history/OCR to fix executable placement.

## Independent translation experiment

- On `experiment/baidu-translation`, use `tools/Start-TranslationTrial.ps1` instead of the stable replacement helper. Never stop or replace the installed QingSnap process. Keep the version unchanged; isolate state, credentials, mutex, logs and global hotkeys.
- A user-approved `--enable-global-hotkeys` session may register the trial's existing bindings after the stable application has exited. Default launches remain hotkey-isolated. Restart only idle trial processes, never close unsaved work or alter stable settings/startup.
- Do not send real translation traffic from tests, startup, typing, or clipboard polling. Real connection tests require a user's explicit click and their own credentials and consent. Never print or commit credentials or source text.
