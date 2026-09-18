# Inventariador de formatos no ingeribles (ítem 15.3)

Herramienta de solo lectura para censar, por extensión/MIME, qué fracción del
corpus de negocio autorizado el scanner de ingesta (`FileSystemIngestionScanner`
+ `ScanProfile.DotNetEnterprise`) puede o no puede indexar.

## Uso

```bash
python3 inventariar_corpus.py --config raices_autorizadas.json --markdown
python3 inventariar_corpus.py --config raices_autorizadas.json --out informe.json
```

## Tests

```bash
python3 test_inventariar_corpus.py -v
```

Corre contra `fixtures/raiz-a/`, un mini-corpus sintético que distingue
admitido-estructural, admitido-fallback, no-admitido, exclusión de directorios
(`bin/`) y duplicados exactos por hash de contenido.

## Diseño

- Las extensiones admitidas y los patrones de exclusión de directorio se leen
  en tiempo de ejecución de `src/RagEngine.Core/Domain/ScanProfile.cs`
  (`DotNetEnterprise`) — no se copian a mano, para que este inventario no
  pueda desincronizarse en silencio del scanner real.
- El conjunto de lenguajes con `IChunkingStrategy` dedicado (`CSharp`,
  `TypeScript`, `Markdown`) sí está fijo en `inventariar_corpus.py`, porque
  `ChunkingStrategyRouter` los arma por reflexión de .NET en tiempo de
  ejecución, no por texto estático parseable desde Python.
- Deduplicación por SHA-256 del contenido (con muestreo de tamaño+cabecera
  para archivos >8MB, pensado para binarios grandes tipo video).
- No escribe ni borra nada bajo las raíces escaneadas.

Ver `docs/eval/quality/15.3/inventario-corpus-negocio.md` para el resultado
de la corrida sobre el corpus de negocio real y el veredicto contra el
criterio de entrada de la ficha del ledger.
