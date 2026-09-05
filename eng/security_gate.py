"""Static release boundary checks. No network traffic or receiver execution."""
import argparse
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]


def verify_source():
    violations = []
    for path in (ROOT / "src").rglob("*.cs"):
        if {"obj", "bin"}.intersection(path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        for pattern in [r"PropertyListParser\.Parse\(", r"using Makaretu\.Dns", r"new ServiceDiscovery\("]:
            if re.search(pattern, text):
                violations.append(f"Unsafe inbound dependency path: {path.relative_to(ROOT)}")
    for path in (ROOT / ".github/workflows").glob("*.yml"):
        for action in re.findall(r"uses:\s*([^\s#]+)", path.read_text()):
            if not re.fullmatch(r"[\w./-]+@[0-9a-f]{40}", action):
                violations.append(f"Unpinned action in {path.name}: {action}")
    sdk = json.loads((ROOT / "global.json").read_text())["sdk"]
    if sdk.get("rollForward") != "disable":
        violations.append("SDK roll-forward is enabled")
    if violations:
        raise ValueError("\n".join(violations))


def verify_release():
    gates = json.loads((ROOT / "eng/release-gates.json").read_text())["blockers"]
    missing = [key for key, value in gates.items() if value is not True]
    if missing:
        raise ValueError("ARM64 build held; unresolved gates: " + ", ".join(missing))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--release", action="store_true")
    args = parser.parse_args()
    try:
        verify_source()
        if args.release:
            verify_release()
    except ValueError as error:
        print(error, file=sys.stderr)
        sys.exit(1)
    print("Source boundary checks passed." if not args.release else "Recorded release gates passed.")
