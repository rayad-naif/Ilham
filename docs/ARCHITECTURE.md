# Architecture

```
          Browser (wwwroot)                         Your PC
┌────────────────────────────────┐        ┌──────────────────────────────┐
│ avatar.js  — her animated face │        │ Ollama (local model)         │
│ voice.js   — speech in/out     │  HTTP  │  qwen2.5 / llama3.1 / …       │
│ perception.js — webcam read    │◄──────►│                              │
│ app.js / settings.js — UI      │  SSE   │ Ilham.App (ASP.NET Core)     │
└────────────────────────────────┘        │  Program.cs  — local API     │
                                           │  GuardianLoop — 30s heartbeat│
                                           │        │ uses                │
                                           │  Ilham.Core                  │
                                           │   IlhamBrain                 │
                                           │   Emotion · Memory · Vault   │
                                           │   Guardian · Commands · LLM  │
                                           │        │                     │
                                           │  ilham.db (SQLite, on disk)  │
                                           └──────────────────────────────┘
```

Nothing leaves the PC. The only outbound connection is the browser talking to `localhost` (or your phone to
the PC over your home Wi-Fi), and the app talking to Ollama, also on `localhost`.

## A conversation, end to end

1. You type or speak. The browser sends the text to `GET /api/chat` (Server-Sent Events).
2. `IlhamBrain.RespondAsync` builds the turn:
   - loads your **profile**,
   - asks **MemoryStore** for the facts most relevant to your message (TF-IDF cosine),
   - reads her current **emotional state** and the **guardian**'s health picture,
   - assembles the **system prompt** (`PersonaBuilder`): who she is + how she feels + what she remembers +
     what's coming up + the action-tag protocol.
3. The **model** streams a reply. Tags like `[mood:worried]`, `[remember: …]`, `[occasion: …|MM-DD]`,
   `[run:powershell: …]` are stripped from what you see (`ReplyParser`) and turned into actions.
4. Consequences are applied: new **facts/dates** saved, her **mood** nudged, any **command** queued for your
   approval. Her face switches to the new mood; her voice speaks the reply with lip-sync.

## Why these designs

- **Emotions as decaying intensities.** Each mood is a value in [0,1] that events raise and time pulls back to
  a resting baseline (exponential decay, 40-minute half-life). The strongest mood drives her face and tone, and
  recent *reasons* go into her prompt, so her feelings have causes instead of being random.
- **Memory you can trust.** Plain SQLite you can open and read. Facts are retrieved by keyword relevance, so
  the prompt stays small and on-topic as her memory grows.
- **The guardian is separate from the chat.** A background loop ticks every 30 s off the OS idle time, so she
  tracks *real* screen time and can actually lock/shutdown even when the window isn't focused — while the chat
  UI voices the warnings. Every action has a countdown and a password override.
- **Commands are proposals.** The model can only *ask*; `CommandSafety` rates the risk and nothing runs until
  you click approve. You keep full control of your own machine without handing the model a blank cheque.
- **The brain is swappable and testable.** `IlhamBrain` takes an `ILlmClient`, so the real Ollama client, and
  a scripted one for offline/demo/tests, are interchangeable. All of `Ilham.Core` is UI-free and unit-tested.

## Testing

`dotnet test` runs 49 tests: emotion decay and opposites, memory dedup/recall/forget, vault round-trip and
wrong-password safety, guardian escalation/snooze/bedtime math, command classification, reply parsing,
occasion maths, override-password hashing, perception mapping, and the HTTP API end to end
(`WebApplicationFactory`, including a streamed chat and a vault round-trip over HTTP).
