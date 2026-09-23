# 15.3 — Inventario de formatos no ingeribles en el corpus de negocio

**Fecha de la corrida:** 2026-09-18 (UTC). **Comando:**
`python3 infra/inventario-formatos/inventariar_corpus.py --config infra/inventario-formatos/raices_autorizadas.json --out docs/eval/quality/15.3/inventario-agregado.json --markdown`

## Raíces autorizadas escaneadas

Las 4 raíces que `replicate-env/scripts/04-ingest-collections.sh` realmente ingesta
como colecciones de Qdrant (ver `infra/inventario-formatos/raices_autorizadas.json`
para la procedencia exacta):

| Raíz | Colección | Archivos únicos totales |
|---|---|---|
| `BusinessSuite.Xaf` | `bsuite-repo` | 3611 |
| `docs-bsute-innovapp-plan` | `innovapp-docs` | 15 |
| `Reyma.TI.Tickets.Microservice` | `micro-repo` | 295 |
| `Business-Suite` | `wiki-solis` | 58 |

Deliberadamente **excluidas** de este censo (no son "raíces autorizadas" en el
sentido de este ítem): `rag-engine` (el propio motor, no corpus de negocio) y
`Reyma.InnovApp` (existe en disco pero no es una colección ingestada — decisión
D-1 de `docs/analisis-futuro/plan-refinamiento-arquitectonico-produccion.md#7`;
su entrada es el ítem 15.6, no este).

Exclusiones de directorio y extensiones admitidas: parseadas en tiempo de
ejecución de `src/RagEngine.Core/Domain/ScanProfile.cs` (`DotNetEnterprise`),
no copiadas a mano — ver `_procedencia` en el JSON de salida para el snapshot
usado en esta corrida.

## Resultado agregado

- **Corpus útil (archivos únicos, sin `bin`/`obj`/`.git`/etc.):** 3979
- **No admitidos (archivos únicos):** 1474 (**37.04%** del corpus útil)
- **Admitido estructural** (chunker dedicado: `.cs`, `.md`): 2433
- **Admitido por fallback** (ventana deslizante: `.sql`, `.xaml`, `.js`, `.json`, `.xml`, `.csproj`, `.txt`): 72

Top formatos no admitidos por volumen (archivos únicos):

| Extensión | Familia | Archivos únicos | Bytes |
|---|---|---|---|
| `.png` | imagen | 1104 | 6.03 MB |
| `.resx` | lenguaje estructural (recursos .NET) | 119 | 2.28 MB |
| `.razor` | lenguaje estructural (Blazor) | 92 | 0.92 MB |
| `.css` | — | 61 | 1.88 MB |
| `.map` | — | 24 | 6.52 MB |
| `.xafml` | lenguaje estructural (XAF) | 9 | 1.77 MB |
| `.pdf` | pdf | 3 | 0.95 MB |
| `.jpg` | imagen | 11 | 16.32 MB |

Detalle completo (todas las extensiones, agregado y por raíz): ver
`inventario-agregado.json` en este mismo directorio.

## Veredicto contra el criterio de entrada de la ficha

> "Entrada propuesta para parseo: >=10 archivos útiles no ingeribles y >=10%
> del corpus útil, O >=5 preguntas de negocio prioritarias verificadas que
> solo esos formatos pueden responder."

**CUMPLE por volumen de archivos:** 1474 archivos únicos no admitidos
(≥10) y 37.04% del corpus útil (≥10%). No hizo falta evaluar la vía alternativa
de preguntas de negocio verificadas.

**Lectura honesta del resultado:** la mayor parte del volumen no admitido
(1104 `.png` + 92 `.razor` + 119 `.resx`, ≈89% del total no admitido) son
recursos de una app .NET/Blazor (iconos, vistas, recursos localizados) y
lenguajes estructurales sin chunker (`.razor`, `.xafml`), no documentos de
prosa de negocio (PDF/Office) en el sentido de LightRAG §5.3. El hueco de
**PDF/Office real es pequeño en volumen** (3 PDF, 0 Office detectados en estas
4 raíces) pero el criterio literal de la ficha —contar archivos, no
interpretarlos— se cumple igual porque `.razor`/`.resx` también son "lenguajes
estructurales ausentes de la ingesta", que el título del ítem incluye
explícitamente junto a los binarios/multimodales.

## Consecuencia para el ledger

Con este resultado, el bloqueo de entrada de **15.4** (parsing binario local)
y **15.5** (modalidades y procedencia) por volumen de PDF/Office puro no se
cumple con evidencia fuerte (solo 3 PDF, 0 Office en las 4 raíces). El hueco
medible más grande es `.razor`/`.resx`/`.xafml` (lenguajes estructurales de
Blazor/XAF), que no es lo que 15.4/15.5 proponen resolver (esas fichas hablan
de un sidecar de parseo PDF/Office, no de chunkers para lenguajes .NET UI).
Se registra la medición completa en el ledger; no se reclasifica 15.4/15.5
como desbloqueadas solo por este número agregado — su condición de entrada
específica (PDF/Office) sigue sin alcanzar el umbral en este corpus, y así se
deja documentado en `resultado`.

## Reproducibilidad

```bash
python3 infra/inventario-formatos/inventariar_corpus.py \
  --config infra/inventario-formatos/raices_autorizadas.json \
  --out docs/eval/quality/15.3/inventario-agregado.json \
  --markdown
```

Re-ejecutar sobre el mismo corpus (sin cambios) produce los mismos conteos
(cubierto por `test_reejecucion_es_deterministica` en
`infra/inventario-formatos/test_inventariar_corpus.py`). Tests:

```bash
python3 infra/inventario-formatos/test_inventariar_corpus.py -v
```

13/13 tests verdes contra fixtures sintéticos que distinguen
admitido-estructural / admitido-fallback / no-admitido, exclusión de
directorios (`bin/`) y duplicados exactos por hash de contenido.
