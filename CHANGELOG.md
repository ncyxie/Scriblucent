# Scriblucent releases

## 1.0.0-beta.3

- Windows setup now installs for all users in Program Files, safely converts existing per-user installations, and provides English and Lithuanian setup text.
- Upgrade recovery restores program files and Windows integration after cancellation or failure, with retained recovery state for interrupted installs.
- Smaller Windows installations with only English and Lithuanian language resources, clean AI worker publishing, and complete third-party notices.
- Optional native file associations: opening an exported note, flashcard set, mind map, or quiz imports a library copy and opens its editor without modifying the original file.
- Single-instance library protection and file handoff to the running app.
- Opening a written-answer quiz no longer falsely marks it as edited or triggers an unnecessary save prompt.
- Safer legacy data migration, upgrade and downgrade handling, and remembered setup choices.
- Setup follows Windows light/dark mode, retains user libraries and models during uninstall, and verifies the AI CPU backend on x64 and ARM64.

## 1.0.0-beta.2

<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/ncyxie/Scriblucent/v1.0.0-beta.2/Scriblucent/Assets/brand-logo-light.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/ncyxie/Scriblucent/v1.0.0-beta.2/Scriblucent/Assets/brand-logo-dark.svg">
  <img src="https://raw.githubusercontent.com/ncyxie/Scriblucent/v1.0.0-beta.2/Scriblucent/Assets/brand-logo-dark.svg" width="240" alt="Scriblucent">
</picture>

### Write. Recite. Get it right.

**A new chapter for NoteCards.**

</div>

Scriblucent builds on the original NoteCards with a substantial rewrite of its interface and daily workflows. Write, connect ideas, and practice recall in one Windows workspace. Your materials stay local, and AI is optional.

<div align="center">
<a href="https://raw.githubusercontent.com/ncyxie/Scriblucent/v1.0.0-beta.2/Screenshots/dashboard-light.png">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/ncyxie/Scriblucent/v1.0.0-beta.2/Screenshots/dashboard-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/ncyxie/Scriblucent/v1.0.0-beta.2/Screenshots/dashboard-light.png">
  <img src="https://raw.githubusercontent.com/ncyxie/Scriblucent/v1.0.0-beta.2/Screenshots/dashboard-light.png" width="680" alt="Scriblucent dashboard with grouped notes and a planning calendar. Click to view at full resolution.">
</picture>
</a>
</div>

### Four tools, one connected workspace

| Create | Make it useful |
| --- | --- |
| **Notes** | Write with rich text, images, checklists, tables, and sketches. Link notes, reuse templates, and return to earlier versions. |
| **Flashcards** | Build cards with text, images, and audio. Choose SM-2 or FSRS-6 spaced repetition, use Cram mode, and follow your progress. |
| **Mind maps** | Connect ideas, arrange branches, style nodes, and link them to your notes. |
| **Quizzes** | Mix choice, true/false, and written questions, then review attempts and missed answers. |

### An interface that feels alive

Light and dark themes, soft depth, cursor-responsive card tints, and coordinated motion give the dashboard and editors one visual language. Tabs keep your materials together, smooth caret movement makes writing more fluid, and reduced motion offers a quieter experience.

<details>
<summary><strong>See it in motion</strong></summary>

![Card feedback, the sliding sidebar, and quick actions in Scriblucent](https://raw.githubusercontent.com/ncyxie/Scriblucent/v1.0.0-beta.2/Screenshots/scriblucent-in-motion.gif)

</details>

### Less friction in the daily work

- **Organize your way.** Group and tag materials, search your collection, and drag cards into a manual order.
- **Work in batches.** Select multiple items for common actions, including bulk renaming and export where supported.
- **Review across topics.** Combine flashcard sets or quizzes into a broader study set or test.
- **Keep practice in view.** Plan reminders in the dashboard calendar and choose the first day of your week.
- **Bring your materials along.** Import RTF and text notes, CSV or Anki plain-text flashcards, and GIFT or CSV quizzes. Export notes to PDF or selected materials together in a ZIP archive.
- **Make it yours.** Choose English or Lithuanian and adjust motion to your preference.

### Local first, AI when you want it

Write, organize, and study offline without an AI model. Optional local AI can turn notes into draft flashcards, mind maps, and quizzes. Models download when you choose them and run locally afterward. Review generated material before studying it.

### Ready for Windows

Setup installers are available for **x64** and **ARM64**, with the required runtimes included. Optional models download separately. This is a beta release; [report issues](https://github.com/ncyxie/Scriblucent/issues) with the steps to reproduce them.

**[Watch the current workflow demo](https://github.com/ncyxie/Scriblucent#readme)** · [Explore the tools](https://github.com/ncyxie/Scriblucent#one-place-to-think-and-learn) · [Download the latest beta](https://github.com/ncyxie/Scriblucent/releases)
