# ApplicationCategorizer — Name Normalization & Inference Design

**Date:** 2026-06-10  
**File:** `src/Shared/Utilities/ApplicationCategorizer.cs`  
**Status:** Approved

---

## Problem

Two concrete issues in the current design:

1. **Arch suffix bleed (B):** `NormalizeProcessName` retains trailing architecture tokens (`64`, `32`, `x86`, `x64`). Unknown apps following the `<name><arch>` convention (e.g., `myeditor64`) arrive at `InferFromName` with the suffix intact, causing suffix patterns like `EndsWith("studio")` to miss.

2. **Mutable static map (C):** The single `Dictionary<string, string> s_appCategoryMap` serves as both the authoritative known-app lookup and the runtime inference cache. This prevents immutability, pollutes `GetAllCategories`/`GetApplicationsByCategory` with inferred entries, and is not thread-safe under concurrent writes.

---

## Goals

- Strip arch suffixes before inference without touching `NormalizeProcessName`
- Make the known map immutable (`FrozenDictionary`)
- Isolate runtime inferred results in a separate `ConcurrentDictionary`
- Expand `InferFromName` to cover currently unrepresented categories (Gaming, FileManager, Design, Office, System) at zero additional runtime cost
- No new allocations on the hot path

---

## Data Layer

### Known map
```
static readonly FrozenDictionary<string, string> s_knownApps
```
Built once at static init via `.ToFrozenDictionary(StringComparer.Ordinal)`. No runtime writes possible — enforced by type. Faster lookup than `Dictionary` for read-heavy static data.

### Inferred cache
```
static readonly ConcurrentDictionary<string, string> s_inferredCache
```
Empty at startup. Holds all runtime-derived results. `TryAdd` used so first writer wins with no lock contention.

### Lookup order in `Categorize`
1. `s_knownApps` — O(1), immutable
2. `s_inferredCache` — O(1), concurrent-safe read
3. `InferFromName` → on hit, `s_inferredCache.TryAdd`
4. `CategorizeFromFileInfo` → `s_inferredCache.TryAdd`

### Query methods
`GetAllCategories()` and `GetApplicationsByCategory()` read `s_knownApps` only. Inferred entries are runtime noise and excluded from authoritative queries.

---

## `StripArchSuffix`

```
private static ReadOnlySpan<char> StripArchSuffix(ReadOnlySpan<char> name)
```

Strips trailing architecture tokens in longest-first order to avoid partial matches:

| Token stripped |
|---|
| `x86_64` |
| `x64` |
| `x86` |
| `x32` |
| `64` |
| `32` |

Span-based slice — zero allocation. Returns the stripped span for immediate consumption inside `InferFromName`.

---

## `InferFromName`

Calls `StripArchSuffix` as its first step, then runs the pattern switch on the stripped span. All comparisons operate on the already-lowercased normalized name.

| Pattern | Category |
|---|---|
| ends with `browser` | Browser |
| ends with `studio`, `ide`, `code`, `edit` | IDE |
| ends with `term`, `console`, `shell` | Terminal |
| ends with `chat`, `meet`, `call` | Comms |
| ends with `mail` | Email |
| ends with `db`, `sql`, `base` | Database |
| ends with `player`, `media`, `cast` | Media |
| ends with `design`, `paint`, `draw` | Design |
| ends with `game`, `games`, `launcher` | Gaming |
| ends with `fm`, `files`, `manager` | FileManager |
| ends with `office`, `doc`, `docs`, `note` | Office |
| contains `remote`, `rdp`, `vnc` | RemoteAccess |
| contains `svc`, `host`, `broker` | System |
| fallthrough | Other |

---

## `NormalizeProcessName`

No changes. Remains a pure transformation: strip path → strip extension → remove non-alphanumeric → lowercase.

---

## `CategorizeFromFileInfo`

Expand the switch to cover the same category set as `InferFromName` (keyword-contains on lowercased `FileDescription`/`ProductName`). Ensures the file-info fallback path produces consistent results with the inference path.

---

## What Does NOT Change

- Public API surface (`Categorize`, `GetAllCategories`, `GetApplicationsByCategory`, `NormalizeProcessName` signature)
- The known-app entries in the static map (existing `rider64`, `obs64` etc. entries are kept as-is)
- `CategorizeFromFileInfo` signature

---

## Testing Notes

- `StripArchSuffix` should be tested in isolation: `idea64` → `idea`, `myapp32` → `myapp`, `x86app` → `app`, plain names → unchanged
- `InferFromName` tested against stripped inputs: `myeditor64` → strips to `myeditor` → `IDE`
- `Categorize` concurrency: multiple threads hitting unknown names simultaneously should not corrupt state
- `GetAllCategories` must not include inferred entries
