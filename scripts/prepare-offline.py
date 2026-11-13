"""Assemble the private Windows GPT-SoVITS bundle using verified Comfy assets."""
import argparse
import hashlib
import json
import re
import shutil
import tarfile
import wave
import zipfile
from pathlib import Path, PurePosixPath


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    if (not target.is_file() or target.stat().st_size != source.stat().st_size
            or target.stat().st_mtime_ns != source.stat().st_mtime_ns):
        shutil.copy2(source, target)


def extract(source, destination, prefix=""):
    destination = destination.resolve()
    with zipfile.ZipFile(source) as archive:
        for entry in archive.infolist():
            if entry.is_dir() or not entry.filename.startswith(prefix):
                continue
            relative = entry.filename[len(prefix):]
            target = (destination / relative).resolve()
            if not target.is_relative_to(destination):
                raise ValueError("Archive member outside destination")
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as stream, target.open("wb") as output:
                shutil.copyfileobj(stream, output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("comfy", type=Path)
    parser.add_argument("weights", type=Path)
    parser.add_argument("dist", type=Path)
    args = parser.parse_args()
    assets = args.comfy / "bandori-assets"
    output = args.dist.resolve()
    bundle = output / "gsv"
    engine = bundle / "engine"
    runtime = bundle / "runtime"
    source = args.comfy / "source.zip"
    with zipfile.ZipFile(source) as archive:
        prefix = archive.namelist()[0]
    if "e91506f1fba265e1841617c3042a1573206a7351" not in prefix:
        raise ValueError("Unexpected Comfy GPT-SoVITS revision")
    if not (engine / "api_v2.py").is_file():
        extract(source, engine, prefix)
    models = engine / "GPT_SoVITS" / "pretrained_models"
    files = {
        "chinese-hubert-base": ["config.json", "preprocessor_config.json", "pytorch_model.bin"],
        "chinese-roberta-wwm-ext-large": ["config.json", "tokenizer.json", "pytorch_model.bin"],
        "gsv-v5-pretrained": ["s2Gv5dev.pth", "vocoder.pth"],
    }
    for folder, names in files.items():
        for name in names:
            copy(args.comfy / "pretrained_models" / folder / name, models / folder / name)
    if not (runtime / "nltk_data").exists():
        extract(args.comfy / "nltk_data.zip", runtime)
    dictionary = runtime / "open_jtalk_dic_utf_8-1.11"
    if not dictionary.exists():
        with tarfile.open(args.comfy / "open_jtalk_dic_utf_8-1.11.tar.gz") as archive:
            archive.extractall(runtime, filter="data")
    g2pw = engine / "GPT_SoVITS" / "text" / "G2PWModel"
    if not g2pw.exists():
        extract(args.comfy / "G2PWModel.zip", engine / "GPT_SoVITS" / "text")

    suite = read(assets / "comparison" / "suite.json")
    references = {case["role"]: case for case in suite}
    for role, case in references.items():
        sha = case["reference_pcm_sha256"]
        if not re.fullmatch(r"bd-[a-z0-9-]+", role) or not re.fullmatch(r"[0-9a-f]{64}", sha):
            raise ValueError(f"Invalid role or reference hash: {role}")
        reference = assets / "roles" / role / "wav" / f"{sha}.wav"
        with wave.open(str(reference), "rb") as audio:
            pcm = audio.readframes(audio.getnframes())
        if hashlib.sha256(pcm).hexdigest() != sha:
            raise ValueError(f"Reference PCM checksum mismatch for {role}")
        copy(reference, bundle / "references" / f"{role}.wav")
    copy(Path(__file__).with_name("local-worker.py"), bundle / "local-worker.py")

    report = read(assets / "supplement" / "shared-weights.json")
    if report["status"] != "complete":
        raise ValueError("Round 2 weights are incomplete")
    config = {
        "version": "v5dev", "device": "cpu", "is_half": False,
        "bert_base_path": "GPT_SoVITS/pretrained_models/chinese-roberta-wwm-ext-large",
        "cnhuhbert_base_path": "GPT_SoVITS/pretrained_models/chinese-hubert-base",
    }
    missing = []
    for key, field in (("gpt", "t2s_weights_path"), ("sovits", "vits_weights_path")):
        name = PurePosixPath(report[key]).name
        weight = args.weights / name
        bundled = bundle / "weights" / name
        if not weight.is_file() and bundled.is_file():
            weight = bundled
        if not weight.is_file():
            missing.append(name)
            continue
        if weight.stat().st_size != report[f"{key}_bytes"] or digest(weight) != report[f"{key}_sha256"]:
            raise ValueError(f"Round 2 checksum mismatch: {name}")
        if weight != bundled:
            bundled.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(weight, bundled)
        config[field] = "../weights/" + name

    print(f"Prepared {len(references)} verified references")
    if missing:
        print("Missing trained weights: " + ", ".join(missing))
        return 2
    (bundle / "local.json").write_text(json.dumps({"custom": config}, indent=2), encoding="utf8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
