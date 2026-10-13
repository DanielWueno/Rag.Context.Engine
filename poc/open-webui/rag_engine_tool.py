"""
title: RAG Engine
description: Consulta el motor RAG (RagEngine.Api) y devuelve respuesta con fuentes citadas.
requirements:
version: 0.1.0
"""

import json
import urllib.error
import urllib.request

from pydantic import BaseModel, Field


class RagToolError(RuntimeError):
    pass


def _post(api_url: str, path: str, payload: dict, timeout: float):
    request = urllib.request.Request(
        api_url.rstrip("/") + path,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as ex:
        raise RagToolError(f"La API respondio HTTP {ex.code} en {path}") from ex
    except (urllib.error.URLError, TimeoutError, OSError) as ex:
        raise RagToolError(f"No se pudo consultar la API en {path}: {ex}") from ex


def _get(api_url: str, path: str, timeout: float):
    try:
        with urllib.request.urlopen(api_url.rstrip("/") + path, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as ex:
        raise RagToolError(f"La API respondio HTTP {ex.code} en {path}") from ex
    except (urllib.error.URLError, TimeoutError, OSError) as ex:
        raise RagToolError(f"No se pudo consultar la API en {path}: {ex}") from ex


def ensure_collection(api_url: str, collection: str, timeout: float = 30.0) -> None:
    """La API responde 200 (ask) o 500 (search) ante una coleccion inexistente; se valida antes."""
    body = _get(api_url, "/api/collections", timeout)
    available = body.get("collections", []) if isinstance(body, dict) else []
    if collection not in available:
        raise RagToolError(f"La coleccion '{collection}' no existe o no esta autorizada")


def rag_ask(api_url: str, collection: str, query: str, timeout: float = 300.0) -> dict:
    """Devuelve {'answer': str, 'sources': [...]}. Lanza RagToolError ante cualquier fallo.

    Pide responseMode='technical': en 'simple' (valor por defecto de la API) los
    fragmentos de codigo (no-prosa) llegan con File/Section/Lines redactados a
    proposito (ver RagEngine.Api/Program.cs BuildSourcesAsync), pensado para un
    lector no tecnico. Esta Tool muestra citas archivo:linea (format_sources), asi
    que necesita el detalle tecnico para poder citar codigo.
    """
    ensure_collection(api_url, collection)
    body = _post(
        api_url,
        "/api/ask",
        {"query": query, "collection": collection, "responseMode": "technical"},
        timeout,
    )
    if not isinstance(body, dict) or "answer" not in body or "sources" not in body:
        raise RagToolError("Respuesta de /api/ask con formato inesperado")
    return body


def rag_search(api_url: str, collection: str, query: str, timeout: float = 120.0) -> list:
    ensure_collection(api_url, collection)
    body = _post(api_url, "/api/search", {"query": query, "collection": collection}, timeout)
    if not isinstance(body, list):
        raise RagToolError("Respuesta de /api/search con formato inesperado")
    return body


def format_sources(sources: list) -> str:
    lines = []
    for number, source in enumerate(sources, start=1):
        location = source.get("file") or "(sin archivo)"
        if source.get("startLine") is not None:
            location += f":{source['startLine']}-{source.get('endLine')}"
        lines.append(f"[{number}] {location}")
    return "\n".join(lines)


class Tools:
    class Valves(BaseModel):
        api_url: str = Field(
            default="http://rag-api:5080",
            description="URL base de RagEngine.Api alcanzable desde el contenedor de Open WebUI (red infra_default).",
        )
        collection: str = Field(default="micro-repo", description="Coleccion a consultar.")
        timeout_seconds: float = Field(default=300.0, description="Tiempo maximo por consulta.")

    def __init__(self):
        self.valves = self.Valves()

    def preguntar_al_rag(self, pregunta: str) -> str:
        """
        Responde una pregunta con el motor RAG y cita las fuentes. Usar para preguntas sobre el corpus indexado.
        :param pregunta: pregunta en espanol o ingles.
        """
        try:
            result = rag_ask(
                self.valves.api_url, self.valves.collection, pregunta, self.valves.timeout_seconds
            )
        except RagToolError as ex:
            return f"ERROR: {ex}"
        if not result["sources"]:
            return "El motor RAG no encontro evidencia suficiente en la coleccion para responder."
        return f"{result['answer']}\n\nFuentes:\n{format_sources(result['sources'])}"

    def buscar_en_rag(self, consulta: str) -> str:
        """
        Busca fragmentos relevantes (archivo y lineas) sin generar respuesta.
        :param consulta: texto a buscar en espanol o ingles.
        """
        try:
            sources = rag_search(
                self.valves.api_url, self.valves.collection, consulta, self.valves.timeout_seconds
            )
        except RagToolError as ex:
            return f"ERROR: {ex}"
        return format_sources(sources) or "Sin resultados."
