# Project Instructions

## Persistent development branch

- This checkout uses the persistent development branch `codex/pico-preview`; continue ordinary project work on this branch.
- Do not create a task-specific branch or a new Git worktree for routine work in this project.
- Do not switch this checkout to `main`. If another branch is unexpectedly active, inspect its state before taking action and preserve all local changes.
- An explicit user instruction may override these branch rules.

## Unity build boundary

- Do not initiate Unity Player/APK compilation, packaging, or device installation for routine changes.
- After editing, run only relevant non-build validation and tests, then stop.
- Report Unity Player/APK build and device installation as `NOT_RUN` unless the user explicitly requests them in the current task.
