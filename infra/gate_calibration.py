"""Fail-closed paired comparison of gate scores AND adjudicated answers (12.10)."""
from __future__ import annotations

import hashlib
import json
import math
import re
from pathlib import Path


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def valid_hash(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def validate_identity(identity):
    require(isinstance(identity, dict), "Missing cross_encoder identity")
    for key in ("model_sha256", "tokenizer_sha256"):
        require(valid_hash(identity.get(key)), f"Invalid identity: {key}")
    for key in ("binary", "architecture"):
        require(isinstance(identity.get(key), str) and identity[key].strip(),
                f"Missing identity: {key}")
    require("/" not in identity["binary"] and "\\" not in identity["binary"],
            "binary must be a filename, not a path")
    require(type(identity.get("stable_gate_score")) is bool, "Missing StableGateScore")
    for key, minimum in (("max_sequence_length", 5), ("batch_size", 1)):
        require(type(identity.get(key)) is int and identity[key] >= minimum,
                f"Invalid identity: {key}")


def finite_number(value):
    return type(value) in (float, int) and math.isfinite(value)


def validate_calibration(calibration, identity):
    require(isinstance(calibration, dict), "Missing calibration")
    require(calibration.get("cross_encoder") == identity, "Calibration/loaded binary mismatch")
    low, high = calibration.get("low_confidence_threshold"), calibration.get("high_confidence_threshold")
    require(finite_number(low) and finite_number(high) and 0 <= low < high <= 1,
            "Invalid thresholds: require finite 0 <= low < high <= 1")
    return low, high


def band(score, low, high, result_count):
    if result_count == 0 or score < low:
        return "sin_grounding"
    return "media" if score < high else "alta"


def question_key(row):
    return row["coleccion"], row["pregunta"]


def validate_arm(arm, labeled, labeled_hash, allow_fixtures):
    require(arm.get("schema_version") == 1, "Unsupported artifact schema")
    require(arm.get("kind") == "measured" or (allow_fixtures and arm.get("kind") == "fixture"),
            "Measured evidence required; fixtures do not calibrate a binary")
    require(arm.get("labeled_set_sha256") == labeled_hash, "Labeled-set hash mismatch")
    identity = arm.get("cross_encoder")
    validate_identity(identity)
    low, high = validate_calibration(arm.get("calibration"), identity)
    controls = arm.get("controls")
    require(isinstance(controls, dict), "Missing frozen controls")
    for key in ("corpus_sha256", "retrieval_config_sha256", "prompts_sha256", "generation_config_sha256"):
        require(valid_hash(controls.get(key)), f"Missing control: {key}")
    rows = arm.get("rows")
    require(isinstance(rows, list) and rows, "Missing answer/score rows")
    indexed = {}
    for row in rows:
        key = question_key(row)
        require(key not in indexed, f"Duplicate question: {key}")
        require(key in labeled, f"Unexpected question: {key}")
        for field in ("etiqueta", "categoria"):
            require(row.get(field) == labeled[key][field], f"Changed {field}: {key}")
        require(row.get("retrieval_succeeded") is True, f"Retrieval failed/missing: {key}")
        score, count = row.get("score"), row.get("result_count")
        require(finite_number(score) and 0 <= score <= 1, f"Invalid score: {key}")
        require(type(count) is int and count >= 0 and (count != 0 or score == 0),
                f"Invalid result_count: {key}")
        if count > 0:
            require(row.get("cross_encoder") == identity, f"Mixed/missing score identity: {key}")
        require(row.get("answer_label") in ("correcta", "fabricacion", "abstencion", "incorrecta"),
                f"Missing adjudicated answer label: {key}")
        require(isinstance(row.get("answer"), str) and row["answer"].strip(), f"Missing answer: {key}")
        require(row.get("reviewed_answer_sha256") == sha256(row["answer"].encode()),
                f"Adjudication does not match answer bytes: {key}")
        require(isinstance(row.get("judgement"), str) and row["judgement"].strip(),
                f"Missing adjudication rationale: {key}")
        require(not (row["etiqueta"] == "ausente" and row["answer_label"] == "correcta"),
                f"Absent corpus answer cannot be labeled correct: {key}")
        indexed[key] = dict(row, band=band(score, low, high, count))
    require(indexed.keys() == labeled.keys(), "Incomplete paired question coverage")
    return indexed


def compare(labeled_bytes, control, candidate, *, allow_fixtures=False):
    questions = json.loads(labeled_bytes)
    require(isinstance(questions, list) and questions, "Empty labeled-set")
    labeled = {question_key(row): row for row in questions}
    require(len(labeled) == len(questions), "Duplicate labeled-set questions")
    require({row["etiqueta"] for row in questions} == {"presente", "ausente"},
            "Both present and absent cohorts are required")
    digest = sha256(labeled_bytes)
    a = validate_arm(control, labeled, digest, allow_fixtures)
    b = validate_arm(candidate, labeled, digest, allow_fixtures)
    require(control["kind"] == candidate["kind"], "Cannot mix fixtures and measured evidence")
    require(control["controls"] == candidate["controls"], "Controls changed between arms")
    pairs = []
    for key in labeled:
        before, after = a[key], b[key]
        pairs.append({
            "coleccion": key[0], "pregunta": key[1], "etiqueta": labeled[key]["etiqueta"],
            "control_band": before["band"], "candidate_band": after["band"],
            "control_score": before["score"], "candidate_score": after["score"],
            "control_label": before["answer_label"], "candidate_label": after["answer_label"],
            "crossed_band": before["band"] != after["band"],
            "new_fabrication": after["answer_label"] == "fabricacion" and before["answer_label"] != "fabricacion",
            "lost_correct": before["answer_label"] == "correcta" and after["answer_label"] != "correcta",
        })
    correct_a = sum(row["answer_label"] == "correcta" for row in a.values())
    correct_b = sum(row["answer_label"] == "correcta" for row in b.values())
    new_fabrications = sum(row["new_fabrication"] for row in pairs)
    return {
        "schema_version": 1, "kind": control["kind"], "labeled_set_sha256": digest,
        "n": len(pairs),
        "present": sum(row["etiqueta"] == "presente" for row in questions),
        "absent": sum(row["etiqueta"] == "ausente" for row in questions),
        "control_calibration": control["calibration"], "candidate_calibration": candidate["calibration"],
        "controls": control["controls"],
        "correct_control": correct_a, "correct_candidate": correct_b,
        "abstentions_control": sum(row["answer_label"] == "abstencion" for row in a.values()),
        "abstentions_candidate": sum(row["answer_label"] == "abstencion" for row in b.values()),
        "new_fabrications": new_fabrications,
        "accepted": new_fabrications == 0 and correct_b >= correct_a,
        "pairs": pairs,
    }


def compare_files(labeled_path, control_path, candidate_path, *, allow_fixtures=False):
    return compare(Path(labeled_path).read_bytes(),
                   json.loads(Path(control_path).read_bytes()),
                   json.loads(Path(candidate_path).read_bytes()),
                   allow_fixtures=allow_fixtures)
