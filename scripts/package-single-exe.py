"""Embed the offline GSV runtime and assets in a published Windows executable."""
import argparse
import shutil
import struct
import subprocess
import tempfile
import uuid
import zipfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path, help="Published .NET single-file executable")
    parser.add_argument("assets", type=Path, help="Directory containing gsv/ and characters.local.json")
    parser.add_argument("output", type=Path)
    parser.add_argument("--archive", type=Path, help="Reuse a prebuilt 7z archive")
    parser.add_argument("--extractor", type=Path, help="7z.exe, with 7z.dll next to it")
    args = parser.parse_args()
    if args.output.resolve() == args.executable.resolve():
        parser.error("Use a separate output path")
    required = ("gsv/runtime/python.exe", "gsv/cuda/torch/__init__.py", "gsv/local.json",
                "gsv/local-worker.py", "gsv/engine/api_v2.py", "characters.local.json")
    for relative in required:
        if not (args.assets / relative).is_file():
            raise FileNotFoundError(args.assets / relative)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_name(args.output.name + "." + uuid.uuid4().hex + ".partial")
    try:
        shutil.copyfile(args.executable, temporary)
        files = [args.assets / "characters.local.json"] + [
            file for file in sorted((args.assets / "gsv").rglob("*"))
            if file.is_file() and "__pycache__" not in file.parts and file.suffix.lower() not in {
                ".pyc", ".lib", ".pdb", ".h", ".hpp", ".cuh",
            }
        ]
        if args.archive and not args.extractor:
            parser.error("--archive requires --extractor")
        if args.extractor:
            extractor = args.extractor.resolve()
            library = extractor.with_name("7z.dll")
            files = [file for file in files if not is_cpu_torch(file.relative_to(args.assets))]
            with tempfile.TemporaryDirectory(dir=args.output.parent) as scratch:
                archive = args.archive
                if archive is None:
                    archive = Path(scratch).resolve() / "models.7z"
                    filelist = archive.with_suffix(".txt")
                    filelist.write_text("\n".join(file.relative_to(args.assets).as_posix() for file in files),
                                        encoding="utf-8")
                    subprocess.run([extractor, "a", "-t7z", "-m0=LZMA2:d=32m", "-mx=3", "-ms=128m",
                                    "-scsUTF-8", archive, "@" + str(filelist)], cwd=args.assets, check=True)
                with temporary.open("ab") as output:
                    offset = output.tell()
                    for part in (archive, extractor, library):
                        with part.open("rb") as source:
                            shutil.copyfileobj(source, output, 8 * 1024 * 1024)
                    output.write(struct.pack("<qqii", offset, sum(file.stat().st_size for file in files),
                                             extractor.stat().st_size, library.stat().st_size))
                    output.write(b"BL7ZPACK" + uuid.uuid4().bytes_le)
        else:
            with zipfile.ZipFile(temporary, "a", compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
                for file in files:
                    archive.write(file, file.relative_to(args.assets).as_posix())
                archive.comment = b"BLBUNDLE" + uuid.uuid4().bytes_le
        if temporary.stat().st_size >= 4 * 1024**3:
            raise ValueError("The bundle exceeds the Windows executable size limit")
        temporary.replace(args.output)
        print(f"{args.output} ({args.output.stat().st_size / 1073741824:.2f} GiB)", flush=True)
    finally:
        temporary.unlink(missing_ok=True)


def is_cpu_torch(relative):
    parts = relative.parts
    return len(parts) >= 5 and parts[:4] == ("gsv", "runtime", "Lib", "site-packages") and (
        parts[4] in {"torch", "torchaudio", "functorch", "torchgen"}
        or parts[4].startswith(("torch-2.8", "torchaudio-2.8"))
    )


if __name__ == "__main__":
    main()
