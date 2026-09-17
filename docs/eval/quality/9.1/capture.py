#!/usr/bin/env python3
"""Captura A/B local de 9.1. Solo lecturas de Qdrant; sin ingesta, LLM ni cambios del indice."""
import hashlib
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
from urllib.request import Request, urlopen

from verify import HERE, ROOT, SETS, digest, require, tree_digest, verify_ab, write_json


def qdrant(path, data=None):
    request = Request("http://localhost:6333" + path,
                      data=json.dumps(data).encode() if data is not None else None,
                      headers={"Content-Type": "application/json"})
    with urlopen(request, timeout=120) as response:
        return json.load(response)["result"]


def fingerprint(collection):
    info = qdrant(f"/collections/{collection}")
    sha = hashlib.sha256()
    offset = None
    seen = set()
    while True:
        page = qdrant(f"/collections/{collection}/points/scroll",
                      {"limit": 128, "offset": offset, "with_payload": True, "with_vector": True})
        for point in page["points"]:
            point_id = str(point["id"])
            require(point_id not in seen, f"{collection}: ID repetido en scroll")
            seen.add(point_id)
            sha.update(json.dumps(point, sort_keys=True, separators=(",", ":")).encode())
            sha.update(b"\n")
        offset = page["next_page_offset"]
        if offset is None:
            break
    count = qdrant(f"/collections/{collection}/points/count", {"exact": True})["count"]
    require(len(seen) == count and count > 0, f"{collection}: conteo inestable/vacio")
    return {"points": count, "points_sha256": sha.hexdigest(),
            "schema_sha256": hashlib.sha256(json.dumps(
                {"config": info["config"], "payload_schema": info["payload_schema"]},
                sort_keys=True).encode()).hexdigest()}


def execute(command, cwd, env, log):
    result = subprocess.run(command, cwd=cwd, env=env, text=True, capture_output=True, timeout=600)
    log.write_text(result.stdout + result.stderr)
    require(result.returncode == 0, f"Comando fallo ({result.returncode}); ver {log}\n{result.stdout[-2000:]}{result.stderr[-2000:]}")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--replica", action="store_true", help="Segunda pareja independiente; conserva la captura inicial")
    args = parser.parse_args()
    directory = HERE / "replica" if args.replica else HERE
    directory.mkdir(exist_ok=True)
    require(not (directory / "protocol.json").exists(), "Ya existe un protocolo: no sobrescribir ni corridas parciales")
    require(not subprocess.check_output(["git", "diff", "HEAD", "--", "src", "config"], cwd=ROOT).strip(),
            "El producto tiene cambios sin commit")
    control = subprocess.check_output(["git", "rev-parse", "bb9b11f"], cwd=ROOT, text=True).strip()
    candidate = subprocess.check_output(["git", "rev-parse", "9690c49"], cwd=ROOT, text=True).strip()
    model_root = Path(os.environ.get("RAG_MODELS_DIR", "~/models")).expanduser().resolve()
    config = json.loads((ROOT / "src/RagEngine.Cli/appsettings.json").read_text())
    models = {}
    for section in ("OnnxBrain", "CrossEncoder"):
        for key in ("ModelPath", "VocabPath"):
            path = Path(config[section][key].replace("${RAG_MODELS_DIR}", str(model_root)))
            models[f"{section}.{key}"] = {"path": str(path), "sha256": digest(path)}
    protocol = {
        "item": "9.1-puertos-del-vector-store",
        "control_commit": control, "candidate_commit": candidate,
        "source_sha256": tree_digest(ROOT, ["src", "config"]),
        "test_sha256": tree_digest(ROOT, ["tests/RagEngine.Core.Tests"]),
        "models": models,
        "parameters": {"top_k": 10, "min_score": 0.1, "rerank": False, "two_hop": False,
                       "context": "control sin restriccion local; candidato RetrievalContext.Local",
                       "criterion": "identidad por pregunta de hit_any/full en 1,3,5,10; negativos: salida observable identica"},
        "sets": {},
    }
    for name, collection in SETS.items():
        dataset = ROOT / f"docs/eval/{name}.eval-set.json"
        print(f"Congelando {name}, solo lectura...", flush=True)
        protocol["sets"][name] = {"collection": collection, "dataset_sha256": digest(dataset),
                                  "n": len(json.loads(dataset.read_text())), "before": fingerprint(collection)}
    # El protocolo queda persistido antes de ejecutar CUALQUIERA de los dos brazos.
    write_json(directory / "protocol.json", protocol)
    evidence = {"protocol_sha256": digest(directory / "protocol.json"), "sets": {}}
    with tempfile.TemporaryDirectory(prefix="rag-9.1-ab-") as temp:
        temp = Path(temp)
        worktrees = []
        try:
            env = {key: value for key, value in os.environ.items()
                   if key in {"PATH", "HOME", "TMPDIR", "DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "LANG"}}
            env.update({"RAG_MODELS_DIR": str(model_root), "DOTNET_ENVIRONMENT": "Production",
                        "Ingestion__ResumenCachePath": str(temp / "isolated-cache.sqlite3"),
                        "RAG_LOGS_DIR": str(temp / "logs")})
            for arm, commit in (("control", control), ("candidate", candidate)):
                worktree = temp / arm
                subprocess.run(["git", "worktree", "add", "--detach", str(worktree), commit],
                               cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
                worktrees.append(worktree)
                result = subprocess.run(["dotnet", "build", "src/RagEngine.Cli", "--no-restore", "--nologo"],
                                        cwd=worktree, env=env, text=True, capture_output=True)
                if result.returncode:
                    require("NETSDK1004" in result.stdout, "Fallo de build distinto de assets ausentes:\n" + result.stdout + result.stderr)
                execute(["dotnet", "build", "src/RagEngine.Cli", "--nologo", "-warnaserror"],
                        worktree, env, temp / f"{arm}.build.log")
                require(tree_digest(worktree, ["config"]) == tree_digest(ROOT, ["config"]),
                        f"{arm}: configuracion compartida distinta")
                require(digest(worktree / "src/RagEngine.Cli/appsettings.json") ==
                        digest(ROOT / "src/RagEngine.Cli/appsettings.json"), f"{arm}: config CLI distinta")
                for name, collection in SETS.items():
                    print(f"Evaluando {arm}/{name}...", flush=True)
                    result = execute(["dotnet", "src/RagEngine.Cli/bin/Debug/net10.0/RagEngine.Cli.dll",
                                      "eval", "--eval-set", f"docs/eval/{name}.eval-set.json",
                                      "--collection", collection, "--top-k", "10", "--min-score", "0.1",
                                      "--dump-hits", "--json"], worktree, env, temp / f"{arm}.{name}.log")
                    require("Falló la búsqueda" not in result.stderr, f"{arm}/{name}: eval absorbio un error")
                    run = json.loads(result.stdout)
                    path = directory / f"{name}.{arm}.json"
                    write_json(path, run)
                    evidence["sets"].setdefault(name, {})[f"{arm}_sha256"] = digest(path)
            for name, collection in SETS.items():
                print(f"Verificando indice intacto {name}...", flush=True)
                evidence["sets"][name]["after"] = fingerprint(collection)
                evidence["sets"][name]["models_after"] = {
                    key: {"path": record["path"], "sha256": digest(record["path"])}
                    for key, record in models.items()
                }
            write_json(directory / "capture.json", evidence)
            verify_ab(directory)
        finally:
            for worktree in worktrees:
                subprocess.run(["git", "worktree", "remove", str(worktree)], cwd=ROOT, check=True)
    print("PASS captura A/B sin mutacion de indices")


if __name__ == "__main__":
    main()
