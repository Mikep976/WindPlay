"""Fail closed on audit command errors, missing output, or known advisories."""
import json
import os
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
destination = root / "artifacts/security"
destination.mkdir(parents=True, exist_ok=True)
projects = sys.argv[1:] or ["tests/WindPlay.Protocol.Tests/WindPlay.Protocol.Tests.csproj"]
report = {"projects": []}
for project in projects:
    command = [os.environ.get("WINDPLAY_DOTNET", "dotnet"), "list", project,
               "package", "--vulnerable", "--include-transitive", "--format", "json"]
    result = subprocess.run(command, cwd=root, capture_output=True, text=True, timeout=120, check=True)
    current = json.loads(result.stdout)
    if current.get("problems") or not current.get("projects"):
        raise SystemExit("Dependency audit failed or returned no projects: " + project)
    report["projects"].extend(current["projects"])
(destination / "dependency-audit.json").write_text(json.dumps(report, indent=2) + "\n")
if not report.get("projects"):
    raise SystemExit("Audit returned no projects; cannot assert a clean result.")
if report.get("problems"):
    raise SystemExit("Dependency audit reported problems.")
for project in report["projects"]:
    for framework in project.get("frameworks", []):
        for key in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(key, []):
                if package.get("vulnerabilities"):
                    raise SystemExit("Known dependency advisory: " + package["id"])
print("No known advisories reported for: " + ", ".join(projects))
