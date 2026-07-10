`llama-completion.exe` is expected to be bundled with the app package in this folder.
At runtime, the app copies the bundled runtime payload to `%LocalAppData%\\Scriblucent\\AiCache\\runtime` and runs it from there.
If bundled runtime files are missing, the app attempts to download runtime payload automatically.
Optional debug override URL: environment variable `SCRIBLUCENT_LLAMA_RUNTIME_URL` (Debug builds only).
