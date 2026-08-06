#!/usr/bin/env python3
"""
LoRA fine-tune a small model on a dataset built by build_dataset.py, then export a merged
GGUF you can load into Ollama.

This runs OUTSIDE the .NET app and needs a GPU + the Python ML stack (see requirements.txt).
It is NOT run automatically and NOT wired into the chat loop — you run it deliberately, then
evaluate the result before promoting it (see README).

Example:
  python finetune.py --data data/extraction.jsonl --base unsloth/Llama-3.2-3B-Instruct \
      --out outputs/companion-extractor --epochs 2
"""
import argparse


def main():
    parser = argparse.ArgumentParser(description="LoRA fine-tune from a companion dataset")
    parser.add_argument("--data", required=True, help="JSONL from build_dataset.py")
    parser.add_argument("--base", default="unsloth/Llama-3.2-3B-Instruct", help="Base model to adapt")
    parser.add_argument("--out", default="outputs/companion-adapter", help="Output directory")
    parser.add_argument("--epochs", type=float, default=2.0)
    parser.add_argument("--max-seq-len", type=int, default=4096)
    parser.add_argument("--rank", type=int, default=16)
    parser.add_argument("--learning-rate", type=float, default=2e-4)
    parser.add_argument("--gguf-quant", default="q4_k_m", help="Quantization for the exported GGUF")
    args = parser.parse_args()

    # Imports are inside main so build_dataset.py stays runnable without the ML stack installed.
    from unsloth import FastLanguageModel
    from datasets import load_dataset
    from trl import SFTTrainer, SFTConfig

    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=args.base,
        max_seq_length=args.max_seq_len,
        load_in_4bit=True,
    )
    model = FastLanguageModel.get_peft_model(
        model,
        r=args.rank,
        lora_alpha=args.rank,
        lora_dropout=0.0,
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                        "gate_proj", "up_proj", "down_proj"],
        use_gradient_checkpointing="unsloth",
    )

    dataset = load_dataset("json", data_files=args.data, split="train")

    def to_text(example):
        return {"text": tokenizer.apply_chat_template(
            example["messages"], tokenize=False, add_generation_prompt=False)}

    dataset = dataset.map(to_text)

    trainer = SFTTrainer(
        model=model,
        tokenizer=tokenizer,
        train_dataset=dataset,
        args=SFTConfig(
            dataset_text_field="text",
            per_device_train_batch_size=2,
            gradient_accumulation_steps=4,
            num_train_epochs=args.epochs,
            learning_rate=args.learning_rate,
            warmup_ratio=0.05,
            logging_steps=5,
            output_dir=f"{args.out}/checkpoints",
            optim="adamw_8bit",
            seed=42,
        ),
    )
    trainer.train()

    # Merge LoRA into the base and export a single GGUF — the most reliable path for Ollama.
    model.save_pretrained_gguf(args.out, tokenizer, quantization_method=args.gguf_quant)
    print(f"\nDone. Merged GGUF is under {args.out}/ — point the Modelfile's FROM at it.")


if __name__ == "__main__":
    main()
