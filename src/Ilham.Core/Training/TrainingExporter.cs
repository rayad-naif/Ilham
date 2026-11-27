using System.Text;
using System.Text.Json;
using Ilham.Core.Emotions;
using Ilham.Core.Memory;
using Ilham.Core.Occasions;
using Ilham.Core.Persona;
using Ilham.Core.Profile;

namespace Ilham.Core.Training;

/// <summary>Summary of an export.</summary>
/// <param name="Examples">Number of training examples written.</param>
/// <param name="Facts">Facts folded into the persona context.</param>
/// <param name="Path">Where the dataset was written.</param>
public sealed record ExportResult(int Examples, int Facts, string Path);

/// <summary>
/// Turns Ilham's own stored conversations into a fine-tuning dataset, so "train her on how I talk to her"
/// becomes a concrete file you can run through a LoRA fine-tune on your GPU. Everything comes from her local
/// database — your chats, her replies, her persona — and nothing leaves your machine.
/// </summary>
/// <remarks>
/// <para>Output is JSONL in the widely-supported "messages" format (one JSON object per line):
/// <c>{"messages":[{"role":"system",...},{"role":"user",...},{"role":"assistant",...}, …]}</c>. This is what
/// Unsloth, Axolotl and most trainers consume directly.</para>
/// <para>Each example is a sliding window of the conversation, prefixed with her persona as the system
/// message, so the fine-tune learns <i>her voice in context</i>, not isolated lines. This is not "accuracy"
/// training — there's no score — it's style/voice adaptation of a base model toward how she and you actually talk.</para>
/// </remarks>
public static class TrainingExporter
{
    /// <summary>Builds the JSONL dataset text from her memory.</summary>
    /// <param name="memory">Her store.</param>
    /// <param name="profile">User settings (for her persona/system message).</param>
    /// <param name="windowTurns">How many prior turns of context to include per example.</param>
    /// <param name="minExampleChars">Skip her replies shorter than this (drops trivial "ok" lines).</param>
    public static string BuildJsonl(MemoryStore memory, UserProfile profile, int windowTurns = 6, int minExampleChars = 2)
    {
        var lines = memory.RecentMessages(10_000); // whole history, oldest first
        string system = PersonaSystem(memory, profile);

        var sb = new StringBuilder();
        var window = new List<ChatLine>();
        foreach (var line in lines)
        {
            window.Add(line);
            if (window.Count > windowTurns * 2) window.RemoveAt(0);

            // Emit an example each time she (assistant) replies, teaching that reply from its context.
            if (line.Role == "assistant" && line.Content.Trim().Length >= minExampleChars
                && window.Any(w => w.Role == "user"))
            {
                var messages = new List<object> { new { role = "system", content = system } };
                foreach (var w in window)
                    messages.Add(new { role = w.Role == "assistant" ? "assistant" : "user", content = w.Content });
                sb.AppendLine(JsonSerializer.Serialize(new { messages }));
            }
        }
        return sb.ToString();
    }

    /// <summary>Writes the dataset to <paramref name="path"/> and returns a summary.</summary>
    /// <param name="memory">Her store.</param>
    /// <param name="profile">User settings.</param>
    /// <param name="path">Output .jsonl path.</param>
    /// <param name="windowTurns">Context turns per example.</param>
    public static ExportResult ExportToFile(MemoryStore memory, UserProfile profile, string path, int windowTurns = 6)
    {
        string jsonl = BuildJsonl(memory, profile, windowTurns);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, jsonl);
        int examples = jsonl.Length == 0 ? 0 : jsonl.TrimEnd('\n').Split('\n').Length;
        return new ExportResult(examples, memory.Facts().Count, Path.GetFullPath(path));
    }

    /// <summary>A compact persona system message that captures who she is plus the facts she knows.</summary>
    private static string PersonaSystem(MemoryStore memory, UserProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PersonaBuilder.Character).AppendLine();
        sb.AppendLine($"You call him: {profile.Name}.");
        var facts = memory.Facts();
        if (facts.Count > 0)
        {
            sb.AppendLine("What you know about him:");
            foreach (var f in facts.Take(40)) sb.AppendLine($"  • {f.Text}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A ready-to-run LoRA fine-tuning recipe (Python, Unsloth) the user runs on their own GPU. Written next
    /// to the dataset so "train her" is a copy-paste away. This does the training on <i>their</i> machine —
    /// nothing here uploads anything.
    /// </summary>
    /// <param name="baseModel">Hugging Face base model id to adapt.</param>
    /// <param name="datasetFile">The exported JSONL file name.</param>
    public static string TrainingRecipe(string baseModel = "unsloth/Qwen2.5-7B-Instruct-bnb-4bit", string datasetFile = "ilham-dataset.jsonl") => $$"""
        # Fine-tune a personal "Ilham" LoRA on your own conversations — runs on your GPU (16–24 GB).
        # One-time setup:   pip install "unsloth[cu121] @ git+https://github.com/unslothai/unsloth.git"
        # Then:             python train_ilham.py
        #
        # This teaches a base model Ilham's voice from YOUR chats. It is style adaptation, not "accuracy".
        # On 24 GB VRAM you can raise the base model to a 14B (e.g. unsloth/Qwen2.5-14B-Instruct-bnb-4bit).

        from unsloth import FastLanguageModel
        from datasets import load_dataset
        from trl import SFTTrainer
        from transformers import TrainingArguments
        import torch

        MODEL = "{{baseModel}}"
        DATA  = "{{datasetFile}}"

        model, tokenizer = FastLanguageModel.from_pretrained(MODEL, max_seq_length=4096, load_in_4bit=True)
        model = FastLanguageModel.get_peft_model(model, r=16, lora_alpha=16, lora_dropout=0,
            target_modules=["q_proj","k_proj","v_proj","o_proj","gate_proj","up_proj","down_proj"])

        ds = load_dataset("json", data_files=DATA, split="train")
        def fmt(ex): return { "text": tokenizer.apply_chat_template(ex["messages"], tokenize=False) }
        ds = ds.map(fmt)

        trainer = SFTTrainer(
            model=model, tokenizer=tokenizer, train_dataset=ds, dataset_text_field="text",
            max_seq_length=4096,
            args=TrainingArguments(
                per_device_train_batch_size=2, gradient_accumulation_steps=4,
                warmup_steps=5, num_train_epochs=2, learning_rate=2e-4,
                fp16=not torch.cuda.is_bf16_supported(), bf16=torch.cuda.is_bf16_supported(),
                logging_steps=1, optim="adamw_8bit", output_dir="ilham-lora"),
        )
        trainer.train()

        # Save, then turn into an Ollama model so Ilham uses her:
        model.save_pretrained_gguf("ilham-gguf", tokenizer, quantization_method="q4_k_m")
        # In ilham-gguf/:  ollama create ilham -f Modelfile   (then set model to "ilham" in Settings)
        print("Done. Point Ilham's model at your new 'ilham' model in Settings.")
        """;
}
