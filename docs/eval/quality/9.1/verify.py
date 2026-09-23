#!/usr/bin/env python3
"""Aceptacion 9.1: evidencia A/B estricta y pruebas reales, nunca un eval vacio."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[4]
HERE = Path(__file__).resolve().parent
SETS = {
    "innovapp-docs": "innovapp-docs",
    "bsuite-repo": "bsuite-repo",
    "bsuite-auditorias": "bsuite-auditorias-test",
    "tickets-microservice": "micro-repo",
}
CUTOFFS = {"1", "3", "5", "10"}
CONTRACTS = {
    "ModuleBoundaryTests": 25,
    "RetrievalAuthorizationContextHttpHarnessTests": 19,
    "RetrievalOptionsCompileContractTests": 2,
    "VectorStoreResumeTests": 3,
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    sha = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def tree_digest(root, paths):
    names = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", *paths], cwd=root
    ).decode().split("\0")
    files = {name: digest(root / name) for name in sorted(set(names)) if name}
    require(files, "Arbol fuente vacio")
    return hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()


def compare(items, control, candidate, collection, dataset_hash):
    require(items, "Dataset vacio")
    questions = [item["Question"] for item in items]
    require(len(set(questions)) == len(questions), "Preguntas duplicadas en dataset")
    summaries = []
    for label, run in (("control", control), ("candidato", candidate)):
        require(run["collection"] == collection, f"{label}: coleccion incorrecta")
        require(run["top_k"] == 10 and run["min_score"] == 0.1 and run["rerank"] is False,
                f"{label}: parametros distintos del protocolo")
        require(run["provenance"]["eval_set_hash"] == dataset_hash[:12], f"{label}: hash del dataset")
        rows = run["results"]
        require(len(rows) == len(items), f"{label}: n incompleto")
        require([row["question"] for row in rows] == questions, f"{label}: preguntas omitidas/duplicadas/reordenadas")
        for item, row in zip(items, rows):
            require(row["category"] == item["Category"], f"{label}: categoria distinta")
            require(row["source_file"] == item.get("SourceFile"), f"{label}: ancla distinta")
            anchored = bool((item.get("SourceFiles") or item.get("SourceFile")) and item["TargetContentContains"])
            for field in ("hit_any_at_k", "hit_full_at_k"):
                require(set(row[field]) == (CUTOFFS if anchored else set()), f"{label}: cobertura de {field}")
                require(all(type(value) is bool for value in row[field].values()), f"{label}: hits no booleanos")
            hits = row["hits"]
            # Los cuatro indices congelados tienen candidatos para estas consultas.
            # Una excepcion absorbida por EvalCommand no debe parecer un empate.
            require(0 < len(hits) <= 10, f"{label}: retrieval vacio/sin evidencia")
            require([hit["rank"] for hit in hits] == list(range(1, len(hits) + 1)), f"{label}: ranks invalidos")
            require(math.isfinite(row["top_score"]), f"{label}: score invalido")
            for hit in hits:
                require(math.isfinite(hit["score"]), f"{label}: score de hit invalido")
                require(type(hit["is_target_file"]) is bool and type(hit["matches_anchor"]) is bool,
                        f"{label}: forma de hit invalida")
                require(not hit["matches_anchor"] or hit["is_target_file"], f"{label}: match fuera del ancla")
        summaries.append(sum(row["hit_any_at_k"].get("10", False) for row in rows))
    ignored = {"git_commit", "git_dirty", "eval_set_path"}
    require({k: v for k, v in control["provenance"].items() if k not in ignored}
            == {k: v for k, v in candidate["provenance"].items() if k not in ignored},
            "Procedencia de evaluacion incompatible")
    differences = []
    for before, after in zip(control["results"], candidate["results"]):
        for field in ("hit_any_at_k", "hit_full_at_k"):
            if before[field] != after[field]:
                differences.append(f"{before['question']}: {field}")
        # Los negativos carecen de ground truth en el eval existente: comparar lo
        # observable, no convertir sus mapas vacios en un supuesto acierto.
        if not before["hit_any_at_k"]:
            if before["hits"] != after["hits"] or before["top_score"] != after["top_score"]:
                differences.append(f"{before['question']}: salida sin ancla")
    require(not differences, "Regresion/cambio por pregunta: " + "; ".join(differences))
    return {
        "n": len(items),
        "anchored": sum(bool(row["hit_any_at_k"]) for row in control["results"]),
        "unanchored": sum(not row["hit_any_at_k"] for row in control["results"]),
        "control_hit_any_10": summaries[0], "candidate_hit_any_10": summaries[1],
        "differences": differences,
    }


def verify_ab(directory=HERE):
    protocol = json.loads((directory / "protocol.json").read_text())
    evidence = json.loads((directory / "capture.json").read_text())
    require(protocol["control_commit"].startswith("bb9b11f"), "Control no es pre-9.1")
    require(protocol["candidate_commit"].startswith("9690c49"), "Candidato no es el producto verificado")
    require(set(protocol["sets"]) == set(SETS), "Faltan sets en el protocolo")
    require(set(evidence["sets"]) == set(SETS), "Faltan sets en la evidencia")
    require(protocol["source_sha256"] == tree_digest(ROOT, ["src", "config"]), "Producto distinto del medido")
    require(protocol["test_sha256"] == tree_digest(ROOT, ["tests/RagEngine.Core.Tests"]), "Tests distintos del protocolo")
    require(evidence["protocol_sha256"] == digest(directory / "protocol.json"), "Protocolo alterado")
    results = {}
    errors = []
    for name, collection in SETS.items():
        spec = protocol["sets"][name]
        record = evidence["sets"][name]
        dataset = ROOT / f"docs/eval/{name}.eval-set.json"
        require(digest(dataset) == spec["dataset_sha256"], f"{name}: dataset alterado")
        require(spec["collection"] == collection, f"{name}: coleccion del protocolo")
        require(record["after"] == spec["before"], f"{name}: indice alterado durante A/B")
        require(spec["before"]["points"] > 0, f"{name}: indice vacio")
        require(record["models_after"] == protocol["models"], f"{name}: modelos alterados")
        runs = {}
        for arm, commit in (("control", protocol["control_commit"]), ("candidate", protocol["candidate_commit"])):
            path = directory / f"{name}.{arm}.json"
            require(digest(path) == record[f"{arm}_sha256"], f"{name}: artefacto {arm} alterado")
            run = json.loads(path.read_text())
            require(commit.startswith(run["provenance"]["git_commit"]), f"{name}: commit {arm} incorrecto")
            require(run["provenance"]["git_dirty"] is False, f"{name}: arbol {arm} sucio")
            runs[arm] = run
        items = json.loads(dataset.read_text())
        require(len(items) == spec["n"], f"{name}: n del protocolo")
        try:
            results[name] = compare(items, runs["control"], runs["candidate"], collection, spec["dataset_sha256"])
            print(f"{directory.name}/{name}: {len(items)} preguntas, 0 cambios")
        except ValueError as error:
            errors.append(f"{directory.name}/{name}: {error}")
    require(not errors, "\n".join(errors))
    return results


def run_contracts():
    selector = "|".join(f"FullyQualifiedName~{name}" for name in CONTRACTS)
    with tempfile.TemporaryDirectory(prefix="rag-9.1-trx-") as temp:
        subprocess.run([
            "dotnet", "test", "tests/RagEngine.Core.Tests", "--no-restore", "--nologo",
            "--filter", selector, "--logger", "trx;LogFileName=acceptance.trx", "--results-directory", temp,
        ], cwd=ROOT, check=True)
        root = ET.parse(Path(temp) / "acceptance.trx").getroot()
        rows = root.findall(".//{*}UnitTestResult")
        require(rows and all(row.attrib["outcome"] == "Passed" for row in rows), "Tests omitidos/fallidos o cero tests")
        counts = {}
        for name, minimum in CONTRACTS.items():
            counts[name] = sum(f".{name}." in row.attrib["testName"] for row in rows)
            require(counts[name] >= minimum, f"Cobertura insuficiente: {name}")
        return {"total": len(rows), "classes": counts,
                "tests": [{"name": row.attrib["testName"], "outcome": row.attrib["outcome"]} for row in rows]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-tests", action="store_true", help="Aceptacion completa: exige Qdrant y ejecuta los contratos")
    args = parser.parse_args()
    pipeline = (ROOT / "src/RagEngine.Core/Pipeline/DefaultIngestionPipeline.cs").read_text()
    require(not re.search(r"^\s*(?:global\s+)?using\s+(?:\w+\s*=\s*)?Qdrant[.;]", pipeline, re.M),
            "El pipeline importa Qdrant")
    results = {}
    failures = []
    tests = None
    if args.run_tests:
        tests = run_contracts()
    # Ambas parejas se conservan: nunca elegir la corrida favorable ni ocultar el
    # fallo de una replica. Los errores de infraestructura/formato tambien fallan.
    for directory in (HERE, HERE / "replica"):
        try:
            results[directory.name] = verify_ab(directory)
        except ValueError as error:
            failures.append(str(error))
    if args.run_tests:
        write_json(HERE / "verification.json", {
            "passed": not failures, "ab": results, "failures": failures, "contracts": tests,
            "protocol_sha256": digest(HERE / "protocol.json"),
            "replica_protocol_sha256": digest(HERE / "replica/protocol.json"),
        })
    require(not failures, "\n".join(failures))
    if args.run_tests:
        print(f"PASS 9.1: A/B completo y {tests['total']} contratos reales, sin omisiones")
    else:
        print("PASS A/B archivado solamente; no sustituye --run-tests para cerrar")


if __name__ == "__main__":
    main()
