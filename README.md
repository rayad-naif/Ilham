# Ilham 💜

**A private AI companion that lives on your own PC.** She has moods, remembers your life, looks after your
health, speaks Urdu and English, shows an animated face, and can run things on your computer — all locally.
Your conversations, memories and secrets never leave your machine.

> **In one word: _Companion_.** Think of a caring, stubborn, sarcastic presence on your desktop — part
> friend, part overprotective girlfriend, part tech helper — that runs entirely offline on your hardware.

![Ilham](docs/ilham.png)

---

## What she does

| | |
|---|---|
| 💬 **Talks like you** | Roman-Urdu + English mix (or full Urdu / English), warm, teasing, sarcastic, stubborn. |
| 😊 **Feels** | A real emotion engine — happy, loving, worried, annoyed — that rises to events and calms over time, and drives her face and tone. |
| 🧠 **Remembers** | Facts about you, your birthday and the days that matter. She recalls what's relevant to each conversation (TF-IDF retrieval). |
| 🎂 **Celebrates** | Knows your birthday and important dates and makes a fuss of them. |
| 🩺 **Protects your health** | Tracks real screen time, nags about breaks and sleep, escalates from reminders to arguing to (optionally) locking the screen — always with a password override. |
| 🖥️ **Runs your PC** | Proposes PowerShell / CMD / bash commands; nothing runs until **you** approve it. Dangerous commands get a louder warning. |
| 🗣️ **Voice** | Listens (Urdu/English speech) and speaks back with a female voice; her mouth lip-syncs. |
| 📷 **Sees you (when you turn it on)** | Reads *your* expression from *your* webcam to sense your mood. A light shows when the camera is live. |
| 🔐 **Guards your secrets** | An encrypted vault **you fill yourself** — she never scrapes your browser or OS. |
| 🎭 **Animated face** | An expressive avatar that blinks, breathes, lip-syncs, **wanders around**, and emotes by mood. No model file needed; runs offline. |
| 🔔 **Talks first** | When you go quiet she doesn't just wait — she checks in, teases, gets impatient, then **demanding**, then sulks, the longer she's ignored. |

---

## Quick start (Windows)

**1. Install .NET** (8 or newer) from <https://dotnet.microsoft.com/download>.

**2. Install Ollama** (her brain) from <https://ollama.com>, then download a model sized to your hardware:
```bash
ollama pull qwen2.5:3b
```

| Your hardware | Model | Notes |
|---|---|---|
| **16 GB RAM, integrated/CPU** (now) | `qwen2.5:7b` (default) | Full smartness, runs in RAM on CPU — slower but not dumber. |
| **8–12 GB VRAM** | `qwen2.5:7b` / `14b` | Smooth, sharp Urdu + coding. |
| **24 GB VRAM** (your upgrade) | `qwen2.5:32b` | Her smartest brain — top knowledge, emotion, reasoning. Or `14b` for max speed with long memory. |

She uses whatever you set in Settings, including uncensored community models from Hugging Face.

You pick the model in Settings — including uncensored community models from Hugging Face, which is your call
on your own hardware. She runs in demo mode (scripted) until a model is installed, so you can try everything first.

**3. Run Ilham (native desktop app — no browser):**
```bash
dotnet run --project src/Ilham.Desktop -c Release
```
A real window opens with her animated face and the chat. Without Ollama she runs in a scripted "demo mode" so
you can see everything first.

> There's also an optional **phone-access server** (`src/Ilham.App`) if you want to reach her from your phone
> over Wi-Fi — but the main experience is the native desktop app. See [docs/PHONE.md](docs/PHONE.md).

---

## Make her look like *your* Ilham

Her face is a **stylised, hand-drawn avatar** (rendered natively with SkiaSharp — no browser) that blinks,
breathes, lip-syncs and emotes. It's styled after a reference look: warm skin, long dark hair, a mustard
kameez with a cream dupatta. Three ways to make her *yours*:
- **A — stylised her** (default): the drawn, fully-animated face, tinted to your reference. Comes alive.
- **B — your image as a portrait**: show your own picture file, unchanged, as her portrait alongside the
  animated face.
- **C — true 3D**: build a VRM in free [VRoid Studio](https://vroid.com/en/studio) from your reference.

I don't build a photorealistic clone of an individual's face (see [docs/BOUNDARIES.md](docs/BOUNDARIES.md)),
and no tool turns one photo into a rigged, talking model — so the animated face is always a drawn character.

---

## Train her on your own voice (optional)

"Learn from me" is real: her memory already grows from every chat. For a deeper custom "Ilham", export your
conversations into a fine-tuning dataset and train a LoRA on your own GPU — nothing uploads anywhere.

`TrainingExporter` writes `ilham-dataset.jsonl` (standard messages format) from her local database, plus a
ready-to-run `train_ilham.py` (Unsloth) you run on a 16–24 GB GPU. The result becomes an Ollama model you
point her at in Settings. This is **voice/style adaptation, not "accuracy"** — a personality has no score to
hit, but a custom model makes her sound even more like *her*.

## What she will **not** do — and why

Ilham is built to be *yours*, and these limits are part of that, not a restriction bolted on:

- **She never reads your saved passwords** from your browser, Windows Hello, or any password manager.
  That technique is identical to a credential stealer, so it isn't built. Instead she triggers your real
  password manager's autofill, or asks you for a login and keeps it in her own **encrypted vault** that only
  you fill. ([docs/BOUNDARIES.md](docs/BOUNDARIES.md))
- **She is not a hidden always-on camera/mic, and she does not watch other people.** The camera and mic are
  off until you turn them on, with a visible indicator, and she looks at *you* — never scanning your
  surroundings to identify or comment on people who never agreed to be watched.
- **She won't build or run tooling meant to harm other people or their systems.** On *your* PC, with your
  approval, she'll run anything you like; the line is harm to others.

Everything else — her blunt, adult, unfiltered personality; full control of your own machine; learning from
you; speaking freely — is hers to do.

---

## How it's built

```
Ilham.sln
├── src/
│   ├── Ilham.Core/              ← all the logic, no UI (fully unit-tested)
│   │   ├── Emotions/            EmotionEngine — decaying multi-mood state
│   │   ├── Memory/              MemoryStore — SQLite facts/dates/chat + TF-IDF recall
│   │   ├── Vault/               SecretVault — AES-256-GCM, user-filled only
│   │   ├── Guardian/            HealthGuardian + PcActions (lock/shutdown/idle)
│   │   ├── Commands/            CommandSafety + CommandRunner (approval-gated)
│   │   ├── Llm/                 Ollama client + scripted demo client
│   │   ├── Perception/          turns a webcam/voice read into feelings
│   │   ├── Initiative/          when she speaks first (check-in → tease → demand → sulk)
│   │   ├── Persona/             her system prompt + reply-tag parser
│   │   └── IlhamBrain.cs        orchestrates a turn (memory + mood + model + actions)
│   │   ├── Training/            TrainingExporter — your chats → a LoRA fine-tune dataset + recipe
│   │   └── IlhamServices.cs     one-call composition of her whole brain (no UI)
│   ├── Ilham.Rendering/         ← SkiaSharp avatar renderer (native, cross-platform, no browser)
│   ├── Ilham.Desktop/           ← the app: Avalonia (Windows now, cross-platform ready)
│   │   ├── Controls/AvatarControl.cs   her face, drawn with SkiaSharp inside Avalonia
│   │   ├── ViewModels/          wires Core to the window (chat, mood, approvals, heartbeat)
│   │   └── Views/MainWindow     her face + the conversation
│   └── Ilham.App/               ← OPTIONAL ASP.NET server for phone access (not the main app)
└── tests/Ilham.Tests/           ← 67 tests (core logic, avatar render-to-PNG, web API)
```

- **Private by construction.** One SQLite file under your local app-data folder holds everything. No cloud calls.
- **Her brain is swappable.** Any Ollama model. Offline demo mode when none is installed.
- **Tested.** Emotion decay, memory recall, vault crypto, guardian escalation, command safety, reply parsing,
  and the HTTP API all have tests. `dotnet test` → 67 passing.
- **Runs on .NET 8–11.** The project targets whatever SDK you have (see `Directory.Build.props`).

More: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · [docs/BOUNDARIES.md](docs/BOUNDARIES.md) · [docs/PHONE.md](docs/PHONE.md) · [docs/AVATAR.md](docs/AVATAR.md)
