"""Generate a dependency-only CycloneDX inventory from locked NuGet packages.

This is NOT a final binary/runtime SBOM or artifact attestation. Those remain release gates.
"""
import json
import pathlib

root = pathlib.Path(__file__).resolve().parents[1]
components = {}
for lockfile in [root / "src/WindPlay.Protocol/packages.lock.json", root / "src/WindPlay.App/packages.lock.json"]:
    for dependencies in json.loads(lockfile.read_text())["dependencies"].values():
        for name, item in dependencies.items():
            if item.get("type") == "Project" or not item.get("resolved"):
                continue
            version = item["resolved"]
            purl = f"pkg:nuget/{name}@{version}"
            components[purl] = {"type": "library", "name": name, "version": version, "purl": purl,
                                "bom-ref": purl}
output = root / "artifacts/security"
output.mkdir(parents=True, exist_ok=True)
(output / "dependency-sbom.cdx.json").write_text(json.dumps({
    "bomFormat": "CycloneDX", "specVersion": "1.6", "version": 1,
    "components": sorted(components.values(), key=lambda item: item["purl"]),
}, indent=2) + "\n")
print(f"Wrote dependency-only inventory for {len(components)} locked packages.")
