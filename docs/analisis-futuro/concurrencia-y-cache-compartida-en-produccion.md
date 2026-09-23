# Concurrencia en producción: la caché de resúmenes como estado mutable compartido

> **Estado: analizado el 2026-08-22, nada implementado salvo el arreglo menor de `busy_timeout`
> (`c21cf1d`).** Surge de una corrupción real de la caché SQLite el 2026-08-20 y de la pregunta
> que la sigue: si esto se despliega, ¿es un problema? La respuesta corta es que el fallo concreto
> que sufrimos es de entorno de desarrollo y no se reproduce en un Linux normal, pero el diseño
> subyacente —un archivo mutable compartido entre procesos— sí pone un techo a la topología de
> despliegue. No implementar nada de aquí sin releer el estado.

## Qué pasó realmente, y qué NO fue

El 2026-08-20 ~18:08 la caché `~/Library/Application Support/rag-engine/summary-cache.sqlite3`
quedó con el btree corrupto y toda petición a `/api/search` reventaba con
`SQLite Error 11: 'database disk image is malformed'`. Recuperada con `.recover` (21.082 de
~21.205 filas, 99,4 %); el runbook está en [operaciones.md](../operaciones.md).

**No lo causó la concurrencia.** SQLite en WAL está diseñado para un escritor y N lectores
concurrentes. Lo que rompió fue cruzar una frontera de sistema de archivos: el índice WAL vive en
memoria compartida (`*-shm`) y exige coherencia de `mmap` entre todos los procesos que abren la
base. Entre el host macOS y la VM Linux de Docker Desktop, VirtioFS no la garantiza — cada lado
veía un `-shm` distinto y escribía páginas encima del otro.

Evidencia de la ventana: `logs/rag-api-20260821.json` muestra al contenedor sirviendo 66 eventos
entre 00:01:29Z y 00:04:31Z mientras `logs/rag-engine-20260820.json` registra la Fase 2 del
`rag ingest` nativo; el primer `malformed` aparece a las 00:08:10Z.

## Qué esperar en producción

| Escenario | Riesgo |
|---|---|
| API e ingesta en el mismo host Linux (procesos o contenedores, mismo filesystem) | **Ninguno específico.** Es el caso para el que WAL está diseñado |
| Bind mount desde host macOS con Docker Desktop | **Corrupción.** Es lo que sufrimos |
| Archivo en filesystem de red (NFS, EFS, SMB) | **Corrupción.** SQLite documenta explícitamente que WAL no está soportado ahí |
| Varias réplicas de la API en hosts distintos con volumen compartido | **Corrupción.** Misma raíz que el anterior |

Por componente, con consultas y una ingesta a la vez:

- **Qdrant** — hecho para ello. Dos matices operativos: con `--force` la colección queda vacía
  mientras se reconstruye (las consultas contra ella no devuelven nada durante ese hueco), y la
  limpieza incremental de obsoletos abre una ventana corta de consistencia eventual.
- **SQLite** — contención de escritura, no corrupción. Ver la nota sobre `busy_timeout` abajo.
- **Ollama** — un solo nodo: la Fase 2 y `rag ask` compiten por él. Latencia, no corrección.

## Varias ingestas a la vez

`DeleteSupersededPointsAsync` acota el borrado a los archivos que **esa** corrida procesó
(`processedFilePaths`). Consecuencias:

- **Seguro:** dos ingestas de proyectos distintos contra la misma colección. Sus conjuntos de
  archivos son disjuntos, así que ninguna borra puntos de la otra. Esto es también lo que hace
  seguro el patrón de tres subcarpetas de `bsuite-auditorias-test` en
  [reingesta-manual.md](../reingesta-manual.md).
- **Peligroso:** dos ingestas **del mismo** proyecto contra la misma colección. La corrida A no
  conoce los chunks que B acaba de escribir para los mismos archivos y los borraría por obsoletos.
  Mitigación: serializar por colección — un lock por nombre de colección, no global.

## El arreglo de `busy_timeout`, y por qué es menos de lo que parecía

`PRAGMA busy_timeout` es por conexión y no se hereda del pool; `SummaryCache.Open` lo fijaba sólo
en su conexión de setup. Parecía un fallo de producción esperando a ocurrir.

**Medido de forma aislada, no lo era.** Con un escritor rival reteniendo el lock 400 ms:

```
sin pragma, CommandTimeout=30 (default) : OK en 457 ms
con pragma, CommandTimeout=30 (default) : OK en 464 ms
sin pragma, CommandTimeout=0            : OK en 460 ms
con pragma, CommandTimeout=0            : OK en 466 ms
```

Microsoft.Data.Sqlite reintenta ante `SQLITE_BUSY` en la capa de comando, así que la espera está
garantizada con o sin el pragma (y `CommandTimeout=0` en este proveedor significa "sin límite", no
"sin espera"). Se aplicó igualmente en `c21cf1d` como defensa en profundidad, no como corrección
de un fallo observado. Queda anotado aquí para que nadie lo cite como precedente de un problema
que no se demostró.

## Opciones de arquitectura

El punto débil no es la concurrencia: es que la caché es **estado mutable compartido en un
archivo**. Funciona mientras escritor y lectores vivan en la misma máquina; deja de funcionar en
cuanto haya más de una réplica o el volumen sea de red, y el síntoma es corrupción, no un error
limpio. De menor a mayor esfuerzo:

1. **Single-writer co-locado.** La ingesta es la única que escribe; la API lee una copia
   (snapshot al arrancar, o `Mode=ReadOnly` sobre el mismo filesystem local). Barato, y suficiente
   mientras haya una sola máquina. No resuelve múltiples réplicas.
2. **Mover la caché a un servidor.** Postgres o Redis. Elimina la clase entera de problema y
   habilita N réplicas. Es el paso obligado si la API se escala horizontalmente.
3. **Guardar el resumen en el payload de Qdrant.** El comentario de `SummaryCache` explica por qué
   no se hizo: un `--force` recrea la colección y lo perdería. Sigue siendo válido como caché
   *secundaria* si existe un respaldo duradero aparte.

## Qué decidir antes de desplegar

- ¿Habrá más de una réplica de la API? Si sí, la opción 1 no basta.
- ¿El volumen de la caché será local o de red? Si es de red, SQLite queda descartado sin más.
- ¿Se permitirán ingestas concurrentes sobre la misma colección? Si sí, hace falta el lock por
  colección antes de que la limpieza de obsoletos pueda morder.

Mientras tanto, la regla operativa vigente —bajar `rag-api` antes de una ingesta con
`--con-resumen`— es un parche de entorno de desarrollo, no una arquitectura.
