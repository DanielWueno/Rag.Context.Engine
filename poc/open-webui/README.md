# PoC Open WebUI + RAG (ficha 24.1)

Open WebUI corre en la Mac (alias SSH `mac-devstudio`) como contenedor `open-webui`, en la red `infra_default` junto a `rag-api`, con volumen propio `openwebui_poc` y puerto `127.0.0.1:3000`. Su RAG propio y los backends Ollama/OpenAI estan desactivados por entorno; el conocimiento lo aporta la Tool.

## Arranque (en la Mac)

```bash
export PATH=/Applications/Docker.app/Contents/Resources/bin:/usr/local/bin:$PATH
docker run -d --name open-webui --restart unless-stopped --network infra_default \
  -p 3000:8080 -v openwebui_poc:/app/backend/data \
  -e ENABLE_OLLAMA_API=true -e OLLAMA_BASE_URL=http://host.docker.internal:11434 \
  -e ENABLE_OPENAI_API=false -e WEBUI_AUTH=true -e DEFAULT_USER_ROLE=pending \
  ghcr.io/open-webui/open-webui:main
```

Acceso directo por red: `http://10.97.0.19:3000` (puerto publicado en todas las interfaces de la Mac). Sin identidad corporativa todavia (24.3): el primer usuario registrado es administrador, asi que registrarlo apenas se despliegue; las cuentas posteriores quedan `pending` hasta que el administrador las apruebe. El tunel SSH (`ssh -N -L 3000:127.0.0.1:3000 mac-devstudio`) sigue valiendo.

Para que el chat tenga un LLM, activar el backend Ollama (`ENABLE_OLLAMA_API=true`, `OLLAMA_BASE_URL=http://host.docker.internal:11434`) y elegir un modelo de hasta 14B (techo acordado). Los modelos de 30B quedan fuera del PoC: la Mac (24 GB) no los soporta junto con Docker, y el Ollama de la Mac es compartido.

## Tool

Importar `rag_engine_tool.py` en Workspace > Tools. Valves por defecto: `api_url=http://rag-api:5080`, `collection=micro-repo`.

## Verificacion

El verificador carga la misma Tool y exige `--api-url` explicito. Contra la Mac, por tunel:

```bash
ssh -N -L 15080:127.0.0.1:5080 mac-devstudio
python poc/open-webui/verify_tool.py --api-url http://127.0.0.1:15080 --collection micro-repo --fixture poc/open-webui/cases.json
```

Resultado 2026-10-13 (dos corridas consecutivas, misma ventana sin el 30B compartido cargado): **8/8**.

Dos hallazgos reales corregidos (no umbrales/corpus, ver ledger 24.1):

1. **`responseMode=technical`**: `/api/ask` redacta a proposito `File/Section/Lines` de
   fragmentos no-prosa en modo `simple` (lectura no tecnica, ver
   `RagEngine.Api/Program.cs BuildSourcesAsync`). La Tool pide citas archivo:linea, asi
   que `rag_ask` ahora envia `responseMode: "technical"` en el body.
2. **Declinar out-of-corpus no es "cero fuentes"**: la API siempre devuelve el top-K
   recuperado como contexto, incluso cuando el LLM declina honestamente en el texto de
   la respuesta (visto en ES: 10 fuentes de score bajo, respuesta declina sin inventar
   datos). El verificador ahora exige "cero fuentes" O un texto de respuesta con marcador
   de declinacion (ES/EN), coincidiendo con el criterio literal ("no produce respuesta
   afirmativa inventada"), no con un proxy mas estricto que el texto escrito.

La propagacion de 403 por coleccion no autorizada (mencionada en `verificacion`) no se
reverifica aqui: ya esta cubierta por la suite xUnit existente
(`CollectionAuthorizationVisibilityHttpHarnessTests`, `CollectionManifestHttpHarnessTests`);
provisionar una coleccion aislada con ACL restringida solo para este PoC quedo fuera de
presupuesto de esta ficha.

## Rollback

```bash
docker rm -f open-webui && docker volume rm openwebui_poc
```

No toca `rag-api`, `qdrant-local`, colecciones ni la cache SQLite.
