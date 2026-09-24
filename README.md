<div align="center">
<img src="Scriblucent/Assets/brand-logo-dark.svg" width="220" alt="Scriblucent logo"/>

<h1>Scriblucent</h1>

**Notes. Flashcards. Mind Maps. Quizzes.**

![Platform](https://img.shields.io/badge/Platform-Windows-0078D4?logo=windows)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)
![Language](https://img.shields.io/badge/Language-C%23-239120?logo=csharp)
![AI](https://img.shields.io/badge/AI-LLamaSharp-FF6B35)
![License](https://img.shields.io/badge/License-MIT-green)
</div>

---

**Scriblucent** is a modern Windows desktop application designed to help you study more effectively. Write notes and instantly convert them into flashcards, mind maps, or quizzes using a **local AI model** — no internet connection required after the initial download.

## Why Scriblucent?

Most learning tools either require an internet connection or charge extra for AI features. Scriblucent runs offline after the model is downloaded. All your data stays on your device. You get everything you need — from note-taking to AI-generated quizzes — in one clean, privacy-focused application.

## Screenshots

| Dark Theme | Light Theme |
|------------|-------------|
| <img src="Screenshots/main-window-dark.png"> | <img src="Screenshots/main-window-light.png"> |

## Key Features

- **Notes** — Rich text editor with images, tags, groups, version history, and auto-save
- **Flashcards** — AI generates question-answer cards directly from your notes
- **Mind Maps** — AI creates hierarchical mind maps with 5 layout modes and rich node styling
- **Quizzes** — AI generates quizzes (SingleChoice / MultipleChoice / TrueFalse) with timers, hints, and attempt history
- **Light & Dark themes** with full English and Lithuanian language support

## Supported AI Models

Choose a local model in Settings:

- **Qwen3.5-0.8B** — Basic
- **Qwen3.5-2B** — Compact
- **Qwen3.5-4B** — Recommended
- **Qwen3.5-9B** — Advanced

The selected model is downloaded the first time you use an AI feature, not on application launch.

## Architecture

Scriblucent follows the **MVVM** (Model–View–ViewModel) architecture:

```
View (XAML / WPF)
    │ data binding
    ▼
ViewModel
    │
    ├── Models
    │   NoteDocument · FlashcardSetDocument · MindMapDocument · QuizDocument
    │
    └── Services
          ├── FlashcardConversionService ← AI → Flashcards
          ├── MindMapConversionService  ← AI → Mind Maps
          ├── QuizConversionService     ← AI → Quizzes
          ├── BundledModelHostService   ← Local LLM (user-selected)
          └── NoteFileService / AppSettingsService

Local JSON Storage (%LocalAppData%\Scriblucent\)
```

All data is stored locally in: `%LocalAppData%\Scriblucent\`

## Quick Start

**Requirements:** Windows 10/11 (64-bit), .NET 10, ≥ 4 GB RAM (8 GB+ recommended for AI features)

### Option 1: Download the executable (recommended)
Download the latest `Scriblucent.exe` from the [Releases](https://github.com/ncyxie/Scriblucent/releases) page and run it.

### Option 2: Build from source
```bash
git clone https://github.com/ncyxie/Scriblucent.git
cd Scriblucent
dotnet run --project Scriblucent
```

## How to Use

1. Create a note and enter your content.
2. Click **Flashcards**, **Mind Map**, or **Quiz** — the AI will generate the material automatically.
3. During quizzes, use keyboard shortcuts: `1`–`9` to select an answer, `Enter` to proceed to the next question.

For complete documentation, visit the **[Wiki](../../wiki)**.

## Testing

```bash
dotnet test
```

## Team

| First Name | Last Name  |
|------------|------------|
| Mantas     | Gaižutis   |
| Jaunius    | Pigaga     |
| Rapolas    | Turauskas  |
| Deividas   | Kepenis    |
| Gustas     | Valaika    |
| Mantas     | Brūžė      |

## License

Distributed under the [MIT License](LICENSE).

---

<div align="center"><i>Scriblucent – Dovydo Komanda, 2026</i></div>
