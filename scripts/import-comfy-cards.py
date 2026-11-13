"""Export local BandoriLove cards from the verified Comfy comparison suite."""
import argparse
import json
import re
import uuid
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path, help="Comfy GSV bandori-assets folder")
    parser.add_argument("output", type=Path)
    parser.add_argument("--endpoint", default="http://127.0.0.1:19883/")
    parser.add_argument("--local", action="store_true", help="Use the packaged Windows inference engine")
    args = parser.parse_args()
    names = {item["id"]: item["name"] for item in read(args.assets / "roles.json")}
    comparison = args.assets / "comparison"
    summary = read(comparison / "summary.json")
    verified = {item["role"] for item in summary["roles"] if item["round"] == summary["recommended_round"]}
    references = {}
    for item in read(comparison / "suite.json"):
        role = item["role"]
        if role in verified:
            previous = references.setdefault(role, item["reference_text"])
            if previous != item["reference_text"]:
                raise ValueError(f"{role}: inconsistent reference transcript")
    if references.keys() != verified or not verified:
        raise ValueError("The comparison suite is missing verified roles")
    cards = []
    for role in sorted(verified, key=lambda key: names[key]):
        if not re.fullmatch(r"bd-[a-z0-9-]+", role):
            raise ValueError(f"Invalid role ID: {role}")
        cards.append({
            "Id": str(uuid.uuid5(uuid.NAMESPACE_URL, "bandorilove:comfy:" + role)),
            "Name": names[role],
            "Endpoint": args.endpoint,
            "ReferenceAudio": f"references/{role}.wav" if args.local else f"/round-1/roles/{role}/reference.wav",
            "PromptText": references[role],
            "PromptLanguage": "ja",
            "Local": args.local,
        })
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(cards, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Exported {len(cards)} cards for recommended round {summary['recommended_round']}")


if __name__ == "__main__":
    main()
