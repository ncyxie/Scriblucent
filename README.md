<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="Scriblucent/Assets/brand-logo-light.svg">
  <source media="(prefers-color-scheme: light)" srcset="Scriblucent/Assets/brand-logo-dark.svg">
  <img src="Scriblucent/Assets/brand-logo-dark.svg" width="260" alt="Scriblucent">
</picture>

# Write. Recite. Get it right.

Learn offline at your own pace. Take notes, build flashcards, map ideas, and test yourself, all in one private space.

**[Download beta](https://github.com/ncyxie/Scriblucent/releases)** &nbsp; · &nbsp; [Explore the tools](#one-place-to-think-and-learn) &nbsp; · &nbsp; [Report an issue](https://github.com/ncyxie/Scriblucent/issues)

Windows desktop · Local storage · Optional local AI

</div>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="Screenshots/dashboard-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="Screenshots/dashboard-light.png">
  <img src="Screenshots/dashboard-light.png" alt="Scriblucent dashboard with grouped study notes and calendar reminders" width="1280">
</picture>

## One place to think and learn

Start with an idea. Keep your notes and study materials together, then move between them in a shared tabbed workspace.

- **Notes.** Give ideas room to grow with rich text, images, linked notes, and templates. Autosave and version history help you pick up where you left off.
- **Flashcards.** Turn key ideas into recall practice. Build sets with text, images, and audio, review with spaced repetition, and follow your progress.
- **Mind maps.** See how ideas connect. Branch out, choose a layout, style your nodes, and link them back to your notes.
- **Quizzes.** Check what you know with choice, true/false, and written questions. Review attempts and return to the questions you missed.

<table>
  <tr>
    <td width="50%" valign="top">
      <p><strong>Notes</strong></p>
      <a href="Screenshots/editor-note.png"><img src="Screenshots/editor-note.png" alt="Rich-text note editor with connected tabs for notes and study materials" width="640"></a>
    </td>
    <td width="50%" valign="top">
      <p><strong>Flashcards</strong></p>
      <a href="Screenshots/editor-flashcards.png"><img src="Screenshots/editor-flashcards.png" alt="Flashcard editor showing a grid of cards for recall practice" width="640"></a>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <p><strong>Mind maps</strong></p>
      <a href="Screenshots/editor-mindmap.png"><img src="Screenshots/editor-mindmap.png" alt="Mind map connecting retrieval, spaced practice, and related ideas" width="640"></a>
    </td>
    <td width="50%" valign="top">
      <p><strong>Quizzes</strong></p>
      <a href="Screenshots/editor-quiz.png"><img src="Screenshots/editor-quiz.png" alt="Quiz editor with editable questions, answer choices, and explanations" width="640"></a>
    </td>
  </tr>
</table>

## Keep your next step in sight

- **Find your flow.** Organize materials with groups and tags, search your collection, drag items into your own order, and handle selections in bulk.
- **Make room for practice.** Schedule reminders right from your materials and see what is coming up in the dashboard calendar.
- **Make it yours.** Switch between light and dark themes, choose English or Lithuanian, and adjust motion to your preference.

<details>
<summary><strong>See Scriblucent in motion</strong></summary>

A short look at card feedback, the sidebar, and quick actions in the current app.

![Scriblucent's sidebar glides open and closed, a recent card gains a soft blue tint, and the floating plus reveals quick actions](Screenshots/scriblucent-in-motion.gif)

</details>

## Your materials, on your computer

Notes, study materials, and settings are stored locally. Write, organize, and study offline without downloading an AI model.

When you want a starting point, optional local AI can generate flashcards, mind maps, and quizzes from your notes. Choose a model suited to your computer; it downloads when needed and runs locally afterward. Review generated material before studying it.

## Get started

1. Download the Windows build for your computer from [Releases](https://github.com/ncyxie/Scriblucent/releases).
2. Extract the portable download and launch `Scriblucent.exe`. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0#desktop-runtime) if prompted, matching your build's x64 or ARM64 architecture.
3. Create your first note or study set using the **+** button, or import existing materials.

Scriblucent is currently in beta. If something gets in your way, [report an issue](https://github.com/ncyxie/Scriblucent/issues) with the steps to reproduce it.

<details>
<summary><strong>Build from source</strong></summary>

On Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then run from the repository root:

```powershell
dotnet restore Scriblucent/Scriblucent.csproj
dotnet run --project Scriblucent -c Release
```

</details>

---

[MIT License](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md)
