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

### Notes

Give ideas room to grow with rich text, images, linked notes, and templates. Autosave and version history help you pick up where you left off.

[![Rich-text note editor with connected tabs for notes and study materials](Screenshots/editor-note.png)](Screenshots/editor-note.png)

### Flashcards

Turn key ideas into recall practice. Build sets with text, images, and audio, review with spaced repetition, and follow your progress.

[![Flashcard editor showing a grid of cards for recall practice](Screenshots/editor-flashcards.png)](Screenshots/editor-flashcards.png)

### Mind maps

See how ideas connect. Branch out, choose a layout, style your nodes, and link them back to your notes.

[![Mind map connecting retrieval, spaced practice, and related ideas](Screenshots/editor-mindmap.png)](Screenshots/editor-mindmap.png)

### Quizzes

Check what you know with choice, true/false, and written questions. Review attempts and return to the questions you missed.

[![Quiz editor with editable questions, answer choices, and explanations](Screenshots/editor-quiz.png)](Screenshots/editor-quiz.png)

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

1. Download the Windows setup installer for your computer from [Releases](https://github.com/ncyxie/Scriblucent/releases).
2. Choose **x64** for most Windows PCs or **ARM64** for Windows on Arm, then run the installer. The .NET runtime is included.
3. Open Scriblucent and create your first note or study set using the **+** button, or import existing materials.

Scriblucent is currently in beta. If something gets in your way, [report an issue](https://github.com/ncyxie/Scriblucent/issues) with the steps to reproduce it.

<details>
<summary><strong>Build from source</strong></summary>

On Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then run from the repository root:

```powershell
dotnet restore Scriblucent/Scriblucent.csproj
dotnet run --project Scriblucent -c Release
```

</details>

<details>
<summary><strong>Create a release</strong></summary>

Add an entry for the version to [CHANGELOG.md](CHANGELOG.md) for curated release notes. In GitHub Actions, select **Windows release → Run workflow**, enter a version such as `1.0.0-beta.2`, and run it. Alternatively, push a matching version tag:

```powershell
git tag v1.0.0-beta.2
git push origin v1.0.0-beta.2
```

Both methods build and check x64 and ARM64 setup installers, then attach them and SHA-256 checksums to a GitHub release. Versions containing a suffix such as `-beta.2` are marked as prereleases. Clear **Publish a GitHub release** in the manual workflow to build installers without publishing. Ordinary commits do not trigger releases.

Installers are currently unsigned. Setup requests administrator approval and installs for all users under `Program Files\Scriblucent`, with a shared Start menu shortcut. Desktop shortcuts and native file associations are optional. Setup is available in English and Lithuanian. Existing per-user installations are replaced after the new installation verifies successfully; launch setup normally from the account that owns the old copy, then approve the administrator prompt. Opening an associated Scriblucent file imports a copy into your library; the exported original is unchanged. Libraries, settings, media and downloaded models remain separate for each account under `%LOCALAPPDATA%\Scriblucent`, including after uninstall. Interrupted upgrades retain a temporary recovery backup that the next setup run restores before continuing.

</details>

---

[Changelog](CHANGELOG.md) · [MIT License](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md)
