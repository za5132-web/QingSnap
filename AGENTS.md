# QingSnap release conventions

- Every public release must include at least one feature illustration or animated GIF in the release notes. Create it from built-in demo content or synthetic samples, never from private screenshots, clipboard contents, or screenshot history.
- Include the visual in the repository and attach it to the GitHub Release. Use an absolute, version-pinned URL in GitHub Release notes so the image renders there.
- Preserve the existing separate-language README pages; update their latest download links and concise release highlights.
- Build the entire solution in Release, run relevant regression tests, sign with the existing certificate, and provide SHA-256 for the ZIP. State test exclusions honestly.
- A local trial is not authorization to publish. When the user asks to wait for “确认发布”, do not push, tag, publish, or replace public release assets until that confirmation is explicitly given.
- After implementing changes, start the newest local build for the user, preserving settings/history/OCR data and retaining a recoverable executable backup when replacing the running version.
