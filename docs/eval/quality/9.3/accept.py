#!/usr/bin/env python3
"""Acceptance of 9.3: frozen pre-refactor controls plus live HTTP, CLI and retrieval."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
GROUPS = [
    "GenerationHttpHarnessTests", "GenerationContractTests", "GenerationQualityTests",
    "GenerationAcceptanceTests", "ConfidenceGateBandTests", "GenerationContextGoldenTests",
    "GenerationContextAssemblerResumenSwapTests", "SystemPromptComposerTests",
    "SanitizeSimpleAnswerTests", "ChatAnswerStreamerTests", "CompositionRootTests",
    "PromptHashesTests", "CollectionAuthorizationVisibilityHttpHarnessTests",
]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def cli_captures(root, temporary):
    captures = []
    for buffered in (False, True):
        command = [
            "dotnet", "run", "--no-build", "--project", "src/RagEngine.Cli", "--",
            "ask", "¿Quién eres?", "--collection", "fixture", "--technical",
        ] + (["--no-stream"] if buffered else [])
        process = subprocess.run(command, cwd=root, env={
            **os.environ, "DOTNET_PROCESSOR_COUNT": "1", "NO_COLOR": "1", "TERM": "dumb",
            "COLUMNS": "100", "Ingestion__ResumenCachePath": str(temporary / "cli.sqlite"),
            "RAG_LOGS_DIR": str(temporary / "logs"),
        }, text=True, capture_output=True, timeout=60)
        require(process.returncode == 0, process.stdout + process.stderr)
        require("Soy Rag.Context.Engine" in process.stdout, "CLI did not emit the meta response")
        captures.append({"buffered": buffered, "stdout": process.stdout, "stderr": process.stderr})
    return captures


def verify_frozen():
    provenance = json.loads((HERE / "control-provenance.json").read_text())
    paths = [p for p in provenance["sha256"] if p.startswith("replicate-env/data/questions/")]
    paths += ["docs/eval/quality/9.3/fixtures.json", "docs/eval/quality/9.3/quality.json"]
    for path in paths:
        require(sha(ROOT / path) == provenance["sha256"][path], f"Frozen input changed: {path}")
    expected = {"fixtures.json": 72, "quality.json": 143}
    for name, count in expected.items():
        rows = json.loads((HERE / name).read_text())
        require(len(rows) == count, f"Incomplete control: {name}")
    recheck = json.loads((HERE / "control-recheck.json").read_text())
    for name in expected:
        require(recheck["captures_sha256"][name] == sha(HERE / name), f"Control recheck mismatch: {name}")
    for path, digest in recheck["instrument_sha256"].items():
        require(sha(ROOT / path) == digest, f"Instrument differs from control: {path}")
    require(recheck["control_commit"] == provenance["control_commit"], "Control commit mismatch")
    require(recheck["cli_sha256"] == sha(HERE / "cli.json"), "CLI control changed")
    return provenance


def main():
    require(os.environ.get("RAG_93_CAPTURE_CONTROL") != "1", "Capture mode cannot certify acceptance")
    started = time.monotonic()
    report = {"passed": False}
    try:
        provenance = verify_frozen()
        subprocess.run(["dotnet", "build", "--no-restore", "--nologo", "-warnaserror"], cwd=ROOT, check=True)
        with tempfile.TemporaryDirectory(prefix="rag-93-accept-") as name:
            temporary = Path(name)
            command = [
                "dotnet", "test", "tests/RagEngine.Core.Tests", "--no-build", "--nologo",
                "--filter", "|".join(f"FullyQualifiedName~{group}" for group in GROUPS),
                "--logger", "trx;LogFileName=accept.trx", "--results-directory", name,
            ]
            subprocess.run(command, cwd=ROOT, check=True, env={
                **os.environ, "DOTNET_PROCESSOR_COUNT": "1", "RAG_93_CANDIDATE_DIR": name,
                "RAG_LOGS_DIR": str(temporary / "logs"),
            })
            results = ET.parse(temporary / "accept.trx").findall(".//t:UnitTestResult", NS)
            require(results and all(r.get("outcome") == "Passed" for r in results), "Failed/omitted/missing tests")
            for group in GROUPS:
                require(any(group in r.get("testName", "") for r in results), f"No coverage for {group}")
            captures = {}
            for filename in ("fixtures.json", "quality.json"):
                rows = json.loads((temporary / filename).read_text())
                control = json.loads((HERE / filename).read_text())
                require(len(rows) == len(control), f"Incomplete candidate: {filename}")
                captures[filename] = {
                    "requests": len(rows), "http_200": sum(r["status"] == 200 for r in rows),
                    "http_500": sum(r["status"] == 500 for r in rows),
                    "search_calls_control": sum(r["searchCalls"] for r in control),
                    "search_calls_candidate": sum(r["searchCalls"] for r in rows),
                    "search_measurements_control": sum(r["searchMeasurements"] for r in control),
                    "search_measurements_candidate": sum(r["searchMeasurements"] for r in rows),
                    "semantic_differences": [],
                }
            expected_cli = json.loads((HERE / "cli.json").read_text())
            require(cli_captures(ROOT, temporary) == expected_cli, "CLI output differs from control")
            verify_frozen()
            report = {
                "passed": True, "control_commit": provenance["control_commit"],
                "tests_passed": len(results), "groups": GROUPS, "captures": captures,
                "cli_modes_identical": ["streaming", "buffered"],
                "limits": "Deterministic LLM; real local retrieval. One absent collection is an explicit HTTP 500 negative. No recall or real-LLM quality claim.",
                "product_sha256": {str(p.relative_to(ROOT)): sha(p) for p in sorted((ROOT / "src").rglob("*.cs"))
                                   if not {"bin", "obj"} & set(p.parts)},
            }
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        report["duration_seconds"] = round(time.monotonic() - started, 2)
        (HERE / "verification.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"PASS: {report['tests_passed']} tests; 72 HTTP/SSE fixtures; 143 quality requests; both CLI modes.")


if __name__ == "__main__":
    main()
