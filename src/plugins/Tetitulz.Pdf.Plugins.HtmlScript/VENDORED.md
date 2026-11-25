# Vendored: AngleSharp.Js 1.1.0

`Vendor/AngleSharp.Js/` is the source of **AngleSharp.Js 1.1.0** (MIT, © AngleSharp — see
`Vendor/AngleSharp.Js/LICENSE`), copied from the `v1.1.0` tag of
<https://github.com/AngleSharp/AngleSharp.Js> (commit `16b974d121`, `src/AngleSharp.Js/*.cs`).
It binds AngleSharp's DOM to the Jint interpreter, which is what lets a page's scripts run for the
managed HTML engine.

## Why vendored rather than referenced

A page's scripts are untrusted code, and Jint can bound them — a statement count, a memory limit, a
deadline — **only while an engine is being built** (`Engine.Options` is internal). AngleSharp.Js
builds its engines itself and exposes nothing but the call-stack depth, so through the package an
infinite loop in a page script could not be stopped in-process at all: .NET cannot abort a thread.
The owner chose this route on 2026-09-29 over attaching constraints by reflection into Jint's
private fields (fragile) or waiting on an upstream change.

## What was changed — eleven things

Everything is byte-for-byte the tag's, visibility included, except these, each marked
`TETITULZ MODIFICATION`:

1. **The limits hook.** `JsScriptingOptions.ConfigureEngine` — an `Action<Jint.Options>` (copied by
   `Clone()`); `EngineInstance`'s constructor invokes it inside `new Engine(...)`, after its own
   configuration.
2. **The assembly filter.** `JsScriptingService.GetAssemblies` keeps the assemblies whose name starts
   with "AngleSharp" — the tag's own assembly among them. Vendored here, that name is
   `Tetitulz.Pdf.Plugins.HtmlScript`, and the filter dropped every DOM member this code adds:
   `console`, `atob`/`btoa`, `DOMParser`, `screen`, the layout properties. A page's `console.log`
   threw. The filter now keeps this assembly too.
3. **The layout getters report.** `ElementPseudoExtensions`' twelve getters (`offsetWidth`,
   `clientHeight`, `scrollTop`, …) still answer 0, through one helper that also tells the page's
   `LayoutQueryLog` (the plugin's, found as a browsing-context service) which one was read.
4. **The namespaces** (2026-10-03). Every file's namespace moved into
   `Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs`: `AngleSharp`, `AngleSharp.Dom`,
   `AngleSharp.Scripting` and `AngleSharp.Js` to the root, `AngleSharp.Js.{Attributes,Cache,Dom,Proxies}`
   to its sub-namespaces. Each file says what it was on its marker line, and gained the `using`s for
   the core AngleSharp namespaces it used to sit inside. Kept, the tag's names collided with the
   official package's: a host referencing both got CS0121 on `Configuration.Default.WithJs()` under
   `using AngleSharp;`, and CS0433 naming `JsScriptingService`. The types stay **public** — AngleSharp.Js
   finds its DOM globals among its own assembly's exported types — so the namespace is what keeps
   them out of a host's way.
5. **The timers report** (2026-10-21). `EngineExtensions.Call` hands `IWindowTimers.SetTimeout` and
   `ClearTimeout` to the plugin's `TimerSettleLog.Intercept` before invoking them. It records the
   timer and when it is due, and wraps the callback so the record goes when it runs. The settle
   window then waits for every timer due inside it, rather than sleeping its length and racing the
   thread pool that queues AngleSharp's timer continuations. Nothing a page sees changes.
6. **The engine hook** (2026-10-22). `JsScriptingOptions.PrepareEngine` — an `Action<Jint.Engine>`
   (copied by `Clone()`); `EngineInstance`'s constructor invokes it last, once the window's globals
   are in place and before any script runs. The plugin runs `Core.Scripting.ScriptBuiltinBounds`'
   prelude there: Jint asks its memory limit when a statement starts, so one call of `repeat`,
   `join`, `JSON.stringify` and their kin built what it was asked to before the limit saw it.
7. **The regex timeout of a prepared script** (2026-10-22). `JsScriptingOptions.RegexTimeout`;
   `EngineInstance.RunScript` hands it to `ScriptCache.GetOrCreate`, which prepares under it and
   keys the cache by source and timeout. A prepared script's regular-expression literals keep the
   timeout they were prepared with — Jint's ten seconds — whatever the evaluating engine's own
   option says, so a backtracking pattern in a page script ran twice its script timeout.
8. **No module off the disk** (2026-10-22). `JsModuleLoader.LoadModuleContents` fetches an http(s)
   module through the document's resource loader and refuses every other specifier with a script
   error. It handed them to Jint's `DefaultModuleLoader`, which reads a `file:` URI's file: a page's
   own classic script could `import()` a JSON or script file of the converting host and write its
   text into the PDF, and a missing file failed differently from a present one.
9. **A string indexer's absent name** (2026-10-23). `DomIndexers.TryGetFromNamedIndex` answers no own
   property where the getter reads null, setter or not, so `'src' in el.dataset` is false with no
   `data-src` (it was true for every name, and a lazy-load script wrote `src="undefined"`); a write
   still reaches the setter. `TrySetToIndex` leaves a symbol key and an accessor descriptor to the
   object's own property bag, where the reads look: a symbol-keyed write went to the indexer as
   `"Symbol(k)"` and never read back.
10. **What the script cache keeps** (2026-11-23). `ScriptCache` keeps a source only when it is at most
   `MaxCachedSourceLength` (512 K) characters long and prepared for the second time (the sources seen once
   are remembered by hash, at most 4 096, forgotten together when the set fills); with every slot taken,
   the entry served least gives way. It kept the first 32 sources the process ever prepared, of any size,
   for good: the first page a server converted could fill every slot with its own scripts, pinned, and
   every library after it paid a full preparation on every run.
11. **A source's hash is keyed** (2026-11-25). `ScriptCache` hashes a source through
   `Core.Util.KeyedHash`, for the cache and for the sources seen once: on .NET Framework a string's own
   hash is one fixed function, so a page could choose a source that shares a library's length and hash
   and pass for it. `TupleKeyComparerGuardTests` fails if the cache's comparer is lost.

## Re-vendoring a newer release

Copy `src/AngleSharp.Js/*.cs` from the new tag over `Vendor/AngleSharp.Js/`, replace `LICENSE`,
re-apply the eleven modifications (search for the marker; the namespace move is a script's work —
every `namespace` line and every `using AngleSharp.Js…`), and check the Jint version range it wants against
`Directory.Packages.props`. The limit tests in `Tetitulz.Pdf.Plugins.HtmlScript.Tests` fail if the
hook is lost, `ScriptGlobalsTests` if the filter or the getters are, and `NamespaceIsolationTests` if a
type is back in an `AngleSharp` namespace, and `TimerSettleTests.APagesTimers_ReachTheLog` if the timers
stop reporting, `ScriptBuiltinBoundsTests` if the engine hook is, and
`ScriptLimitTests.AdversarialBacktrackingPatternStopsAtTheScriptTimeout` if the regex timeout is, and
`ModuleImportTests` if a module is read off the disk again — that is what they are for.

## Beside the official package

A host may reference the official AngleSharp.Js package as well, any version: the vendored code is
in this assembly under its own namespace, so no name is seen twice, and its scripting service scans
only the services of the browsing context the plugin builds, so neither copy picks up the other's DOM
globals. The test project references AngleSharp.Js 1.1.0 for exactly this — `NamespaceIsolationTests`
calls the official `WithJs()` under `using AngleSharp;`, which does not compile if a vendored
extension is back in that namespace, and runs both engines in one process. What the two share is
AngleSharp itself and Jint, one version of each per application, as for any package.
