# Run Ilham's brain on a free Kaggle GPU

Your PC stays in charge of Ilham (her memory, vault, personality). Only the **model** runs on Kaggle's free
GPU, reached over a tunnel. Nothing of hers is stored on Kaggle — only the prompt text is sent there while you
chat, and it's gone when the session ends.

**Free allowance:** Kaggle gives ~30 GPU-hours/week, up to ~12 h per session — the most generous free GPU.
It is **not** always-on: start it when you want her fast, stop it when you're done.

## Steps
1. Make a free account at **kaggle.com** and verify your phone (required for GPU + Internet).
2. **Create → New Notebook**, then **File → Import Notebook** and upload `cloud/ilham-kaggle.ipynb`
   (or paste its code cell into a new notebook).
3. In the right-hand panel: **Accelerator = GPU T4 x2**, **Internet = On**.
4. **Run** the cell. Wait ~2–3 minutes while it installs Ollama, pulls the model, and opens the tunnel.
5. Copy the printed `https://xxxxx.trycloudflare.com` URL.
6. In the Ilham desktop app, open the **⌨ Console** and run:
   ```
   /set ollamaurl https://xxxxx.trycloudflare.com
   /set model qwen2.5:14b
   /set ttsurl https://yyyyy.trycloudflare.com
   /model
   ```
   `/model` should say connected. Now she thinks on the free GPU — your slow PC just shows her — and
   `ttsurl` gives her a **natural neural voice** (`ur-PK-UzmaNeural`) instead of the robotic built-in one.
   The voice falls back to the Windows voice automatically if the cloud box isn't running.
7. **When you're done**, stop the Kaggle notebook (saves your weekly hours). Next time you run it you get a
   **new URL**, so just `/set ollamaurl <new url>` again.

## Notes
- Each new session = a new tunnel URL. Re-paste it with `/set ollamaurl`.
- If `/model` can't connect: make sure the Kaggle cell is still running and Internet was On.
- **First reply slow?** The cell pre-warms the model into the GPUs before printing the URL, and Ollama keeps
  it loaded (`OLLAMA_KEEP_ALIVE=24h`), so replies stay fast. The very first load still takes ~1 min.
- **Run only one Ilham at a time** against the tunnel (desktop *or* phone). Two at once cancel each other.
- Kaggle gives **two** T4s (32 GB total). `qwen2.5:14b` is the sweet spot on one card; `qwen2.5:32b` fits
  across both (spreading is enabled in the cell). `qwen2.5:7b` is the fast, safe fallback.
- This is "private to you" (your own session), but Kaggle is shared hardware — her secrets still never leave
  your PC, only the live prompt does.
