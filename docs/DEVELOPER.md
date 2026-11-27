# Ilham — Developer & Customization Guide

This document explains **every file** in the project, how the pieces fit, and how to customize or extend each
one. It's written for someone who wants to change how Ilham looks, feels, thinks, or behaves.

If you only want to *run* her, see the [README](../README.md). This guide is for changing her.

---

## 1. The big picture

Ilham is a .NET solution with a strict dependency direction. Everything flows **downward** — nothing in
`Ilham.Core` knows about any UI:

```
Ilham.Desktop (Avalonia app)  ─┐
Ilham.App (optional web server) ┼─► Ilham.Core  ◄── Ilham.Rendering (SkiaSharp avatar)
tests/Ilham.Tests ─────────────┘        │
                                   ilham.db (SQLite on disk)   +   Ollama (local model)
```

- **`Ilham.Core`** — all the logic: emotions, memory, vault, guardian, commands, the model client, persona,
  initiative, privacy, training. No UI. Fully unit-tested. **This is where her *behaviour* lives.**
- **`Ilham.Rendering`** — her face, drawn with SkiaSharp. Pure drawing, no UI framework. Shared by the desktop
  app and the tests (which render her to PNG).
- **`Ilham.Desktop`** — the real app: an Avalonia window (Windows now, cross-platform ready).
- **`Ilham.App`** — an *optional* ASP.NET Core server that exposes the same Core over HTTP, so a phone browser
  can reach her. Not the main app; keep it or delete it.
- **`tests/Ilham.Tests`** — xUnit tests for Core logic, the avatar renderer, and the web API.

**Golden rule when extending:** put logic in `Ilham.Core` (so it's testable and UI-independent), and let the
desktop/web layers only *call* it and *display* the result.

---

## 2. Build, run, test

```bash
dotnet build                                   # everything
dotnet test                                    # 73 tests
dotnet run --project src/Ilham.Desktop -c Release   # the native app
dotnet run --project src/Ilham.App -c Release       # the optional phone server (http://localhost:8787)
```

See the rendered avatar faces without a GUI:
```bash
ILHAM_FACE_OUT=./faces dotnet test --filter AvatarRenderTests   # writes faces/face-*.png
```

**Target framework** is chosen automatically from your installed SDK (`Directory.Build.props`) — .NET 8, 10 or
11 all work. Force one with `-p:IlhamTfm=net8.0`.

---

## 3. Root files

| File | What it is | Customize |
|---|---|---|
| `Ilham.sln` | The solution (lists all projects). | Add new projects with `dotnet sln add`. |
| `Directory.Build.props` | Shared MSBuild config: picks the target framework from your SDK version and sets `RollForward=Major`. | Pin a framework by editing `IlhamTfm`; see the comments inside. |
| `.gitignore` | Ignores build output, her local `*.db`, VRM models, training artifacts. | Add anything you don't want committed. |
| `.github/workflows/ci.yml` | GitHub Actions: builds + tests on Ubuntu and Windows on every push. | Add deploy/release steps here. |
| `README.md` | User-facing intro and quick start. | — |
| `docs/` | This guide, plus `ARCHITECTURE.md`, `BOUNDARIES.md`, `PHONE.md`, `AVATAR.md`. | — |

---

## 4. `Ilham.Core` — her brain and behaviour

This is the project you'll touch most. Every file is pure C# with no UI dependency, so every change here is
unit-testable.

### `Ilham.Core.csproj`
Project config: `net*` (auto), nullable on, **docs required** and **warnings-as-errors** (so every public
member must have an XML doc comment — keep that discipline if you add public types), `AllowUnsafeBlocks` (for
the P/Invoke in `PcActions`), and `InternalsVisibleTo=Ilham.Tests`.
*Customize:* add NuGet packages here; it already references `Microsoft.Data.Sqlite`.

### `Emotions/EmotionEngine.cs`  (her feelings)
- **`enum Mood`** — the nine moods (`Neutral, Happy, Loving, Teasing, Excited, Worried, Annoyed, Angry, Sad`).
  These names are the contract used everywhere: the avatar, the persona tags, the reply parser.
- **`record Feeling`** — one mood change with its reason and timestamp.
- **`class EmotionEngine`** — holds an intensity in `[0,1]` for every mood. `Feel(mood, amount, reason)` raises
  one (and softens its opposites); intensities **decay toward a resting baseline** over time (exponential,
  `HalfLife = 40 min`). `Dominant` is her strongest mood; `Describe()` is the text fed into her prompt.
- **Customize her temperament:** edit the `Baseline` dictionary (e.g. make her more `Teasing` at rest), the
  `HalfLife` (how fast she calms down), or the `Opposites` map (which feelings cancel each other).
- **Add a mood:** add it to `enum Mood`, give it a `Baseline`, then add its expression in
  `Ilham.Rendering/AvatarState.cs` and mention it in the persona protocol (see §8).

### `Memory/MemoryStore.cs`  (what she remembers)
- **Records:** `Fact`, `Occasion`, `ChatLine`.
- **`class MemoryStore`** — a single SQLite file (`ilham.db`). Tables: `facts`, `occasions`, `messages`,
  `settings`. Facts are de-duplicated by a normalised form. **`Search(query, k)`** ranks facts by **TF-IDF
  cosine similarity** so her prompt only gets relevant memories. `Get<T>/Set<T>` store any JSON setting.
  `ForgetEverything()` wipes memory but keeps settings. `Tokenize` + `StopWords` back the search.
- **Customize recall:** tune `k` where `Search` is called (in `IlhamBrain`), extend `StopWords`, or replace
  TF-IDF with embeddings (swap the body of `Search`; keep the signature).
- **Reset her:** delete `%LocalAppData%/Ilham/ilham.db` or call `ForgetEverything()`.

### `Vault/SecretVault.cs`  (her encrypted secrets)
- **`record VaultEntry`** (title/username/note — **never** the secret).
- **`class SecretVault`** — AES-256-GCM, key from **PBKDF2-SHA256 (300k iters)** of a master password. The
  master password is never stored; forget it and the secrets are unrecoverable. `Initialize/Unlock/Lock/Put/
  List/Reveal/Delete`. **By design there is no "import from browser/OS" method** (a test enforces this).
- **Customize:** raise the iteration count; add fields to `VaultEntry` (not the secret). Do **not** add any
  scraping/import path — that's the line in `docs/BOUNDARIES.md`.

### `Guardian/HealthGuardian.cs`  (the overprotective part)
- **Enums** `Concern` (Break/Bedtime/DailyLimit), `GuardianAction` (None/Lock/Shutdown).
- **Records** `GuardianAlert` (one escalation step), `GuardianStatus` (for the UI).
- **`class HealthGuardian`** — fed a `Tick(profile, idle)` ~every 30 s. Tracks **real active minutes** (idle
  time doesn't count), notices breaks, and escalates a concern step by step (1 gentle → 5 acting) every
  `EscalateEveryMinutes`. At level 4–5 it returns a `Lock`/`Shutdown` action (if allowed) with a countdown.
  `Snooze` backs off (and annoys her). `IsBedtime` handles windows crossing midnight.
- **Customize:** change thresholds (`IdleThreshold`, `BreakLength`, `EscalateEveryMinutes`), the escalation
  curve, or the canned fallback lines in `FallbackLine` (used when the model is offline).

### `Guardian/PcActions.cs`  (what she can do to the PC)
- **`interface IPcActions`** — `Idle()`, `Lock()`, `ScheduleShutdown()`, `CancelShutdown()`, `Log`.
- **`PcActions.ForCurrentOs()`** → `WindowsPcActions` (real: P/Invoke `GetLastInputInfo`/`LockWorkStation`,
  `shutdown.exe`) on Windows, else `SimulatedPcActions` (logs only — used on Linux/macOS dev and tests).
- **Customize / port:** to support lock/idle on Linux or macOS, add a new `IPcActions` implementation and
  return it from `ForCurrentOs()`.

### `Commands/CommandRunner.cs`  (running things, with approval)
- **`enum Risk`** (Safe/Caution/Dangerous), records `ProposedCommand`, `CommandResult`.
- **`CommandSafety.Classify`** — regex rules rate a command; the **worst** match wins. This is what makes
  destructive commands warn louder.
- **`CommandRunner.RunAsync`** — runs `cmd`/`powershell`/`bash` with a timeout, captures output (capped at
  `MaxOutput`). **Nothing runs here until the UI approves it** (the brain only *queues* commands).
- **Customize:** add/adjust rules in `CommandSafety.Rules`; change `MaxOutput` or the timeout passed in.

### `Llm/LlmClients.cs`  (her connection to the model)
- **`record ChatMessage`**, **`interface ILlmClient`** (`StreamAsync`, `CheckAsync`).
- **`OllamaClient`** — streams from a local Ollama server's `/api/chat`; `CheckAsync` hits `/api/tags` to see
  if the model is installed. Options (temperature, context size) are set here.
- **`ScriptedLlmClient`** — a deterministic stand-in used in **demo mode** and tests, so everything works with
  no GPU.
- **Customize the model's feel:** edit the `options` in `OllamaClient.StreamAsync` (temperature, `num_ctx`,
  `repeat_penalty`). **Use a different backend** (llama.cpp server, LM Studio, a cloud API): implement
  `ILlmClient` and swap it in `IlhamServices`.

### `Persona/PersonaBuilder.cs`  (who she is)
- **`Character`** (const) — her core personality prompt (stubborn, sarcastic, Urdu/English, protective) **and
  the boundaries she keeps**.
- **`Protocol`** (const) — the action-tag format she must use (`[mood:…]`, `[remember:…]`, `[occasion:…]`,
  `[run:…]`).
- **`Build(...)`** — assembles the full system prompt each turn: character + how she feels + relevant memories
  + upcoming occasions + health status.
- **`GuardianInstruction(...)`** — turns a health alert into a one-off "say this now" instruction.
- **Customize her personality:** this is the main knob. Edit `Character` (tone, language, limits). If you add
  a new action tag, add it to `Protocol` **and** parse it in `ReplyParser`.

### `Persona/ReplyParser.cs`  (reading her tags)
- **Records** `CommandRequest`, `OccasionRequest`, `ParsedReply`.
- **`ReplyParser.Parse`** — extracts the `[…]` action tags, returns clean visible text + mood + memories +
  occasions + commands. **`VisiblePrefix`** hides half-typed tags while she streams so you never see raw tags
  flicker.
- **Customize:** to add a new tag type, extend `TagRegex` handling here and add a field to `ParsedReply`, then
  handle it in `IlhamBrain.Apply`.

### `Initiative/InitiativeEngine.cs`  (she talks first)
- **`enum InitiativeKind`** (CheckIn/Tease/Impatient/Demand/Sulk/Spark).
- **`class InitiativeEngine`** — `NoteUserActivity()` / `NoteSpoke()` track silence; `Consider(profile,
  atKeyboard)` decides whether she speaks unprompted and in what tone, escalating the longer she's ignored
  (and being ignored raises her annoyance). `Cooldown` stops spam.
- **Customize:** change the silence thresholds and escalation in `Consider`, or the lines/instructions in
  `Build`. Turn the whole thing off via `UserProfile.Proactive`.

### `Perception/PerceptionInterpreter.cs`  (reading your face/voice)
- **`record PerceptionReport`** — the small summary the UI sends after reading *your* webcam/mic on-device.
- **`PerceptionInterpreter.ToFeelings`** — maps that to emotional nudges (you smile → she's happy; you look
  tired → she worries).
- **Customize:** change the mapping/weights. The heavy ML stays in the UI; this just interprets a summary.

### `Privacy/PrivacyGuard.cs`  (hide from anyone who isn't you)
- **Enums** `PresenceSignal` (Owner/NotOwner/NoFace/CameraOff), `Visibility` (Active/Hidden).
- **`class PrivacyGuard`** — turns a stream of coarse owner-presence readings into a show/hide decision,
  debounced so a glance away won't hide her and an empty chair is never an "intruder". **It carries no data
  about any other person** — only a signal in and a show/hide out (a test enforces this).
- **`interface IOwnerRecognizer`** — the seam you implement on-device: read a frame, return only
  owner/not-owner/no-face. **This is the piece to build** for the webcam privacy lock (see §8).
- **Customize:** tune `HideAfterReadings`/`ShowAfterReadings`. Opt-in via `UserProfile.HideWhenNotOwner`.

### `Occasions/OccasionCalendar.cs`  (birthdays & dates)
- **`record UpcomingOccasion`**, **`OccasionCalendar.Upcoming/Next`** — works out which saved dates (and your
  birthday) are today/soon, with age for birthdays and a Feb-29 fallback.
- **Customize:** change how far ahead she looks (the `<= 14` filter is in `PersonaBuilder.Build`).

### `Profile/UserProfile.cs`  (all her settings)
- **`record UserProfile`** — name, birthday, bedtime/wake, break & daily limits, allow-lock/shutdown, Ollama
  URL + model, language, `Proactive`, `HideWhenNotOwner`.
- **`class ProfileStore`** — load/save the profile (as a JSON setting in the DB) and the **override password**
  (PBKDF2-hashed, constant-time check).
- **Customize / add a setting:** add a property here (give it a default), then surface it in the desktop
  settings view and/or the web `settings.js`.

### `Training/TrainingExporter.cs`  (train her on your chats)
- **`TrainingExporter.BuildJsonl`** — turns her stored conversation into a fine-tuning dataset (messages
  format), each example prefixed with her persona.
- **`ExportToFile`** writes it; **`TrainingRecipe`** returns a ready-to-run Unsloth LoRA script.
- **Customize:** change `windowTurns`, the dataset format, or the base model / hyper-parameters in the recipe.

### `IlhamBrain.cs`  (orchestration — ties it all together)
- **`record ReplyChunk`** — a streamed piece of her reply (text delta + mood + final parsed result).
- **`class IlhamBrain`** — the heart:
  - `RespondAsync(userText)` — logs the message, builds context (persona + memory + history), streams the
    model, hides tags while streaming, then `Apply`s the result (saves memories/dates, nudges mood, **queues**
    commands for approval).
  - `SpeakGuardianAsync` / `SpeakUnpromptedAsync` — she speaks a health nudge or an initiative, in voice;
    falls back to a canned line if the model is offline.
  - `ApproveAsync(id)` / `Reject(id)` / `Pending` — the command-approval gate.
- **Customize:** this is the conversation flow. Change how much history/memory goes into the prompt
  (`BuildContext`, `SystemPrompt`), or what happens to parsed results (`Apply`).

### `IlhamServices.cs`  (composition root)
- **`class IlhamServices`** — builds **all** of the above around one `dataDir` and an `HttpClient`. Any
  front-end constructs one of these and drives it. `ActiveBrainAsync()` returns the real brain if the model is
  reachable, else a demo brain. `PersistCounters()` saves screen-time.
- **Customize:** this is where you'd swap `ILlmClient`, change the data directory, or add a new service.

---

## 5. `Ilham.Rendering` — her face

### `AvatarState.cs`
- **`class AvatarState`** — the animated state: eases toward the current mood's `Expression`, blinks, moves
  gaze, breathes, and smooths mouth openness for lip-sync. `SetMood`, `SetAmplitude`, `LookAt`, `Step(dt)`.
- **`readonly record struct Expression`** + the **`Table`** — the nine moods' parameters (brow, browAngle,
  eye, smile, blush, tilt, hue).
- **Customize her expressions:** edit the numbers in `Expression.Table`. To add a mood, add a row here (and to
  `enum Mood`).

### `AvatarRenderer.cs`
- **`AvatarRenderer.Draw(canvas, size, state, background)`** — draws the whole face with SkiaSharp: hair,
  skin, kameez + dupatta, eyes/brows/nose/mouth, blush. `Hsl` is a colour helper.
- **Customize her look:** the colour variables near the top of `Draw` (`skin`, `hair`, `kameez`, `dupatta`)
  and the path coordinates. This is where her styling (warm skin, long dark hair, mustard kameez) lives.
- **Verify changes instantly:** `ILHAM_FACE_OUT=./faces dotnet test --filter AvatarRenderTests` renders every
  mood to a PNG.

### `Ilham.Rendering.csproj`
References `SkiaSharp`. Cross-platform; the Linux native assets come in via the test project for headless
rendering.

---

## 6. `Ilham.Desktop` — the native app (Avalonia)

### `Program.cs`
Avalonia entry point (`Main`, `BuildAvaloniaApp`). Rarely changes.

### `App.axaml` / `App.axaml.cs`
The application + theme (Fluent dark). `OnFrameworkInitializationCompleted` builds the `MainViewModel` (pointing
at `%LocalAppData%/Ilham`) and the `MainWindow`.
- **Customize:** data directory, theme variant.

### `Controls/AvatarControl.cs`
A custom Avalonia `Control` that owns an `AvatarState`, ticks it ~60 fps with a `DispatcherTimer`, and paints
it by leasing a real `SKCanvas` and calling `AvatarRenderer.Draw`. Exposes `SetMood`, `SetAmplitude`, `LookAt`.
- **Customize:** swap this for a 3D (VRM) renderer later — keep the same three methods and the rest of the app
  is unchanged (see `docs/AVATAR.md`).

### `ViewModels/MainViewModel.cs`
The window's brain-facing layer:
- `ChatLineVm`, `PendingCommandVm` — bindable items.
- `MainViewModel` — owns `IlhamServices`, exposes `Messages`, `Pending`, `Mood`, `Status`, `Draft`. `SendAsync`
  streams her reply onto the UI thread. A `DispatcherTimer` **heartbeat** (every 8 s) runs the guardian tick
  (and acts on lock/shutdown) and her initiative. `ApproveAsync`/`Reject` gate commands. `MoodChanged` drives
  the avatar.
- **Customize:** heartbeat interval, how streaming maps to bubbles, wiring new Core features to the UI.

### `ViewModels/Converters.cs`
Tiny value converters for the chat template (bubble alignment and colour by speaker).

### `Views/MainWindow.axaml` / `.axaml.cs`
The layout: her face on the left, the conversation + command cards + input on the right. The code-behind wires
button/Enter events to the view model and the view model's `MoodChanged`/`Amplitude` to the `AvatarControl`.
- **Customize the UI:** colours live in `Window.Resources`; add a settings panel, a portrait image, etc. here.
- **Add a settings screen:** this is where you'd add it (bind to `UserProfile` via the view model).

### `app.manifest`, `Ilham.Desktop.csproj`
Windows DPI manifest; project refs Avalonia + Core + Rendering. `ImplicitUsings` on, warnings-as-errors off
(Avalonia generates code).

---

## 7. `Ilham.App` — optional phone-access server

Keep this only if you want to reach her from a phone browser. It exposes the **same Core** over HTTP; it is not
the main app.

- **`Program.cs`** — a minimal API: `/api/chat` (Server-Sent Events), command approve/reject, `/api/perceive`,
  `/api/state`, profile, memory, vault, model-status, and `/api/initiative`. All local-only.
- **`IlhamHost.cs`** — the web equivalent of `IlhamServices` (predates it; could be refactored to use it).
- **`GuardianLoop.cs`** — a 30-second `BackgroundService` heartbeat that acts on health alerts.
- **`wwwroot/`** — the browser UI: `index.html`, `app.css`, `app.js` (chat + SSE + initiative + wander),
  `avatar.js` (the **browser** version of her face — the canvas twin of the SkiaSharp renderer), `voice.js`
  (speech in/out + lip-sync), `perception.js` (webcam emotion read), `settings.js` (settings/vault/memory UI).
- **`appsettings*.json`, `Properties/launchSettings.json`** — config and dev ports.
- **Customize:** add an endpoint in `Program.cs` and a matching call in the relevant `wwwroot/*.js`. The web
  `avatar.js` must be restyled separately from the SkiaSharp one if you want them to match.

> If you're going desktop-only, you can delete `Ilham.App` and its test (`WebTests.cs`) and drop the
> `Microsoft.AspNetCore.Mvc.Testing` reference. The desktop app doesn't need any of it.

---

## 8. Common customizations (step by step)

**Change her personality / language.** Edit `Persona/PersonaBuilder.cs` → `Character`. That single string
controls tone, language mix, and the limits she states. Re-run and talk to her.

**Make her moodier / calmer.** `Emotions/EmotionEngine.cs` → `Baseline` (resting feelings) and `HalfLife`
(how fast she settles).

**Restyle her face.** `Ilham.Rendering/AvatarRenderer.cs` → the colour variables at the top of `Draw` (skin,
hair, kameez, dupatta). Verify with `ILHAM_FACE_OUT=./faces dotnet test --filter AvatarRenderTests`.

**Add a new mood.** (1) add to `enum Mood` in `EmotionEngine.cs`; (2) add a `Baseline` entry; (3) add an
`Expression` row in `Ilham.Rendering/AvatarState.cs`; (4) mention it in `PersonaBuilder.Protocol` so she'll use
it. The avatar and parser pick it up automatically.

**Add a new action tag** (e.g. `[reminder: …]`). (1) document it in `PersonaBuilder.Protocol`; (2) parse it in
`ReplyParser.Parse` and add a field to `ParsedReply`; (3) handle it in `IlhamBrain.Apply`.

**Swap the model backend.** Implement `ILlmClient` (see `OllamaClient` for the shape) and construct it in
`IlhamServices`. Her whole brain is model-agnostic.

**Add a setting.** Add a property to `UserProfile`, then expose it in `MainViewModel`/the desktop view (and/or
web `settings.js`). It persists automatically via `ProfileStore`.

**Build the webcam privacy lock (the owner recognizer).** Implement `IOwnerRecognizer` in the desktop app
using an on-device face-recognition library (enroll the owner's face once; each frame return only
owner/not-owner/no-face). Feed its output into a `PrivacyGuard`, and on `Hide`/`Show` minimize/restore the
window. Keep it to the coarse signal — never store or describe the other person (that's the boundary).

**Give her a true 3D face.** Build a VRM in VRoid Studio (no Blender needed), then write a 3D `AvatarControl`
replacement (three-vrm in a web view, or a native GL renderer) exposing `SetMood`/`SetAmplitude`/`LookAt`. See
`docs/AVATAR.md`.

**Train a custom "Ilham" model.** Chat with her to build up history, then
`TrainingExporter.ExportToFile(...)` → run the generated `train_ilham.py` on your GPU → `ollama create ilham`
→ set the model to `ilham` in settings. See `Training/TrainingExporter.cs`.

---

## 9. Testing & conventions

- **Tests** live in `tests/Ilham.Tests`: `CoreTests.cs` (emotions, memory, vault, commands, parser, occasions,
  profile, perception, privacy, training), `BrainTests.cs` (orchestration, guardian, initiative),
  `AvatarRenderTests.cs` (renders every mood to PNG), `WebTests.cs` (the optional API end-to-end),
  `GlobalUsings.cs` (`global using Xunit;`).
- **Conventions:** behaviour goes in `Ilham.Core` with a test; public members get XML docs (Core enforces it as
  an error); time-dependent logic takes a `TimeProvider` so tests can use `FakeTimeProvider`; the model client
  is an interface so tests use a fake. Follow these and the suite stays fast and deterministic.
- **The boundaries in `docs/BOUNDARIES.md` are enforced by tests** (no vault import; the privacy guard carries
  no personal data). If you extend those areas, keep the tests green.

---

## 10. Where her data lives

Everything personal is one SQLite file: `%LocalAppData%/Ilham/ilham.db` on Windows (or the platform equivalent;
the web server uses the same). It holds her facts, dates, conversation, settings, the (encrypted) vault, and
screen-time counters. Delete the file to reset her completely. Nothing is sent anywhere — the only network
calls are to your local Ollama server.
