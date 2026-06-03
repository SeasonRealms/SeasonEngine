#!/usr/bin/env python3
import argparse
import importlib
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Any

import torch
from huggingface_hub import hf_hub_download
from huggingface_hub.errors import EntryNotFoundError, GatedRepoError, RepositoryNotFoundError
from safetensors.torch import load_file


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_STABLE_AUDIO_REPO = "https://github.com/Stability-AI/stable-audio-3.git"
DEFAULT_STABLE_AUDIO_REF = "main"
VENDORED_STABLE_AUDIO = REPO_ROOT / "stable-audio-3-main"
FALLBACK_STABLE_AUDIO = REPO_ROOT / ".stable-audio-3-main"

create_diffusion_cond_from_config = None
copy_state_dict = None
NumberConditioner = None


def _ensure_stable_audio_source() -> Path:
    override_dir = os.environ.get("STABLE_AUDIO_3_DIR", "").strip()
    if override_dir:
        candidate = Path(override_dir).resolve()
        package_dir = candidate / "stable_audio_3"
        if package_dir.is_dir():
            return candidate
        raise RuntimeError(
            f"环境变量 STABLE_AUDIO_3_DIR 指向的目录不包含 `stable_audio_3` 包: {candidate}"
        )

    for candidate in (VENDORED_STABLE_AUDIO, FALLBACK_STABLE_AUDIO):
        if (candidate / "stable_audio_3").is_dir():
            return candidate

    repo_url = os.environ.get("STABLE_AUDIO_3_GIT_URL", DEFAULT_STABLE_AUDIO_REPO).strip()
    repo_ref = os.environ.get("STABLE_AUDIO_3_REF", DEFAULT_STABLE_AUDIO_REF).strip() or DEFAULT_STABLE_AUDIO_REF
    target_dir = FALLBACK_STABLE_AUDIO

    target_dir.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "git",
            "clone",
            "--depth",
            "1",
            "--branch",
            repo_ref,
            repo_url,
            str(target_dir),
        ],
        check=True,
    )
    return target_dir


def _load_stable_audio_modules() -> None:
    global create_diffusion_cond_from_config
    global copy_state_dict
    global NumberConditioner

    if create_diffusion_cond_from_config is not None:
        return

    stable_audio_root = _ensure_stable_audio_source()
    if str(stable_audio_root) not in sys.path:
        sys.path.insert(0, str(stable_audio_root))

    factory_module = importlib.import_module("stable_audio_3.factory")
    loading_utils_module = importlib.import_module("stable_audio_3.loading_utils")
    conditioners_module = importlib.import_module("stable_audio_3.models.conditioners")

    create_diffusion_cond_from_config = factory_module.create_diffusion_cond_from_config
    copy_state_dict = loading_utils_module.copy_state_dict
    NumberConditioner = conditioners_module.NumberConditioner


def _patch_t5_conditioner() -> None:
    _load_stable_audio_modules()
    import stable_audio_3.factory as factory_module
    import stable_audio_3.models.conditioners as conditioners_module

    class ExportOnlyT5GemmaConditioner(conditioners_module.Conditioner):
        T5GEMMA_MODEL_DIMS = {
            "google/t5gemma-b-b-ul2": 768,
        }

        def __init__(
            self,
            output_dim: int,
            model_name: str = "google/t5gemma-b-b-ul2",
            max_length: int = 256,
            project_out: bool = False,
            padding_mode: str = "zero",
            **_: Any,
        ) -> None:
            if model_name not in self.T5GEMMA_MODEL_DIMS:
                raise ValueError(f"Unsupported T5Gemma model for export stub: {model_name}")
            super().__init__(
                self.T5GEMMA_MODEL_DIMS[model_name],
                output_dim,
                project_out=project_out,
                padding_mode=padding_mode,
            )
            self.max_length = int(max_length)

        def forward(self, inputs: Any, device: str) -> Any:
            raise RuntimeError(
                "The export-only T5Gemma stub does not support runtime encoding. "
                "Export the text encoder separately if you need it."
            )

    factory_module.T5GemmaConditioner = ExportOnlyT5GemmaConditioner
    conditioners_module.T5GemmaConditioner = ExportOnlyT5GemmaConditioner


def _download_file(
    repo_id: str,
    filename: str,
    revision: str,
    token: str | None,
    subfolder: str | None = None,
) -> str:
    remote_path = f"{subfolder.strip('/')}/{filename}" if subfolder else filename
    try:
        return hf_hub_download(
            repo_id=repo_id,
            filename=remote_path,
            revision=revision,
            repo_type="model",
            token=token,
        )
    except GatedRepoError as ex:
        raise RuntimeError(
            f"无法下载 `{repo_id}/{remote_path}`。该模型仓库是 gated repo，请在环境变量或 GitHub Secrets 中提供 `HF_TOKEN`，"
            "并确认该 token 已接受模型许可。"
        ) from ex
    except RepositoryNotFoundError as ex:
        raise RuntimeError(f"模型仓库不存在或当前 token 无权限访问: {repo_id}") from ex
    except EntryNotFoundError as ex:
        raise RuntimeError(f"仓库中不存在文件: {repo_id}/{remote_path}") from ex


def _load_config(config_path: str) -> dict[str, Any]:
    with open(config_path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def _load_diffusion_model(
    repo_id: str,
    revision: str,
    token: str | None,
    device: str,
) -> tuple[torch.nn.Module, dict[str, Any], str, str]:
    _patch_t5_conditioner()
    _load_stable_audio_modules()

    config_path = _download_file(repo_id, "model_config.json", revision, token)
    checkpoint_path = _download_file(repo_id, "model.safetensors", revision, token)
    model_config = _load_config(config_path)

    model = create_diffusion_cond_from_config(model_config)
    copy_state_dict(model, load_file(checkpoint_path))
    model.to(device).eval().requires_grad_(False)
    return model, model_config, config_path, checkpoint_path


class StableAudioDiTOnnxWrapper(torch.nn.Module):
    def __init__(
        self,
        model: torch.nn.Module,
        max_text_tokens: int,
        text_conditioner_key: str,
        seconds_conditioner_key: str,
        local_condition_key: str | None,
    ) -> None:
        super().__init__()
        self.model = model.model
        self.max_text_tokens = max_text_tokens
        self.text_conditioner_key = text_conditioner_key
        self.seconds_conditioner_key = seconds_conditioner_key
        self.local_condition_key = local_condition_key

        conditioner = model.conditioner.conditioners[seconds_conditioner_key]
        if not isinstance(conditioner, NumberConditioner):
            raise TypeError(
                f"Conditioner '{seconds_conditioner_key}' must be NumberConditioner, got {type(conditioner).__name__}."
            )
        self.seconds_conditioner = conditioner

    def _embed_seconds_total(self, seconds_total: torch.Tensor) -> torch.Tensor:
        conditioner = self.seconds_conditioner
        values = seconds_total.reshape(-1).to(torch.float32)
        values = values.clamp(conditioner.min_val, conditioner.max_val)
        values = (values - conditioner.min_val) / (conditioner.max_val - conditioner.min_val)

        embedder_dtype = next(conditioner.embedder.parameters()).dtype
        values = values.to(embedder_dtype)
        embeddings = conditioner.embedder(values).unsqueeze(1)
        embeddings = conditioner.proj_out(embeddings)
        return embeddings.squeeze(1)

    def forward(
        self,
        x: torch.Tensor,
        t: torch.Tensor,
        t5_hidden: torch.Tensor,
        t5_mask: torch.Tensor,
        seconds_total: torch.Tensor,
        local_add_cond: torch.Tensor,
    ) -> torch.Tensor:
        # Keep the mask as a visible ONNX input even though the underlying model
        # currently does not consume it after export-time graph lowering.
        t5_hidden = t5_hidden + (t5_mask.unsqueeze(-1).to(t5_hidden.dtype) * 0.0)

        global_cond = self._embed_seconds_total(seconds_total)
        cross_attn_mask = t5_mask > 0.5

        return self.model(
            x=x,
            t=t,
            cross_attn_cond=t5_hidden,
            cross_attn_mask=cross_attn_mask,
            global_cond=global_cond,
            local_add_cond=local_add_cond,
            cfg_scale=1.0,
        )


class StableAudioDecoderOnnxWrapper(torch.nn.Module):
    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.decoder = model.pretransform

    def forward(self, latent: torch.Tensor) -> torch.Tensor:
        return self.decoder.decode(latent, chunked=False)


class T5GemmaEncoderOnnxWrapper(torch.nn.Module):
    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> torch.Tensor:
        hidden_states = self.model(
            input_ids=input_ids,
            attention_mask=attention_mask,
        ).last_hidden_state
        return hidden_states + (attention_mask.unsqueeze(-1).to(hidden_states.dtype) * 0.0)


def _download_tokenizer_bundle(
    repo_id: str,
    revision: str,
    token: str | None,
    source_subfolder: str,
    bundle_dir: Path,
) -> dict[str, Any]:
    tokenizer_files = [
        "tokenizer.json",
        "tokenizer.model",
        "tokenizer_config.json",
        "special_tokens_map.json",
    ]

    downloaded: list[dict[str, Any]] = []
    bundle_dir.mkdir(parents=True, exist_ok=True)

    for name in tokenizer_files:
        cached_path = Path(
            _download_file(
                repo_id=repo_id,
                filename=name,
                revision=revision,
                token=token,
                subfolder=source_subfolder,
            )
        ).resolve()
        target_path = bundle_dir / name
        target_path.write_bytes(cached_path.read_bytes())
        downloaded.append(
            {
                "source": f"{repo_id}/{source_subfolder}/{name}",
                "path": target_path.as_posix(),
                "size_bytes": target_path.stat().st_size,
            }
        )

    return {
        "directory": bundle_dir.as_posix(),
        "files": downloaded,
    }


def _resolve_text_conditioner_info(model: torch.nn.Module) -> tuple[str, int]:
    _load_stable_audio_modules()
    if len(model.cross_attn_cond_ids) != 1:
        raise ValueError(
            f"Expected exactly one cross-attention conditioner, got {model.cross_attn_cond_ids!r}."
        )
    key = model.cross_attn_cond_ids[0]
    conditioner = model.conditioner.conditioners[key]
    max_text_tokens = int(getattr(conditioner, "max_length", 256))
    return key, max_text_tokens


def _resolve_seconds_conditioner_key(model: torch.nn.Module) -> str:
    _load_stable_audio_modules()
    if len(model.global_cond_ids) != 1:
        raise ValueError(f"Expected exactly one global conditioner, got {model.global_cond_ids!r}.")
    return model.global_cond_ids[0]


def _resolve_local_condition_key(model: torch.nn.Module) -> str | None:
    _load_stable_audio_modules()
    if len(model.local_add_cond_ids) > 1:
        raise ValueError(
            f"Expected at most one local-add conditioner, got {model.local_add_cond_ids!r}."
        )
    return model.local_add_cond_ids[0] if model.local_add_cond_ids else None


def _export_dit(
    model: torch.nn.Module,
    out_path: Path,
    latent_example_length: int,
    opset: int,
) -> dict[str, Any]:
    text_key, max_text_tokens = _resolve_text_conditioner_info(model)
    seconds_key = _resolve_seconds_conditioner_key(model)
    local_key = _resolve_local_condition_key(model)

    wrapper = StableAudioDiTOnnxWrapper(
        model=model,
        max_text_tokens=max_text_tokens,
        text_conditioner_key=text_key,
        seconds_conditioner_key=seconds_key,
        local_condition_key=local_key,
    ).eval()

    example_x = torch.randn(1, model.io_channels, latent_example_length, dtype=torch.float32)
    example_t = torch.tensor([0.5], dtype=torch.float32)
    example_t5_hidden = torch.randn(1, max_text_tokens, 768, dtype=torch.float32)
    example_t5_mask = torch.ones(1, max_text_tokens, dtype=torch.float32)
    example_seconds_total = torch.tensor([7.0], dtype=torch.float32)
    example_local_add_cond = torch.zeros(1, 257, latent_example_length, dtype=torch.float32)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        wrapper,
        (
            example_x,
            example_t,
            example_t5_hidden,
            example_t5_mask,
            example_seconds_total,
            example_local_add_cond,
        ),
        str(out_path),
        export_params=True,
        opset_version=opset,
        do_constant_folding=True,
        input_names=[
            "x",
            "t",
            "t5_hidden",
            "t5_mask",
            "seconds_total",
            "local_add_cond",
        ],
        output_names=["velocity"],
        dynamic_axes={
            "x": {2: "latent_length"},
            "local_add_cond": {2: "latent_length"},
            "velocity": {0: "batch", 2: "latent_length"},
        },
    )

    return {
        "path": out_path.as_posix(),
        "max_text_tokens": max_text_tokens,
        "io_channels": model.io_channels,
        "seconds_conditioner_key": seconds_key,
        "text_conditioner_key": text_key,
        "local_condition_key": local_key,
    }


def _export_decoder(
    model: torch.nn.Module,
    out_path: Path,
    latent_example_length: int,
    opset: int,
) -> dict[str, Any]:
    wrapper = StableAudioDecoderOnnxWrapper(model).eval()
    example_latent = torch.randn(1, model.io_channels, latent_example_length, dtype=torch.float32)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        wrapper,
        (example_latent,),
        str(out_path),
        export_params=True,
        opset_version=opset,
        do_constant_folding=True,
        input_names=["latent"],
        output_names=["pcm"],
        dynamic_axes={
            "latent": {2: "latent_length"},
            "pcm": {0: "batch", 2: "sample_length"},
        },
    )

    return {
        "path": out_path.as_posix(),
        "io_channels": model.io_channels,
    }


def _export_text_encoder(
    repo_id: str,
    revision: str,
    token: str | None,
    subfolder: str | None,
    out_path: Path,
    max_text_tokens: int,
    opset: int,
) -> dict[str, Any]:
    from transformers import AutoConfig, T5GemmaEncoderModel

    hf_kwargs: dict[str, Any] = {"revision": revision, "token": token}
    if subfolder:
        hf_kwargs["subfolder"] = subfolder

    config = AutoConfig.from_pretrained(repo_id, **hf_kwargs)
    config.is_encoder_decoder = False
    model = T5GemmaEncoderModel.from_pretrained(
        repo_id,
        config=config,
        **hf_kwargs,
    ).eval()

    wrapper = T5GemmaEncoderOnnxWrapper(model).eval()
    example_input_ids = torch.zeros(1, max_text_tokens, dtype=torch.long)
    example_attention_mask = torch.ones(1, max_text_tokens, dtype=torch.long)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        wrapper,
        (example_input_ids, example_attention_mask),
        str(out_path),
        export_params=True,
        opset_version=opset,
        do_constant_folding=True,
        input_names=["input_ids", "attention_mask"],
        output_names=["hidden_states"],
    )

    return {
        "path": out_path.as_posix(),
        "max_text_tokens": max_text_tokens,
        "hidden_size": 768,
        "source_repo_id": repo_id,
        "source_subfolder": subfolder or "",
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Export Stable Audio checkpoints to ONNX.")
    parser.add_argument("--model-id", default="stabilityai/stable-audio-3-small-sfx")
    parser.add_argument("--revision", default="main")
    parser.add_argument("--model-alias", default="sa3-sm-sfx")
    parser.add_argument("--decoder-subdir", default="same-s")
    parser.add_argument("--decoder-filename", default="dec_dynamic_bf16.onnx")
    parser.add_argument("--out-dir", default="out")
    parser.add_argument("--opset", type=int, default=17)
    parser.add_argument("--latent-example-length", type=int, default=256)
    parser.add_argument("--export-decoder", action="store_true")
    parser.add_argument("--export-text-encoder", action="store_true")
    parser.add_argument("--download-tokenizer", action="store_true")
    parser.add_argument("--t5-source-repo-id", default="")
    parser.add_argument("--t5-source-subfolder", default="t5gemma-b-b-ul2")
    parser.add_argument("--t5-bundle-subdir", default="t5gemma")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    token = os.environ.get("HF_TOKEN", "").strip() or None
    out_dir = Path(args.out_dir).resolve()
    device = "cpu"

    model, model_config, config_path, checkpoint_path = _load_diffusion_model(
        repo_id=args.model_id,
        revision=args.revision,
        token=token,
        device=device,
    )

    dit_out_path = out_dir / args.model_alias / "dit.onnx"
    dit_summary = _export_dit(
        model=model,
        out_path=dit_out_path,
        latent_example_length=args.latent_example_length,
        opset=args.opset,
    )

    artifacts: dict[str, Any] = {
        "dit": dit_summary,
        "decoder": None,
        "text_encoder": None,
        "tokenizer": None,
    }

    if args.export_decoder:
        decoder_out_path = out_dir / args.decoder_subdir / args.decoder_filename
        artifacts["decoder"] = _export_decoder(
            model=model,
            out_path=decoder_out_path,
            latent_example_length=args.latent_example_length,
            opset=args.opset,
        )

    if args.export_text_encoder:
        t5_source_repo_id = args.t5_source_repo_id or args.model_id
        text_encoder_out_path = out_dir / args.t5_bundle_subdir / "encoder.onnx"
        artifacts["text_encoder"] = _export_text_encoder(
            repo_id=t5_source_repo_id,
            revision=args.revision,
            token=token,
            subfolder=args.t5_source_subfolder,
            out_path=text_encoder_out_path,
            max_text_tokens=dit_summary["max_text_tokens"],
            opset=args.opset,
        )

    if args.download_tokenizer:
        t5_source_repo_id = args.t5_source_repo_id or args.model_id
        artifacts["tokenizer"] = _download_tokenizer_bundle(
            repo_id=t5_source_repo_id,
            revision=args.revision,
            token=token,
            source_subfolder=args.t5_source_subfolder,
            bundle_dir=out_dir / args.t5_bundle_subdir,
        )

    manifest = {
        "model_id": args.model_id,
        "revision": args.revision,
        "model_alias": args.model_alias,
        "source_files": {
            "config": config_path,
            "checkpoint": checkpoint_path,
        },
        "sample_rate": model_config.get("sample_rate"),
        "sample_size": model_config.get("sample_size"),
        "exports": artifacts,
        "notes": [
            "The DiT export matches the SeasonEngine ONNX runtime contract.",
            "The text encoder and tokenizer can be sourced from the same Stable Audio repo to maximize compatibility.",
            "The decoder export is generated from the SAME weights bundled inside the Stable Audio checkpoint.",
            "When the source repo is gated, HF_TOKEN is required for both weight download and tokenizer download.",
        ],
    }

    out_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = out_dir / "export-manifest.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

    print(json.dumps(manifest, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
