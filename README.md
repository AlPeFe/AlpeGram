<p align="center">
  <img src="assets/hannigram-mascot.png" alt="HanniGram mascot: a claymotion-style monitor character wearing headphones, heart-shaped blue eyes, and a blue bunny clip acting as the circular logo" width="220" />
  <br />
  <em>HanniGram the monitor — always listening, always remembering.</em>
</p>

# HanniGram

Memoria persistente por proyecto para agentes de IA de código. Un motor **.NET 10** (SQLite + FTS5, daemon HTTP, CLI) que vive como **módulo independiente** y se integra **nativamente** con [HanniPI](https://github.com/AlPeFe/HanniPI) — sin MCP.

Inspirado en [engram](https://github.com/Gentleman-Programming/engram), construido para que sea tuyo: adáptalo libremente, ejecútalo standalone, o conéctalo a tu harness basado en Pi.

```
Harness HanniPI
    ↓ extensión nativa (pi-extension/, registra tools mem_*)
HanniGram daemon (.NET 10, HTTP en 127.0.0.1:8765)
    ↓
SQLite + FTS5 (~/.hannigram/hannigram.db)
```

## Qué hace

- **Memoria por proyecto** — resuelve el proyecto desde el directorio de trabajo (git remote cuando está disponible), así cada proyecto tiene su propio espacio de memoria.
- **Dos ámbitos: `project` y `global`** — `project` es el espacio del repositorio; `global` es una memoria compartida por **todos** los proyectos, para lo que no es un hecho del repo sino una heurística que se repite: cómo montas una consola, tus convenciones de commit, en qué capas separas el código. Se elige al escribir (`scope=project|global`) y `hannigram promote <id>` sube al ámbito global un hallazgo que resultó genérico; si el `topic_key` ya existe allí, el nuevo contenido gana **sobre la fila global, en el sitio** (sin duplicados). Una fila `archived` se **rechaza** por defecto (HTTP 409, `outcome=refused`, `reason=source_archived`) para no pisar la verdad global viva con contenido retirado; con `allowArchived=true` se promueve igual, pero la fila resultante conserva el estado `archived`. Los dos ámbitos comparten un único store, un único índice FTS y el mismo upsert por `topic_key`.
- **Observaciones estructuradas** — What / Why / Where / Learned, con título y tipo buscables.
- **Búsqueda de texto completo** — SQLite FTS5 sobre las observaciones, con **tokenizer trigram**: tolera typos (buscar `rozado` encuentra `rosado`), substrings y CJK. Si el match estricto no da resultados, reintenta con los trigramas de la query (fuzzy) rankeado por bm25.
- **Búsqueda entre ámbitos** — `hannigram search` y `/api/search` aceptan `scope=project|global|all` (por defecto `all`): devuelven los aciertos del proyecto **y** los globales en un solo resultado, cada uno etiquetado con su origen, para que el modelo distinga un hecho del repo de una heurística universal.
- **ADR / decisiones** — las observaciones con `type=adr` y `topic_key=adr/<slug>` son decisiones de arquitectura/convención (p.ej. `adr/ui/palette`). Son la base de "respetar estilos": HanniPI las consulta antes de implementar en zonas cubiertas, sin redefinirlas en cada prompt.
- **Memoria local exportada** — `hannigram init` crea el proyecto en la BD **y** escribe `.hannigram/memory.md` en el proyecto: una vista markdown de las decisiones, para que cualquier harness (o persona) que solo trabaje con ficheros lea la memoria sin depender del daemon. La BD sigue siendo la fuente de verdad. El fichero **no** embebe rutas absolutas de la máquina (solo nombra la ruta por defecto de la BD y su variable de entorno), así que se puede commitear sin filtrar el disco de nadie.
- **Topic keys** — claves estables (`architecture/auth-model`) para mantener el conocimiento evolutivo en un solo lugar. Guardar con un `topic_key` ya existente **actualiza** la observación (upsert) en vez de crear una versión duplicada.
- **Corregir sin duplicar** — `hannigram update <id>` (`mem_update` / `PUT /api/observations/{id}`) edita una observación existente: solo cambia los campos que le pasas, conserva `created_at` y mueve `updated_at`, y las filas siguen escopadas a su ámbito.
- **Sesiones + handoff** — `session_summary` guarda objetivo, descubrimientos y próximos pasos para que la siguiente sesión recupere el contexto.
- **Ciclo de vida y revisión** — cada observación tiene un estado (`active` / `needs_review` / `archived`) y un flag `pinned`. Las lecturas devuelven solo `active` por defecto; `archived` es el olvido seguro (la fila se conserva) y queda excluida salvo que se pida con `state=`. Guardar devuelve un aviso `review` compacto cuando sobrescribe un `topic_key` o detecta un casi-duplicado; `GET /api/review` los lista acotados con previews truncados. La inyección automática (`GET /api/context`) se limita a un número fijo de heurísticas globales fijadas (`pinned`) más unas pocas filas recientes del proyecto.

## Arquitectura

| Proyecto | Rol |
|---|---|
| `src/HanniGram.Core` | Dominio + store SQLite/FTS5 (`HanniGramStore`) + resolver de proyecto |
| `src/HanniGram.Server` | Daemon HTTP (ASP.NET Core minimal API) |
| `src/HanniGram.Cli` | CLI standalone (no necesita daemon) |
| `src/HanniGram.Mcp` | Servidor MCP por stdio (opcional, reutiliza `HanniGram.Core`; sin daemon) |
| `pi-extension/` | Extensión nativa de Pi que registra las tools `mem_*` |

## Ejecutar el daemon

```bash
cd src/HanniGram.Server
dotnet run -c Release
# escucha en http://127.0.0.1:8765 (override con HANNIGRAM_URLS)
# BD en ~/.hannigram/hannigram.db (override con HANNIGRAM_DB)
```

## HTTP API

| Método | Ruta | Propósito |
|---|---|---|
| GET | `/health` | Health + ruta de la BD |
| GET | `/api/projects` | Listar proyectos |
| GET | `/api/projects/current?cwd=` | Resolver proyecto para un cwd |
| POST | `/api/observations?cwd=&scope=` | Guardar una observación (upsert por `topic_key`); devuelve un `review` compacto si hubo colisión |
| GET | `/api/observations/{id}?cwd=` | Obtener una observación (escopada al proyecto) |
| GET | `/api/observations?cwd=&limit=&scope=&state=` | Observaciones recientes (solo `active` por defecto) |
| GET | `/api/observations/topic/{key}?cwd=` | Observaciones por topic key (historial completo) |
| PUT | `/api/observations/{id}?cwd=&scope=` | Actualizar una observación por id (mem_update); los campos nulos no cambian |
| DELETE | `/api/observations/{id}?cwd=` | Borrar una observación (escopada al proyecto) |
| POST | `/api/observations/{id}/state?cwd=&scope=` | Cambiar el estado (`active`/`needs_review`/`archived`) |
| POST | `/api/observations/{id}/pin?cwd=&scope=` | Fijar o quitar el pin de una observación |
| POST | `/api/observations/{id}/promote?cwd=&allowArchived=` | Promover una observación al ámbito global; una fuente `archived` se rechaza con 409 salvo `allowArchived=true` |
| GET | `/api/search?cwd=&q=&limit=&type=&topicKey=&scope=&state=` | Búsqueda FTS5 (filtro opcional por `type`, `topicKey`, `scope` y `state`) |
| GET | `/api/review?cwd=&scope=&limit=` | Revisión acotada: filas `needs_review` + candidatos duplicados, con previews truncados |
| GET | `/api/context?cwd=` | Contexto de inyección acotado: heurísticas globales fijadas + filas recientes |
| GET | `/api/topics?cwd=` | Topic keys existentes |
| POST | `/api/sessions/start?cwd=` | Iniciar una sesión |
| POST | `/api/sessions/end?cwd=` | Terminar una sesión (summary) |
| GET | `/api/sessions?cwd=` | Listar sesiones |

Ámbito de proyecto: pasa `cwd` (o `project`) como query param; el daemon resuelve el proyecto desde ahí. `scope=global` escribe/lee la memoria compartida entre proyectos; `state=` selecciona el ciclo de vida (`active` por defecto, o `needs_review`/`archived`/`all`).

## CLI (standalone)

```bash
hannigram project [cwd]
hannigram init [--cwd]   # crea el proyecto en la BD + escribe .hannigram/memory.md (memoria local exportada)
hannigram save <title> [--content] [--what] [--why] [--where] [--learned] [--topic] [--type] [--scope project|global] [--project] [--cwd]
hannigram update <id> [--title] [--content] [--what] [--why] [--where] [--learned] [--topic] [--type] [--scope project|global] [--project] [--cwd]
hannigram state <id> --set active|needs_review|archived [--scope project|global] [--project] [--cwd]
hannigram pin <id> [--off] [--scope project|global] [--project] [--cwd]
hannigram search <query> [--limit] [--state active|needs_review|archived|all] [--scope project|global|all] [--project] [--cwd]
hannigram list [--limit] [--state active|needs_review|archived|all] [--project] [--cwd]
hannigram review [--limit] [--scope project|global|all] [--project] [--cwd]
hannigram topics [--project] [--cwd]
hannigram session-start [--id] [--project] [--cwd]
hannigram session-end <id> [--summary] [--goal] [--next] [--project] [--cwd]
hannigram projects
```

### `hannigram init` — instancia la memoria del proyecto

Crea el proyecto en la BD global **y** escribe `.hannigram/memory.md` en el directorio de trabajo: una vista markdown de las observaciones/ADR del proyecto, para que cualquier harness (o persona) que solo trabaje con ficheros lea las decisiones sin depender del daemon. La BD sigue siendo la fuente de verdad; el fichero es una exportación.

```bash
cd /ruta/al/proyecto
hannigram init --cwd .
# → init: proyecto 'org/repo' en BD + memoria local ./.hannigram/memory.md (N observaciones)
```

## Filosofía sODD + memoria

HanniGram no se instancia siempre. Siguiendo la filosofía **sODD** (orgánico, no burocrático), la memoria de un proyecto se crea **cuando hace falta**, no por defecto:

- **Manual**: `hannigram init` (o `/hannigram init` en HanniPI) cuando tú decides que el proyecto merece memoria.
- **Automático (sODD)**: cuando el proyecto es lo bastante grande y se toman decisiones importantes que conviene recordar, el orquestador lo sugiere/activa.

La memoria guarda **contexto combinado y relacionado** — no solo observaciones sueltas: decisiones (ADR), sesiones, y las relaciones entre ellas. En **cada** turno (el hook de contexto de HanniPI dispara antes de cada llamada al modelo), HanniPI inyecta un **índice ligero** de la memoria del proyecto — las ADR y las observaciones recientes, con título, `topic_key` y `learned`, nunca el contenido completo — y recupera la observación completa **bajo demanda** cuando la tarea la necesita, sin releer todos los ficheros ni gastar tokens de más. La inyección **no** está condicionada a que haya una tarea o ficha activa: ocurre siempre que el proyecto tenga observaciones que encajen.

**Respetar estilos/convenciones**: una ADR marcada como convención (p.ej. `adr/ui/palette`) se aplica siempre en las zonas que cubre; si hay duda, el agente pregunta. Así decides el estilo una vez y todas las llamadas lo respetan, sin redefinirlo en cada prompt.

## Integración con HanniPI (nativa, sin MCP)

El directorio `pi-extension/` es una extensión de Pi que registra las tools `mem_*`. Pi la descubre vía el campo `pi.extensions` de su `package.json` (o añadiendo la ruta al array `extensions` de `~/.pi/agent/settings.json`).

Tools: `mem_current_project`, `mem_save`, `mem_search`, `mem_context`, `mem_timeline`, `mem_get_observation`, `mem_suggest_topic_key`, `mem_session_summary`, `mem_update`, `mem_review`, `mem_promote`, `mem_state`, `mem_pin`.

`mem_save` acepta `scope=project|global` y `mem_search` acepta `scope=project|global|all` (cada acierto llega etiquetado con su origen); `mem_update`, `mem_state`, `mem_pin` y `mem_delete` aceptan `scope` para elegir a qué ámbito pertenece la fila (así `mem_delete` alcanza la memoria global). `mem_promote` acepta `allowArchived`: una fuente `archived` se rechaza por defecto y el rechazo llega como resultado (`outcome=refused`, `reason=source_archived`), no como error.

Todas las tools son **bajo demanda**: la extensión no inyecta memoria en el prompt por sí sola ni inyecta texto completo de una observación.

```bash
cd pi-extension && npm install   # typebox + jiti (para el script de verificación)
```

Verifica la extensión contra un daemon en ejecución:

```bash
node --experimental-strip-types --no-warnings verify-extension.mjs
```

## Servidor MCP por stdio (opt-in, desactivado por defecto)

Además de la integración nativa con HanniPI, HanniGram trae un **servidor MCP opcional** (`src/HanniGram.Mcp`, `hannigram-mcp.exe`). Habla **JSON-RPC 2.0 por stdio** (mensajes delimitados por saltos de línea), reutiliza `HanniGram.Core` y **abre la SQLite directamente — no necesita el daemon**. Coexiste con el daemon en marcha sobre la misma BD (WAL + `busy_timeout`).

**Está desconectado por defecto.** HanniGram no escribe ni modifica ningún fichero de configuración de harness (`~/.pi/agent/mcp.json`, `.pi/mcp.json`, etc.): nada se registra solo. Si quieres usarlo, **regístralo tú** en tu cliente MCP:

```json
{
  "mcpServers": {
    "hannigram": {
      "command": "C:\\ruta\\a\\hannigram-mcp.exe",
      "args": [],
      "env": { "HANNIGRAM_DB": "C:\\Users\\<usuario>\\.hannigram\\hannigram.db" }
    }
  }
}
```

- La BD sale de `HANNIGRAM_DB` (mismo valor por defecto que el daemon/CLI: `~/.hannigram/hannigram.db`).
- El proyecto se resuelve desde el argumento `project` de cada tool, luego `HANNIGRAM_CWD`, luego el directorio de trabajo del proceso.
- Todo **dato diagnóstico va a stderr**; stdout es solo el canal de protocolo.

Tools expuestas (mismo motor que la API de memoria): `mem_current_project`, `mem_save`, `mem_search`, `mem_context`, `mem_get_observation`, `mem_timeline`, `mem_suggest_topic_key`, `mem_delete`, `mem_session_summary`, `mem_update`, `mem_review`, `mem_promote`, `mem_state`, `mem_pin`.

## Releases

El binario Windows x64 (daemon + CLI + servidor MCP self-contained, sin .NET en la máquina destino) se publica aquí como
`hannigram-windows-x64.zip` y es la **fuente de verdad** del motor:

```bash
git tag v0.2.0 && git push origin v0.2.0     # dispara .github/workflows/release.yml
```

El workflow (Windows) publica el exe, hace smoke test de `/health`, empaqueta con `scripts/pack-asset.ps1`
y crea la release con `hannigram-windows-x64.zip`, `SHA256SUMS` y `VERSION.txt` (tag, commit, fecha).
El detalle de cada release está en [`CHANGELOG.md`](CHANGELOG.md).

El asset incluye `pi-extension/` (y el `hannigram-mcp.exe` opt-in), así que quien lo instala obtiene daemon + CLI + server MCP + tools `mem_*` de una vez.
HanniPI copia este mismo asset **pinneado** en su release (ver `HanniPI/.pi/skills/release.md`) para que
instalar HanniPI+memoria sea una sola descarga; `hanni-setup.ps1 -Hannigram latest` trae la última de aquí.

## Licencia

MIT
