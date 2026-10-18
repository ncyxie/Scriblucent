<div align="center">
<img src="Scriblucent/Assets/brand-logo-dark.svg" width="220" alt="Scriblucent logo"/>

<h1>Scriblucent</h1>

**Write. Recite. Get it right.**

Learn offline at your own pace. Take notes, build flashcards, map ideas, and test yourself, all in one private space.
</div>

Scriblucent is a Windows desktop app for notes and study materials. Create and edit materials yourself, or use optional local AI to generate them from your notes. AI features require a model download; ordinary editing and study do not.

## Features

- **Notes:** rich text, images, linked notes, templates, version history, and autosave.
- **Flashcards:** editable sets, study sessions, audio, and progress statistics.
- **Mind maps:** connected ideas, multiple layouts, and node styling.
- **Quizzes:** single-choice, multiple-choice, and true/false questions, with attempt history.
- **Organization:** tags, groups, search, manual ordering, and bulk actions.
- **Planning:** calendar reminders and a configurable first day of the week.

Light and dark themes are available in English and Lithuanian. Documents and settings are stored locally.

## Get started

Download Scriblucent from [Releases](https://github.com/ncyxie/Scriblucent/releases). The app runs on Windows 10/11.

Create a note or study set from its dashboard. Edit materials directly, import existing files, or choose a local AI tool when generation is useful.

### Optional local AI

Choose a model in Settings: Qwen3.5-0.8B, Qwen3.5-2B, Qwen3.5-4B, or Qwen3.5-9B. Memory requirements depend on the selected model. Model files are downloaded when needed for an AI feature, rather than on app launch.

Application data is stored under `%LocalAppData%\Scriblucent\`. Downloaded models are stored in `Models` beneath that folder. After downloading a model, generation runs locally without an internet connection.

Attachments are stored in `NotesMedia` and `FlashcardMedia`; installed spell-check dictionaries use `Dictionaries`. Diagnostic logs are grouped under `Logs\AI` and `Logs\Dashboard`.

## Screenshots

| Dark theme | Light theme |
| --- | --- |
| <img src="Screenshots/main-window-dark.png" alt="Dark dashboard"/> | <img src="Screenshots/main-window-light.png" alt="Light dashboard"/> |

## Build and test

Install the .NET 10 SDK on Windows. Run these commands from the repository root:

```powershell
dotnet restore
dotnet build Scriblucent/Scriblucent.csproj -c Release
dotnet run --project Scriblucent
dotnet test Scriblucent.Tests/Scriblucent.Tests.csproj -c Release --filter 'Category!=RealModel&Category!=AiBenchmark'
```

Real-model tests and AI benchmarks are opt-in and may require downloaded models. Build and test sequentially because the application and tool projects share intermediate output.

In Debug builds, `SCRIBLUCENT_QWEN_MODEL_URL` can override the model download URL for local testing.

## Development

Scriblucent uses WPF and MVVM. Views bind to view models; services handle persistence, scheduling, and local AI workers. Documents use local JSON storage.

- Editors share `EditorWorkspaceService` and use isolated working copies. Mark a document saved only after persistence succeeds; preserve dashboard-owned grouping, pinning, and reminders when saving.
- Collection operations act on logical items, including items outside the realized viewport. Keep selection transient and preserve native text-editing shortcuts.
- Dashboard rendering uses viewport virtualization and bounded caches. Avoid loading document content solely to populate hidden controls or previews.
- Use the [design-system guide](Scriblucent/Themes/README.md) for shared controls and rendering rules, and the [runtime UI checks](tools/MotionAudit/README.md) for focused verification.

## Credits

| First name | Last name |
| --- | --- |
| Mantas | Gaižutis |
| Jaunius | Pigaga |
| Rapolas | Turauskas |
| Deividas | Kepenis |
| Gustas | Valaika |
| Mantas | Brūžė |

## License

Scriblucent is distributed under the [MIT License](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for component licenses.
