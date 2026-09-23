#!/usr/bin/env python3
"""Local, fail-closed recall gate. Never writes indices or promotes baselines."""
from __future__ import annotations

import argparse
import datetime as dt
import fcntl
import hashlib
import json
import math
import os
import platform
import plistlib
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

EXIT = {"ok": 0, "regression": 1, "incomparable": 2, "infrastructure": 3}
PROVENANCE_KEYS = (
    "eval_set_hash", "embedding_model", "embedding_dimensions",
    "embedding_max_sequence_length", "cross_encoder_model", "cross_encoder",
    "gate_calibration", "weight_codigo", "weight_sparse", "weight_resumen",
    "rrf_k", "chunking_contract_version", "index_short_type_declarations",
    "resumen_prompt_version",
)
LABEL = "com.rag-engine.eval-nocturno"
MODEL_KEYS = tuple(f"{section}__{key}" for section in ("OnnxBrain", "CrossEncoder")
                   for key in ("ModelPath", "VocabPath"))


class GateError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def require(condition, message, status="incomparable"):
    if not condition:
        raise GateError(status, message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def file_hash(path):
    fingerprint = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            fingerprint.update(block)
    return fingerprint.hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def load(path):
    return json.loads(Path(path).read_bytes())


def save(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")


def question_id(question):
    return digest(question.encode())[:16]


def valid_hash(value):
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value)


def cohort(dataset):
    require(isinstance(dataset, list) and dataset, "Dataset vacio o invalido")
    rows = {}
    for item in dataset:
        question, category = item.get("Question"), item.get("Category")
        require(isinstance(question, str) and question.strip(), "Pregunta ausente")
        require(isinstance(category, str) and category.strip(), "Categoria ausente")
        require(question not in rows, "Pregunta duplicada")
        sources = item.get("SourceFiles") or ([item["SourceFile"]] if item.get("SourceFile") else [])
        anchors = item.get("TargetContentContains")
        require(isinstance(anchors, list), "Anclas ausentes")
        rows[question] = {
            "id": question_id(question), "category": category,
            "source_file": item.get("SourceFile"), "answerable": bool(sources and anchors),
        }
    return rows


def validate_result(result, expected, dataset_hash):
    require(isinstance(result, dict), "Resultado no es objeto")
    provenance = result.get("provenance")
    require(isinstance(provenance, dict), "Procedencia ausente")
    missing = [key for key in PROVENANCE_KEYS if key not in provenance]
    require(not missing, "Procedencia incompleta: " + ", ".join(missing))
    require(provenance["eval_set_hash"] == dataset_hash[:12], "Hash de dataset distinto")
    require(type(result.get("top_k")) is int and result["top_k"] == 10, "Se requiere top_k=10")
    require(type(result.get("rerank")) is bool, "rerank ausente/invalido")
    require(isinstance(result.get("collection"), str) and result["collection"], "Coleccion ausente")
    require(type(result.get("min_score")) in (float, int) and
            math.isfinite(result["min_score"]), "min_score ausente/invalido")
    for key in ("embedding_dimensions", "embedding_max_sequence_length", "rrf_k", "chunking_contract_version"):
        require(type(provenance[key]) is int and provenance[key] > 0, f"Procedencia invalida: {key}")
    for key in ("weight_codigo", "weight_sparse", "weight_resumen"):
        require(type(provenance[key]) in (float, int) and math.isfinite(provenance[key])
                and provenance[key] >= 0, f"Procedencia invalida: {key}")
    for key in ("embedding_model", "resumen_prompt_version", "git_commit"):
        require(isinstance(provenance.get(key), str) and provenance[key], f"Procedencia invalida: {key}")
    require(type(provenance.get("git_dirty")) is bool and
            type(provenance["index_short_type_declarations"]) is bool, "Flags de procedencia invalidos")
    context = result.get("nightly_context")
    require(isinstance(context, dict) and context.get("schema_version") == 1,
            "Referencia sin nightly_context: no acredita modelos/config/indice")
    for key in ("models", "configuration", "collection"):
        require(isinstance(context.get(key), dict) and context[key], f"Falta contexto: {key}")
    require(set(context["models"]) == set(MODEL_KEYS) and
            all(valid_hash(value) for value in context["models"].values()), "Identidad de modelos incompleta")
    configuration = context["configuration"]
    require(all(valid_hash(configuration.get(key)) for key in ("appsettings_sha256", "shared_sha256"))
            and configuration.get("environment_policy") == 1
            and isinstance(configuration.get("architecture"), str) and configuration["architecture"],
            "Identidad de configuracion incompleta")
    collection = context["collection"]
    require(valid_hash(collection.get("sha256")) and type(collection.get("points")) is int
            and collection["points"] > 0 and isinstance(collection.get("vectors"), dict)
            and collection["vectors"] and "sparse_vectors" in collection,
            "Identidad de indice incompleta")
    if result["rerank"]:
        identity = provenance["cross_encoder"]
        require(isinstance(identity, dict) and
                identity.get("model_sha256") == context["models"]["CrossEncoder__ModelPath"] and
                identity.get("tokenizer_sha256") == context["models"]["CrossEncoder__VocabPath"] and
                identity.get("binary") == provenance["cross_encoder_model"],
                "Identidad de rerank ausente/distinta de los bytes verificados")
    else:
        require(provenance["cross_encoder_model"] is None and provenance["cross_encoder"] is None,
                "Identidad de rerank inesperada")
    rows = result.get("results")
    require(isinstance(rows, list) and len(rows) == len(expected), "Cobertura incompleta")
    indexed = {}
    for row in rows:
        question = row.get("question")
        require(question in expected and question not in indexed, "Pregunta extra o duplicada")
        spec = expected[question]
        require(row.get("category") == spec["category"], f"Categoria distinta: {spec['id']}")
        require(row.get("source_file") == spec["source_file"], f"Fuente distinta: {spec['id']}")
        require(row.get("retrieval_succeeded") is True, f"Busqueda no acreditada: {spec['id']}")
        for field in ("hit_any_at_k", "hit_full_at_k"):
            hits = row.get(field)
            if spec["answerable"]:
                require(isinstance(hits, dict) and set(hits) == {"1", "3", "5", "10"}
                        and all(type(v) is bool for v in hits.values()),
                        f"Hits invalidos: {spec['id']}/{field}")
                require(list(hits[k] for k in ("1", "3", "5", "10")) ==
                        sorted(hits[k] for k in ("1", "3", "5", "10")),
                        f"Hits no monotonos: {spec['id']}")
            else:
                require(hits == {}, f"Negativo incluido en recall: {spec['id']}")
        if spec["answerable"]:
            require(all(not row["hit_full_at_k"][k] or row["hit_any_at_k"][k]
                        for k in ("1", "3", "5", "10")), "Hit full sin hit any")
        indexed[question] = row
    return indexed


def compare(dataset_bytes, baseline, candidate):
    """Compare counts, not rounded percentages; report gross losses even if offset."""
    expected = cohort(json.loads(dataset_bytes))
    n = sum(row["answerable"] for row in expected.values())
    report = {"answerable": n, "negative": len(expected) - n,
              "dataset_sha256": digest(dataset_bytes)}
    try:
        require(n > 0, "No hay preguntas respondibles")
        a = validate_result(baseline, expected, digest(dataset_bytes))
        b = validate_result(candidate, expected, digest(dataset_bytes))
        changed = [key for key in ("collection", "top_k", "rerank", "min_score", "nightly_context")
                   if baseline.get(key) != candidate.get(key)]
        changed += [f"provenance.{key}" for key in PROVENANCE_KEYS
                    if baseline["provenance"][key] != candidate["provenance"][key]]
        require(not changed, "Configuracion distinta: " + ", ".join(changed))
        lost, gained = [], []
        for question, spec in expected.items():
            if not spec["answerable"]:
                continue
            before, after = a[question]["hit_any_at_k"]["10"], b[question]["hit_any_at_k"]["10"]
            if before != after:
                (lost if before else gained).append({"id": spec["id"], "category": spec["category"]})
        net = len(lost) - len(gained)
        return dict(report, status="regression" if net > 1 else "ok", lost=lost, gained=gained,
                    net_lost=net, lost_percentage_points=100 * net / n,
                    baseline_hits=sum(r["hit_any_at_k"].get("10", False) for r in a.values()),
                    candidate_hits=sum(r["hit_any_at_k"].get("10", False) for r in b.values()))
    except GateError as error:
        return dict(report, status=error.status, reason=str(error))


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def check_trust(root, expected_commit):
    commit = git(root, "rev-parse", "HEAD")
    dirty = bool(git(root, "status", "--porcelain"))
    if expected_commit:
        require(commit == expected_commit and not dirty,
                "Checkout distinto/sucio: reinstalar solo tras revisar el nuevo commit",
                "infrastructure")
    return {"git_commit": commit, "git_dirty": dirty}


def inventory(root):
    value = load(root / "infra/eval-nocturno/inventory.json")
    require(value.get("schema_version") == 1, "Version de inventario invalida")
    profiles = value.get("profiles")
    require(isinstance(profiles, list) and len(profiles) == 5, "Se requieren cinco perfiles")
    require(len({p["id"] for p in profiles}) == 5, "Perfiles duplicados")
    for p in profiles:
        require(p["id"].replace("-", "").replace(".", "").isalnum(), "ID de perfil invalido")
        require(type(p["rerank"]) is bool, "rerank invalido")
        for field in ("dataset", "baseline"):
            path = (root / p[field]).resolve()
            require(path.is_relative_to(root), "Ruta fuera del checkout")
            require(path.is_file(), f"Falta {p[field]}")
            require(file_hash(path) == p[f"{field}_sha256"], f"Hash distinto: {p[field]}")
        rows = cohort(load(root / p["dataset"]))
        n = sum(r["answerable"] for r in rows.values())
        require((n, len(rows) - n) == (p["answerable"], p["negative"]), f"Denominador distinto: {p['id']}")
    return profiles


def request(port, route, body=None):
    headers = {"Content-Type": "application/json"}
    key = os.environ.get("Qdrant__ApiKey")
    if key:
        headers["api-key"] = key
    req = urllib.request.Request(f"http://127.0.0.1:{port}/{route}", headers=headers,
                                 data=canonical(body) if body is not None else None)
    try:
        # Never send local credentials through an inherited HTTP proxy.
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(req, timeout=20) as response:
            value = json.load(response)
        require(value.get("status") == "ok" and "result" in value,
                "Respuesta Qdrant invalida", "infrastructure")
        return value["result"]
    except (OSError, urllib.error.URLError, ValueError) as error:
        raise GateError("infrastructure", f"Qdrant ausente/invalido en puerto {port}: {type(error).__name__}") from error


def snapshot(port, collection):
    require(collection.replace("-", "").replace("_", "").isalnum(), "Coleccion invalida")
    info = request(port, f"collections/{collection}")
    require(info.get("status") == "green" and info.get("points_count", 0) > 0,
            f"Coleccion no disponible: {collection}", "infrastructure")
    fingerprint, count, offset = hashlib.sha256(), 0, None
    while True:
        body = {"limit": 256, "with_payload": True, "with_vector": True}
        if offset is not None:
            body["offset"] = offset
        page = request(port, f"collections/{collection}/points/scroll", body)
        for point in page["points"]:
            fingerprint.update(canonical(point) + b"\n")
            count += 1
        next_offset = page.get("next_page_offset")
        require(next_offset is None or next_offset != offset, "Scroll sin avance", "infrastructure")
        offset = next_offset
        if offset is None:
            break
    require(count == info["points_count"], f"Indice cambio durante preflight: {collection}")
    return {"points": count, "sha256": fingerprint.hexdigest(),
            "vectors": info["config"]["params"]["vectors"],
            "sparse_vectors": info["config"]["params"].get("sparse_vectors")}


def model_paths(models_dir, config):
    paths = {}
    for section in ("OnnxBrain", "CrossEncoder"):
        for key in ("ModelPath", "VocabPath"):
            value = config[section][key].replace("${RAG_MODELS_DIR}", str(models_dir))
            path = Path(value).expanduser()
            require(path.is_absolute() and "${" not in value, "Ruta de modelo no resuelta", "infrastructure")
            if platform.machine().lower() not in ("arm64", "aarch64") and "_qint8_arm64" in path.name:
                path = path.with_name(path.name.replace("_qint8_arm64", ""))
            require(path.is_file(), f"Modelo/tokenizador ausente: {path}", "infrastructure")
            paths[f"{section}__{key}"] = path
    return paths


def environment(root, output, models, http_port, grpc_port):
    env = {key: os.environ[key] for key in ("HOME", "TMPDIR", "PATH") if key in os.environ}
    env.update({
        "DOTNET_ENVIRONMENT": "Production", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "RAG_SHARED_CONFIG_DIR": str(root / "config"), "RAG_LOGS_DIR": str(output / "logs"),
        "Qdrant__Host": "127.0.0.1", "Qdrant__HttpPort": str(http_port),
        "Qdrant__GrpcPort": str(grpc_port), "Audit__ActorId": "local-eval-nocturno",
        "Audit__DbPath": str(output / "audit.sqlite3"),
        "Ingestion__ResumenCachePath": str(output / "summary-cache.sqlite3"),
    })
    env.update({key: str(path) for key, path in models.items()})
    if os.environ.get("Qdrant__ApiKey"):
        env["Qdrant__ApiKey"] = os.environ["Qdrant__ApiKey"]
    return env


def execute(command, root, env, output, name, timeout):
    with (output / f"{name}.stdout").open("x") as stdout, (output / f"{name}.stderr").open("x") as stderr:
        try:
            process = subprocess.run(command, cwd=root, env=env, stdout=stdout, stderr=stderr, timeout=timeout)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise GateError("infrastructure", f"{name}: {type(error).__name__}; ver stderr local") from error
    require(process.returncode == 0, f"{name}: exit {process.returncode}; ver stderr/stdout local", "infrastructure")


def run(args):
    root = args.root.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    report = {"schema_version": 1, "started_at": dt.datetime.now(dt.timezone.utc).isoformat(),
              "profiles": [], "status": "infrastructure"}
    try:
        report["source"] = check_trust(root, args.expected_commit)
        profiles = inventory(root)
        config = load(root / "src/RagEngine.Cli/appsettings.json")
        models = model_paths(args.models_dir.resolve(), config)
        report["model_sha256"] = {key: file_hash(path) for key, path in models.items()}
        with socket.create_connection(("127.0.0.1", args.grpc_port), timeout=5):
            pass
        before = {p["collection"]: None for p in profiles}
        for collection in before:
            before[collection] = snapshot(args.http_port, collection)
        env = environment(root, output, models, args.http_port, args.grpc_port)
        # One build, isolated output; ignored per-machine appsettings are not part of this job.
        app = output / "app"
        execute([args.dotnet, "publish", "src/RagEngine.Cli", "--no-restore", "-c", "Release",
                 "-o", str(app), "--nologo", "-v", "quiet"], root, env, output, "build", 300)
        extra = [p.name for p in app.glob("appsettings.*.json")]
        require(not extra, "Config local no autorizada en publish: " + ", ".join(extra))
        require(file_hash(app / "appsettings.json") == file_hash(root / "src/RagEngine.Cli/appsettings.json"),
                "Config publicada distinta")
        configuration = {
            "appsettings_sha256": file_hash(app / "appsettings.json"),
            "shared_sha256": file_hash(root / "config/shared.appsettings.json"),
            "environment_policy": 1,
            "architecture": platform.machine().lower(),
        }
        report["binary_sha256"] = {p.name: file_hash(p) for p in app.glob("RagEngine.*.dll")}
        for profile in profiles:
            entry = {key: profile[key] for key in ("id", "collection", "dataset_sha256", "answerable", "negative")}
            try:
                command = [args.dotnet, str(app / "RagEngine.Cli.dll"), "eval", "--json",
                           "--eval-set", str(root / profile["dataset"]), "-c", profile["collection"],
                           "-k", "10", "--min-score", "0.1"]
                if profile["rerank"]:
                    command.append("--rerank")
                execute(command, root, env, output, profile["id"], 180)
                candidate = load(output / f"{profile['id']}.stdout")
                candidate["nightly_context"] = {
                    "schema_version": 1, "models": report["model_sha256"],
                    "configuration": configuration, "collection": before[profile["collection"]],
                }
                expected = cohort(load(root / profile["dataset"]))
                validate_result(candidate, expected, profile["dataset_sha256"])
                require(candidate["collection"] == profile["collection"] and
                        candidate["rerank"] == profile["rerank"] and candidate["min_score"] == 0.1,
                        "CLI no respeto el perfil solicitado")
                save(output / f"{profile['id']}.result.json", candidate)
                entry["provenance"] = candidate["provenance"]
                entry.update(compare((root / profile["dataset"]).read_bytes(),
                                     load(root / profile["baseline"]), candidate))
                entry["executed"] = True
                entry["result_sha256"] = file_hash(output / f"{profile['id']}.result.json")
            except GateError as error:
                entry.update(status=error.status, reason=str(error), executed=False)
            except (OSError, ValueError, KeyError, TypeError) as error:
                entry.update(status="infrastructure", reason=f"Salida invalida: {type(error).__name__}", executed=False)
            report["profiles"].append(entry)
        after = {collection: snapshot(args.http_port, collection) for collection in before}
        require(before == after, "Indice cambio durante los evals; resultados incomparables")
        require(report["model_sha256"] == {key: file_hash(path) for key, path in models.items()},
                "Modelos cambiaron durante los evals")
        require(configuration["shared_sha256"] == file_hash(root / "config/shared.appsettings.json"),
                "Config compartida cambio durante los evals")
        inventory(root)
        require(report["source"] == check_trust(root, args.expected_commit), "Checkout cambio durante los evals")
        report["collections_unchanged"] = True
        report["collections"] = before
        report["status"] = max((row["status"] for row in report["profiles"]), key=EXIT.get)
    except GateError as error:
        report.update(status=error.status, reason=str(error))
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        report.update(status="infrastructure", reason=f"Preflight/runner: {type(error).__name__}: {error}")
    report["elapsed_seconds"] = round(time.monotonic() - started, 2)
    save(output / "report.json", report)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    if report["status"] != "ok":
        print(f"ALARMA {report['status']}: {output / 'report.json'}", file=sys.stderr)
    return EXIT[report["status"]]


def schedule(root, directory, python, dotnet, commit, models_dir, hour, minute):
    return {
        "Label": LABEL,
        "ProgramArguments": [python, str(directory / "runner.py"), "run", "--root", str(root),
                             "--expected-commit", commit, "--dotnet", dotnet,
                             "--models-dir", str(models_dir)],
        "StartCalendarInterval": {"Hour": hour, "Minute": minute},
        "WorkingDirectory": str(directory),
        "StandardOutPath": str(directory / "schedule.stdout"),
        "StandardErrorPath": str(directory / "schedule.stderr"),
        "EnvironmentVariables": {"PATH": os.environ.get("PATH", os.defpath)},
        "ProcessType": "Standard",
    }


def install(args):
    require(sys.platform == "darwin", "El schedule de esta maquina requiere launchd", "infrastructure")
    root = args.root.resolve()
    source = check_trust(root, git(root, "rev-parse", "HEAD"))
    inventory(root)
    require(0 <= args.hour <= 23 and 0 <= args.minute <= 59, "Horario invalido")
    directory = Path.home() / "Library/Application Support/rag-engine/eval-nocturno"
    directory.mkdir(parents=True, exist_ok=True)
    plist = Path.home() / "Library/LaunchAgents" / f"{LABEL}.plist"
    require(not plist.exists(), f"Schedule ya existe: deshabilitar y archivar {plist} antes de reinstalar")
    shutil.copyfile(__file__, directory / "runner.py")
    value = schedule(root, directory, sys.executable, args.dotnet, source["git_commit"],
                     args.models_dir.resolve(), args.hour, args.minute)
    plist.parent.mkdir(parents=True, exist_ok=True)
    with plist.open("xb") as stream:
        plistlib.dump(value, stream)
    subprocess.run(["launchctl", "bootstrap", f"gui/{os.getuid()}", str(plist)], check=True)
    subprocess.run(["launchctl", "print", f"gui/{os.getuid()}/{LABEL}"], check=True)
    print(f"Schedule instalado: {plist}; commit {source['git_commit']}")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("run", "install", "compare"))
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--expected-commit")
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or "dotnet")
    parser.add_argument("--models-dir", type=Path,
                        default=Path(os.environ.get("RAG_MODELS_DIR", str(Path.home() / "models"))))
    parser.add_argument("--http-port", type=int, default=6333)
    parser.add_argument("--grpc-port", type=int, default=6334)
    parser.add_argument("--hour", type=int, default=3)
    parser.add_argument("--minute", type=int, default=0)
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--candidate", type=Path)
    args = parser.parse_args()
    if args.command == "compare":
        if not all((args.dataset, args.baseline, args.candidate)):
            parser.error("compare requiere --dataset, --baseline y --candidate")
        try:
            report = compare(args.dataset.read_bytes(), load(args.baseline), load(args.candidate))
        except (OSError, ValueError, KeyError, TypeError, GateError) as error:
            report = {"status": "incomparable", "reason": str(error)}
        print(json.dumps(report, ensure_ascii=False, indent=2))
        if report["status"] != "ok":
            print(f"ALARMA {report['status']}", file=sys.stderr)
        return EXIT[report["status"]]
    if args.command == "install":
        return install(args)
    if args.output is None:
        stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
        args.output = args.root / "logs/eval-nocturno" / stamp
    lock_path = args.root / "logs/eval-nocturno/runner.lock"
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            print("ALARMA infrastructure: otro eval nocturno esta en curso", file=sys.stderr)
            return EXIT["infrastructure"]
        return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
