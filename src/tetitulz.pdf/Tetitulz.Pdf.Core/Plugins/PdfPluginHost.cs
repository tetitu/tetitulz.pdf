// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Manages the lifecycle of <see cref="IPdfPlugin"/> instances and coordinates
    /// hook invocations throughout the processing pipeline.
    ///
    /// <para>
    /// Usage:
    /// <code>
    ///   var host = PdfPluginHost.Instance;
    ///   host.Register(new MyPlugin());
    ///   // ... use PdfOperations normally; auto-dispatched plugins intercept automatically ...
    ///   host.Unregister("MyCompany.MyPlugin");
    /// </code>
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para><b>Which plugin types the library dispatches automatically</b> — registering any
    /// other type has no effect until the host application resolves it itself via
    /// <see cref="GetPlugins{T}"/> / <see cref="GetFirstPlugin{T}"/>:</para>
    /// <list type="table">
    ///   <item><term><see cref="ITextExtractionHook"/></term>
    ///         <description>Per page during <c>PdfOperations.ExtractText</c>.</description></item>
    ///   <item><term><see cref="IRenderHook"/></term>
    ///         <description>After each page render.</description></item>
    ///   <item><term><see cref="IRendererPlugin"/></term>
    ///         <description>Replaces the built-in renderer on the PNG paths.</description></item>
    ///   <item><term><see cref="IImageDecodePlugin"/></term>
    ///         <description>Per declared image <c>/Filter</c>, during rendering.</description></item>
    ///   <item><term><see cref="IPostScriptPlugin"/></term>
    ///         <description>Per <c>/Subtype /PS</c> XObject and per inline <c>PS</c> operator,
    ///         during rendering (raster and SVG).</description></item>
    ///   <item><term><see cref="IExportPlugin"/> / <see cref="IImportPlugin"/></term>
    ///         <description>Via <c>PdfOperations.Export</c> / <c>Import</c>.</description></item>
    ///   <item><term><see cref="IValidationPlugin"/></term>
    ///         <description>Via <c>PdfOperations.ValidateConformance</c>.</description></item>
    ///   <item><term><see cref="IOcrPlugin"/></term>
    ///         <description>Via <c>PdfOperations.RecognizeTextWithOcr</c> (explicit, never implicit).</description></item>
    /// </list>
    /// <para>
    /// The remaining SDK interfaces (<see cref="IPdfProcessingHook"/>, <see cref="ISanitizePlugin"/>,
    /// <see cref="IOptimizePlugin"/>,
    /// <see cref="ILinearizePlugin"/>, <see cref="IMergePlugin"/>, <see cref="ISplitPlugin"/>,
    /// <see cref="IRedactPlugin"/>, <see cref="IWatermarkPlugin"/>, <see cref="IRepairPlugin"/>,
    /// <see cref="IAccessibilityPlugin"/>) are <b>discovery contracts</b>: they let a host reach a
    /// capability without referencing the implementing assembly. They are deliberately not
    /// auto-dispatched — see each interface's remarks.
    /// </para>
    /// <para><b><see cref="IPdfProcessingHook"/> is in that list.</b> Nothing in the library calls
    /// <see cref="InvokeBeforeProcess"/> / <see cref="InvokeAfterProcess"/> — the two methods exist
    /// so a host can run the hooks around its <i>own</i> pipeline — and <c>PdfOperations</c> says
    /// so at the top of its own source. The table said the opposite, and
    /// the table is the document a plugin author reads: a compliance plugin that stripped
    /// JavaScript in <c>OnBeforeProcess</c> would have been registered, reported as registered, and
    /// never once called, with every document written unsanitized and nothing reported. Do not move
    /// the row back without a dispatch site to point at.</para>
    /// <para><b>Thread safety:</b> all members are safe for concurrent use, and <b>no</b>
    /// plugin-implemented member is invoked while the registry lock is held — not
    /// <c>Initialize</c>, <c>Shutdown</c>, <c>CanRender</c> or <c>CanProcess</c>, and not the
    /// properties either (<see cref="IPdfPlugin.Name"/>,
    /// <see cref="IImageDecodePlugin.SupportedFilters"/>, <see cref="IRendererPlugin.Priority"/>,
    /// <see cref="IExportPlugin.FormatKey"/>, <see cref="IValidationPlugin.ConformanceLevel"/>).
    /// A plugin may therefore compute any of them lazily behind its own lock.</para>
    /// <para>The properties are in that sentence because evaluating them under the lock would be a
    /// lock-order inversion waiting for a plugin author to believe the documented promise. The host
    /// keeps its own name index and its own snapshot of each decode plugin's filter list, both built
    /// from a single read taken outside the lock, and every <c>Find*</c> orders and matches on a
    /// snapshot rather than inside the <c>lock</c>.</para>
    /// </remarks>
    public sealed class PdfPluginHost
    {
        // ---------------------------------------------------------------
        //  Singleton
        // ---------------------------------------------------------------

        private static readonly Lazy<PdfPluginHost> _instance =
            new Lazy<PdfPluginHost>(() => new PdfPluginHost());

        /// <summary>The process-wide singleton plugin host.</summary>
        public static PdfPluginHost Instance => _instance.Value;

        // ---------------------------------------------------------------
        //  State
        // ---------------------------------------------------------------

        private readonly List<IPdfPlugin> _plugins = new List<IPdfPlugin>();
        private readonly object _lock = new object();

        // Names reserved by an in-flight Register whose Initialize() is running outside the
        // lock. Checked alongside _plugins so two concurrent Registers of the same name still
        // produce exactly one registration.
        private readonly HashSet<string> _registering = new HashSet<string>(StringComparer.Ordinal);

        // Name → plugin, maintained by the host from the single `plugin.Name` read at the top of
        // Register. Every lock-held name test consults this instead of calling IPdfPlugin.Name on
        // each registered plugin, which is plugin code and must not run under the registry lock.
        private readonly Dictionary<string, IPdfPlugin> _byName =
            new Dictionary<string, IPdfPlugin>(StringComparer.Ordinal);

        // Fast filter-name → decode plugin lookup; rebuilt on every Register/Unregister.
        private Dictionary<string, IImageDecodePlugin> _decodeCache =
            new Dictionary<string, IImageDecodePlugin>(StringComparer.Ordinal);

        // Per-plugin snapshot of SupportedFilters, read and validated ONCE in Register (outside the
        // lock) so RebuildDecodeCache never has to call back into plugin code. Keyed by reference —
        // and here that is load-bearing rather than cautious: a plugin is third-party code free to
        // override Equals/GetHashCode, so two distinct registered instances that compare equal would
        // otherwise collapse into one entry and lose a plugin's filters.
        private readonly Dictionary<IImageDecodePlugin, string[]> _decodeFilters =
            new Dictionary<IImageDecodePlugin, string[]>(Objects.PdfReferenceEquality.Instance);

        private PdfPluginHost() { }

        // ---------------------------------------------------------------
        //  Registration
        // ---------------------------------------------------------------

        /// <summary>
        /// Registers a plugin and calls its <see cref="IPdfPlugin.Initialize()"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Initialize runs outside the registry lock.</b> It may allocate resources, read
        /// configuration or touch the disk, and holding the process-wide lock across an
        /// arbitrary plugin callback would stall every other Register/Unregister/Find* call —
        /// the same reasoning <see cref="FindRendererPlugin"/> applies to <c>CanRender</c>.
        /// The name is reserved before <c>Initialize</c> runs and released if it throws, so the
        /// plugin is registered only after a successful init and a duplicate can never slip in.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">If <paramref name="plugin"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// If the plugin's <see cref="IPdfPlugin.Name"/> is null or empty, or if it implements
        /// <see cref="IImageDecodePlugin"/> and its <see cref="IImageDecodePlugin.SupportedFilters"/>
        /// is null or contains a null / empty entry. That check happens <b>before</b> anything is
        /// committed, so a plugin refused for it leaves the registry exactly as it was.
        /// </exception>
        /// <exception cref="DuplicatePluginException">
        /// If a plugin with the same name is already registered.
        /// <para>
        /// <see cref="DuplicatePluginException"/> derives from <c>PdfException</c>, <b>not</b> from
        /// <see cref="InvalidOperationException"/>, so an existing
        /// <c>catch (InvalidOperationException)</c> around <c>Register</c> no longer catches a
        /// duplicate registration. Catch <see cref="DuplicatePluginException"/> (or
        /// <c>PdfException</c>) instead.
        /// </para>
        /// </exception>
        public void Register(IPdfPlugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));

            string name = plugin.Name;
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException(
                    "Plugin Name must be a non-empty unique identifier.", nameof(plugin));

            // Phase 0 — validate the decode-filter list BEFORE the plugin is committed, and read it
            // exactly once. Enumerating `dp.SupportedFilters` — arbitrary plugin code — inside the
            // registry lock AFTER `_plugins.Add`, a null list or a null name would throw with the
            // broken plugin already in `_plugins` and `_decodeCache` holding its pre-registration
            // value — so every LATER Register and Unregister in the process would throw from the
            // same rebuild. Validating here refuses the one broken plugin instead.
            var decodePlugin = plugin as IImageDecodePlugin;
            string[] decodeFilters = decodePlugin == null
                ? null
                : ValidateSupportedFilters(decodePlugin, name);

            // Phase 1 — claim the name.
            lock (_lock)
            {
                // Reads _byName, not `p.Name` over every registered plugin: Name is plugin code,
                // and the claim has to stay atomic, so the check consults the index the host
                // itself maintains rather than calling back into plugins under the lock.
                if (_byName.ContainsKey(name) || _registering.Contains(name))
                    throw new DuplicatePluginException(name);
                _registering.Add(name);
            }

            // Phase 2 — initialise outside the lock. Any failure releases the claim and
            // propagates to the caller unchanged (notably NOT as a duplicate).
            try
            {
                plugin.Initialize();
            }
            catch
            {
                lock (_lock) _registering.Remove(name);
                throw;
            }

            // Phase 3 — commit. The filter snapshot goes in first so RebuildDecodeCache reads only
            // validated data and can no longer throw with the plugin half-registered.
            lock (_lock)
            {
                _registering.Remove(name);
                if (decodeFilters != null) _decodeFilters[decodePlugin] = decodeFilters;
                _plugins.Add(plugin);
                _byName[name] = plugin;
                RebuildDecodeCache();
            }

            System.Diagnostics.Debug.WriteLine(
                $"[PdfPluginHost] Registered: {name} v{plugin.Version}");
        }

        /// <summary>
        /// Unregisters the plugin with the given name. Calls <see cref="IPdfPlugin.Shutdown()"/>.
        /// Returns true if a plugin was found and removed, false if not found.
        /// </summary>
        public bool Unregister(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            IPdfPlugin plugin;
            lock (_lock)
            {
                // _byName rather than a Name scan: see the note in Register.
                if (!_byName.TryGetValue(name, out var found)) return false;
                _byName.Remove(name);
                // Remove first, then Shutdown: a throwing Shutdown must not leave the plugin
                // registered (stale state).
                _plugins.Remove(found);
                if (found is IImageDecodePlugin removedDecoder)
                    _decodeFilters.Remove(removedDecoder);
                RebuildDecodeCache();
                plugin = found;
            }

            // Shutdown outside the lock — it releases resources and may block; holding the
            // registry lock across arbitrary plugin code would stall the process.
            try { plugin.Shutdown(); }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PdfPluginHost] {plugin.Name}.Shutdown threw: {ex.Message}");
            }
            return true;
        }

        /// <summary>Returns a snapshot of all currently registered plugins.</summary>
        public IReadOnlyList<IPdfPlugin> GetPlugins()
        {
            lock (_lock) return _plugins.ToArray();
        }

        /// <summary>Returns true if a plugin with the given name is registered.</summary>
        public bool IsRegistered(string name)
        {
            lock (_lock) return name != null && _byName.ContainsKey(name);
        }

        // ---------------------------------------------------------------
        //  Typed queries
        // ---------------------------------------------------------------

        /// <summary>
        /// Returns a snapshot of all registered plugins that implement <typeparamref name="T"/>,
        /// in registration order.
        /// </summary>
        public IReadOnlyList<T> GetPlugins<T>() where T : IPdfPlugin
        {
            lock (_lock) return _plugins.OfType<T>().ToArray();
        }

        /// <summary>
        /// Returns the first registered plugin that implements <typeparamref name="T"/>,
        /// or <c>null</c> if none is registered.
        /// </summary>
        public T? GetFirstPlugin<T>() where T : class, IPdfPlugin
        {
            lock (_lock) return _plugins.OfType<T>().FirstOrDefault();
        }

        /// <summary>
        /// Returns the highest-priority <see cref="IRendererPlugin"/> that reports
        /// it can render <paramref name="page"/>, or <c>null</c> to use the built-in renderer.
        /// Plugins are sorted by descending <see cref="IRendererPlugin.Priority"/>.
        /// </summary>
        public IRendererPlugin? FindRendererPlugin(PdfPage page, PdfDocument document)
        {
            // Snapshot under the lock, then evaluate CanRender OUTSIDE it: holding
            // the global registry lock across an arbitrary (possibly slow/hanging)
            // plugin callback would stall all Register/Unregister/Find* calls
            // process-wide. (Shutdown only releases resources, so a concurrently
            // unregistered plugin's CanRender is the plugin's own concern.)
            // The ORDERING moved out of the lock too. OrderByDescending is lazy, but ToArray()
            // forces it inside the lock, so `r.Priority` — plugin code — was evaluated there.
            IRendererPlugin[] snapshot;
            lock (_lock) snapshot = _plugins.OfType<IRendererPlugin>().ToArray();
            var ordered = snapshot.OrderByDescending(r => r.Priority).ToArray();
            foreach (var r in ordered)
                if (r.CanRender(page, document)) return r;
            return null;
        }

        /// <summary>
        /// Returns the first registered <see cref="IImageDecodePlugin"/> that supports
        /// <paramref name="filterName"/>, or <c>null</c> to fall back to the built-in decoder.
        /// Uses a cached lookup rebuilt on every Register/Unregister (O(1) hot path).
        /// </summary>
        public IImageDecodePlugin? FindDecodePlugin(string filterName)
        {
            if (filterName == null) return null;
            lock (_lock)
            {
                _decodeCache.TryGetValue(filterName, out var plugin);
                return plugin;
            }
        }

        /// <summary>
        /// Returns the first registered <see cref="IExportPlugin"/> whose
        /// <see cref="IExportPlugin.FormatKey"/> matches <paramref name="formatKey"/>
        /// (case-insensitive), or <c>null</c> if none is registered.
        /// </summary>
        public IExportPlugin? FindExportPlugin(string formatKey)
        {
            IExportPlugin[] snapshot;
            lock (_lock) snapshot = _plugins.OfType<IExportPlugin>().ToArray();
            return snapshot.FirstOrDefault(p =>
                string.Equals(p.FormatKey, formatKey, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns the first registered <see cref="IImportPlugin"/> whose
        /// <see cref="IImportPlugin.FormatKey"/> matches <paramref name="formatKey"/>
        /// (case-insensitive), or <c>null</c> if none is registered.
        /// </summary>
        public IImportPlugin? FindImportPlugin(string formatKey)
        {
            IImportPlugin[] snapshot;
            lock (_lock) snapshot = _plugins.OfType<IImportPlugin>().ToArray();
            return snapshot.FirstOrDefault(p =>
                string.Equals(p.FormatKey, formatKey, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns the first registered <see cref="IValidationPlugin"/> whose
        /// <see cref="IValidationPlugin.ConformanceLevel"/> matches <paramref name="conformanceLevel"/>
        /// (case-insensitive), or <c>null</c> if none is registered.
        /// </summary>
        public IValidationPlugin? FindValidationPlugin(string conformanceLevel)
        {
            if (conformanceLevel == null) return null;
            IValidationPlugin[] snapshot;
            lock (_lock) snapshot = _plugins.OfType<IValidationPlugin>().ToArray();
            return snapshot.FirstOrDefault(p =>
                string.Equals(p.ConformanceLevel, conformanceLevel, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns every registered <see cref="IValidationPlugin"/>, in registration order.
        /// Multiple conformance levels may be registered simultaneously.
        /// </summary>
        public IReadOnlyList<IValidationPlugin> GetValidationPlugins()
        {
            lock (_lock) return _plugins.OfType<IValidationPlugin>().ToArray();
        }

        /// <summary>
        /// Returns every registered <see cref="IConformanceConversionPlugin"/>, in registration
        /// order. Several targets may be registered simultaneously.
        /// </summary>
        public IReadOnlyList<IConformanceConversionPlugin> GetConformanceConversionPlugins()
        {
            lock (_lock) return _plugins.OfType<IConformanceConversionPlugin>().ToArray();
        }

        /// <summary>
        /// Returns the first registered <see cref="IOcrPlugin"/> that reports it can process
        /// pixels with the given component count and bit depth, or <c>null</c> if none can.
        /// </summary>
        public IOcrPlugin? FindOcrPlugin(int components, int bitsPerComponent)
        {
            // Snapshot under the lock, then call CanProcess outside it — an arbitrary plugin
            // callback must not be invoked while holding the process-wide registry lock
            // (same reasoning as FindRendererPlugin).
            IOcrPlugin[] snapshot;
            lock (_lock) snapshot = _plugins.OfType<IOcrPlugin>().ToArray();
            foreach (var p in snapshot)
                if (p.CanProcess(components, bitsPerComponent)) return p;
            return null;
        }

        /// <summary>
        /// The first registered plugin that can report word <b>positions</b>, not just text —
        /// what a searchable PDF needs. <c>null</c> when none is registered.
        /// </summary>
        /// <remarks>
        /// Deliberately not "FindOcrPlugin and cast": a host may register a text-only plugin and a
        /// word-capable one, and casting whichever came first would report "no engine" while an
        /// engine that could have done the job sat in the registry behind it.
        /// </remarks>
        public IOcrWordPlugin? FindOcrWordPlugin(int components, int bitsPerComponent)
        {
            IOcrWordPlugin[] snapshot;
            lock (_lock) snapshot = _plugins.OfType<IOcrWordPlugin>().ToArray();
            foreach (var p in snapshot)
                if (p.CanProcess(components, bitsPerComponent)) return p;
            return null;
        }

        /// <summary>
        /// Reads and validates a decode plugin's <see cref="IImageDecodePlugin.SupportedFilters"/>
        /// exactly once, outside the registry lock, before the plugin is committed.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The list is null, or contains a null / empty / whitespace filter name.
        /// </exception>
        private static string[] ValidateSupportedFilters(IImageDecodePlugin plugin, string name)
        {
            // The getter is arbitrary plugin code and may THROW as readily as it may return null —
            // a plugin computing its list from a config file does both when the file is missing.
            // Letting the plugin's own exception escape Register leaves the caller with an
            // InvalidOperationException (or anything else) out of a method whose contract is
            // ArgumentException, and with no indication of which plugin produced it.
            IReadOnlyList<string> declared;
            try { declared = plugin.SupportedFilters; }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    $"Plugin '{name}' threw from SupportedFilters: {ex.Message}", nameof(plugin), ex);
            }

            if (declared == null)
                throw new ArgumentException(
                    $"Plugin '{name}' implements IImageDecodePlugin but its SupportedFilters is null. " +
                    "Return an empty list to declare no filters.", nameof(plugin));

            var filters = new string[declared.Count];
            for (int i = 0; i < declared.Count; i++)
            {
                string filter = declared[i];
                if (string.IsNullOrWhiteSpace(filter))
                    throw new ArgumentException(
                        $"Plugin '{name}' declares a null or empty SupportedFilters entry at index {i}; " +
                        "every entry must be a PDF filter name.", nameof(plugin));
                filters[i] = filter;
            }
            return filters;
        }

        // Must be called inside _lock.
        //
        // Reads only the snapshots captured and validated by Register, never `dp.SupportedFilters`
        // again: enumerating arbitrary plugin code from inside the registry lock is what let one
        // plugin's null list wedge every later Register and Unregister in the process (and is a
        // lock-order inversion besides, since a plugin computing that property behind its own lock
        // would deadlock against a thread calling GetPlugins).
        private void RebuildDecodeCache()
        {
            var cache = new Dictionary<string, IImageDecodePlugin>(StringComparer.Ordinal);
            foreach (var p in _plugins)
            {
                if (!(p is IImageDecodePlugin dp)) continue;
                if (!_decodeFilters.TryGetValue(dp, out var filters)) continue;
                foreach (var filter in filters)
                    if (!cache.ContainsKey(filter))
                        cache[filter] = dp;
            }
            _decodeCache = cache;
        }

        // ---------------------------------------------------------------
        //  Assembly scanning
        // ---------------------------------------------------------------

        [ThreadStatic]
        private static string _lastRegistrationError;

        /// <summary>
        /// Reason the most recent <see cref="RegisterFromAssembly"/> / <see cref="RegisterFromDirectory"/>
        /// on <b>this thread</b> skipped a type or assembly, or <c>null</c> if none was skipped.
        /// Assembly scanning is failure-tolerant by design, so this is the only signal that
        /// something was dropped.
        /// </summary>
        /// <remarks>
        /// Backed by a <c>[ThreadStatic]</c> field, so concurrent scans on different threads do
        /// not overwrite each other's diagnostic (see the Diagnostics Pattern section of
        /// CLAUDE.md). Read it on the same thread that ran the scan.
        /// </remarks>
        public static string LastRegistrationError
        {
            get => _lastRegistrationError;
            private set => _lastRegistrationError = value;
        }

        /// <summary>Clears <see cref="LastRegistrationError"/>.</summary>
        public static void ResetLastRegistrationError() => _lastRegistrationError = null;

        /// <summary>
        /// Scans an assembly for all public, non-abstract types that implement
        /// <see cref="IPdfPlugin"/> and have a public parameterless constructor,
        /// instantiates them, and registers each one.
        /// <para>
        /// SECURITY: instantiating a discovered type executes its constructor (and
        /// the type/module initializers already ran when the assembly was loaded).
        /// Only pass assemblies you trust — a malicious plugin assembly runs
        /// arbitrary code in this process. This API performs no signature or
        /// sandbox verification.
        /// </para>
        /// </summary>
        /// <param name="assembly">The assembly to scan.</param>
        /// <returns>The number of plugins registered.</returns>
        /// <remarks>
        /// A type whose constructor or <see cref="IPdfPlugin.Initialize"/> throws is skipped and
        /// the reason recorded in <see cref="LastRegistrationError"/>; the scan continues.
        /// </remarks>
        public int RegisterFromAssembly(Assembly assembly)
        {
            // A Last* slot a caller reads AFTER an operation must be cleared at the START of
            // that operation, on every path — otherwise a clean scan reports the previous scan's
            // failure and the property's own contract ("or null if none was skipped") cannot
            // hold. This is the PdfDecryptor.LastPermsValidation defect: per-thread storage is
            // not per-operation scope.
            LastRegistrationError = null;

            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            int count = 0;
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IPdfPlugin).IsAssignableFrom(type)) continue;
                // A plugin that runs the document's own code is enabled only by name (Register).
                if (type.GetCustomAttribute<PdfPluginExplicitRegistrationAttribute>() != null) continue;
                var ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor == null) continue;
                try
                {
                    var plugin = (IPdfPlugin)ctor.Invoke(null);
                    Register(plugin);
                    count++;
                }
                catch (DuplicatePluginException)
                {
                    // Already registered — benign while scanning; skip silently. Not
                    // `catch (InvalidOperationException)`, which would also swallow a plugin whose
                    // Initialize() throws InvalidOperationException for bad configuration.
                }
                catch (Exception ex)
                {
                    // Constructor or Initialize failed. Record it and continue the scan so one
                    // bad plugin does not hide the rest of the assembly.
                    LastRegistrationError =
                        $"{type.FullName}: {ex.GetType().Name}: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine(
                        $"[PdfPluginHost] Failed to register {type.FullName}: {ex}");
                }
            }
            return count;
        }

        /// <summary>
        /// Scans all DLL files in <paramref name="directoryPath"/> for plugin assemblies,
        /// loads each that carries a <see cref="PdfPluginAssemblyAttribute"/>,
        /// and calls <see cref="RegisterFromAssembly"/> on it.
        /// <para>
        /// SECURITY: <c>Assembly.LoadFrom</c> executes the assembly's module/type
        /// initializers <i>before</i> the marker attribute is checked, so any DLL
        /// in this directory runs code in-process — the marker is not a security
        /// boundary. The directory MUST be trusted and writable only by trusted
        /// principals (treat it like a folder of executables). Loaded plugin
        /// assemblies are not unloadable for the process lifetime.
        /// </para>
        /// </summary>
        /// <param name="directoryPath">Directory to scan (non-recursive).</param>
        /// <returns>Total number of plugins registered across all assemblies.</returns>
        public int RegisterFromDirectory(string directoryPath)
        {
            // Reset FIRST: see the note on LastRegistrationError. RegisterFromAssembly below
            // resets it too, so a per-DLL reason is captured into a local and re-applied after.
            LastRegistrationError = null;

            if (directoryPath == null) throw new ArgumentNullException(nameof(directoryPath));
            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException($"Plugin directory not found: {directoryPath}");

            int total = 0;
            string skipped = null;
            foreach (var dll in Directory.GetFiles(directoryPath, "Tetitulz.Pdf.Plugins.*.dll"))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    // Only scan assemblies that explicitly opt-in via the marker attribute.
                    if (asm.GetCustomAttribute<PdfPluginAssemblyAttribute>() == null)
                    {
                        // Equally silent before, and the likelier cause of "my plugin does
                        // nothing": the DLL is present and loadable, it simply is not marked.
                        skipped ??= $"{dll}: no [PdfPluginAssembly] attribute, so it was not scanned.";
                        continue;
                    }
                    total += RegisterFromAssembly(asm);
                    skipped ??= LastRegistrationError;
                }
                catch (Exception ex)
                {
                    // Debug.WriteLine ALONE is compiled out of a Release build, so a plugin that
                    // failed to load — wrong architecture, missing transitive dependency, a
                    // blocked-file zone identifier — left no trace anywhere, while
                    // LastRegistrationError documents itself as "the only signal that something
                    // was dropped". RegisterFromLoadedAssemblies already records its equivalent.

                    skipped ??= $"{dll}: {ex.GetType().Name}: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine(
                        $"[PdfPluginHost] Failed to load assembly '{dll}': {ex.Message}");
                }
            }
            LastRegistrationError = skipped;
            return total;
        }

        /// <summary>Assembly-name prefix that identifies a first-party plugin assembly.</summary>
        private const string PluginAssemblyPrefix = "Tetitulz.Pdf.Plugins.";

        /// <summary>
        /// Registers every plugin found in plugin assemblies that this process has already loaded,
        /// plus (by default) any <c>Tetitulz.Pdf.Plugins.*</c> assembly that a loaded assembly
        /// merely <i>references</i>. Only assemblies carrying
        /// <see cref="PdfPluginAssemblyAttribute"/> are scanned. Returns the number registered.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists.</b> Referencing a plugin package was not enough to make it
        /// work: <c>Tetitulz.Pdf.Base</c> cannot reference a plugin (every plugin references
        /// <c>Base</c>, so the edge would be a <b>cycle</b>), so nothing wired the two together and
        /// the host had to call
        /// <see cref="Register"/> itself. The failure was silent and format-specific — a JPEG 2000
        /// image simply did not appear, because <c>Core</c>'s <c>JpxDecodeFilter</c> passes the
        /// codestream through by design and, until Core's own decoder, the pixels could
        /// only come from a registered <see cref="IImageDecodePlugin"/>. The decoder answers first
        /// now; a registered plugin is the second opinion when it declines.</para>
        /// <para><b>Still opt-in.</b> Nothing calls this for you; discovery that ran implicitly
        /// would change rendering behaviour just because a package is on disk. Call it once at
        /// startup when you want "referenced means enabled".</para>
        /// <para><b>Loading is by assembly name only</b> — this never probes a directory, so
        /// unlike <see cref="RegisterFromDirectory"/> it cannot load a DLL that the application
        /// did not already reference. The names it will load are restricted to the
        /// <c>Tetitulz.Pdf.Plugins.</c> prefix. Constructors of discovered plugin types still run,
        /// so the usual caveat applies: reference only plugin packages you trust.</para>
        /// <para>Already-registered plugins are skipped, so calling this twice is harmless and
        /// it composes with an explicit <see cref="Register"/>. Per-assembly failures are
        /// tolerated and recorded in <see cref="LastRegistrationError"/>.</para>
        /// </remarks>
        /// <param name="loadReferencedPlugins">
        /// When true (the default), also <c>Assembly.Load</c>s referenced-but-not-yet-loaded
        /// assemblies whose name starts with <c>Tetitulz.Pdf.Plugins.</c>. .NET loads an assembly
        /// lazily on first use, so without this a referenced plugin that the host never touches
        /// would not be in the loaded set and would not be found.
        /// </param>
        public int RegisterFromLoadedAssemblies(bool loadReferencedPlugins = true)
        {
            // Reset before the GetAssemblies try/catch below, which also writes the slot.
            LastRegistrationError = null;
            string scanError = null;

            var seen       = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<Assembly>();

            void Consider(Assembly asm)
            {
                if (asm == null || asm.IsDynamic) return;
                string key = asm.FullName ?? asm.GetName().Name;
                if (key != null && seen.Add(key)) candidates.Add(asm);
            }

            Assembly[] loaded;
            try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception ex)
            {
                LastRegistrationError = $"GetAssemblies: {ex.GetType().Name}: {ex.Message}";
                return 0;
            }

            foreach (var asm in loaded) Consider(asm);

            if (loadReferencedPlugins)
            {
                // Walk the references of everything already loaded rather than only the entry
                // assembly: under a test runner the entry assembly is the runner, which does not
                // reference the plugin — the test assembly does.
                foreach (var asm in loaded)
                {
                    if (asm.IsDynamic) continue;
                    AssemblyName[] refs;
                    try { refs = asm.GetReferencedAssemblies(); }
                    catch { continue; }

                    foreach (var refName in refs)
                    {
                        if (refName?.Name == null) continue;
                        if (!refName.Name.StartsWith(PluginAssemblyPrefix, StringComparison.Ordinal)) continue;
                        try { Consider(Assembly.Load(refName)); }
                        catch (Exception ex)
                        {
                            scanError ??= $"{refName.Name}: {ex.GetType().Name}: {ex.Message}";
                        }
                    }
                }
            }

            int total = 0;
            foreach (var asm in candidates)
            {
                try
                {
                    if (asm.GetCustomAttribute<PdfPluginAssemblyAttribute>() == null) continue;
                    total += RegisterFromAssembly(asm);
                    // RegisterFromAssembly resets the slot on entry, so a per-assembly reason has
                    // to be lifted out before the next assembly clears it.
                    scanError ??= LastRegistrationError;
                }
                catch (Exception ex)
                {
                    scanError ??= $"{asm.GetName().Name}: {ex.GetType().Name}: {ex.Message}";
                }
            }
            LastRegistrationError = scanError;
            return total;
        }

        // ---------------------------------------------------------------
        //  Hook invocations (called by the processing pipeline)
        // ---------------------------------------------------------------

        /// <summary>
        /// Invokes <see cref="IPdfProcessingHook.OnBeforeProcess"/> on all registered
        /// processing hooks, threading the bytes through each in registration order.
        /// </summary>
        /// <remarks>
        /// <b>Exceptions propagate.</b> Unlike the observer hooks
        /// (<see cref="InvokeTextExtracted"/> / <see cref="InvokePageRendered"/>), which swallow
        /// plugin failures so a misbehaving observer cannot abort the host's pipeline, a
        /// <i>transform</i> hook that throws aborts the operation. That is deliberate: this hook
        /// may rewrite the document bytes, so silently continuing would hand the caller output
        /// the plugin believed it had modified. A transform hook that wants to be optional must
        /// catch its own exceptions and return the input unchanged.
        /// </remarks>
        public byte[] InvokeBeforeProcess(string operationName, byte[] pdfBytes,
            System.Threading.CancellationToken cancellationToken = default)
            => ThreadThroughProcessingHooks(
                (hook, bytes) => hook.OnBeforeProcess(operationName, bytes, cancellationToken),
                pdfBytes, cancellationToken);

        /// <summary>
        /// Invokes <see cref="IPdfProcessingHook.OnAfterProcess"/> on all registered
        /// processing hooks in registration order.
        /// </summary>
        /// <remarks>Exceptions propagate — see <see cref="InvokeBeforeProcess"/>.</remarks>
        public byte[] InvokeAfterProcess(string operationName, byte[] pdfBytes,
            System.Threading.CancellationToken cancellationToken = default)
            => ThreadThroughProcessingHooks(
                (hook, bytes) => hook.OnAfterProcess(operationName, bytes, cancellationToken),
                pdfBytes, cancellationToken);

        /// <summary>
        /// Threads <paramref name="pdfBytes"/> through every registered
        /// <see cref="IPdfProcessingHook"/> in registration order, applying
        /// <paramref name="stage"/> at each one and keeping the input where a hook returns null.
        /// </summary>
        /// <remarks>
        /// <para><b>The two public transform hooks differed in the interface method named and in
        /// nothing else</b> — same snapshot under the lock, same per-plugin cancellation check,
        /// same type test, same null-coalesce, same accumulation. A rule written twice is a rule
        /// that can be changed once, and the halves that could have drifted here are the ones a
        /// caller cannot see: dropping the cancellation check inside the loop, or discarding what a
        /// hook returned.</para>
        /// <para><b>The snapshot under the lock is not incidental.</b> A hook may register or
        /// unregister a plugin while the pipeline is running, and enumerating <c>_plugins</c>
        /// directly would throw. Copying once per invocation also fixes the set a given operation
        /// sees, so a plugin registered mid-pipeline runs from the next operation rather than
        /// halfway through this one.</para>
        /// <para>The closure per call is deliberate and not a hot path: these are host entry points
        /// called once per operation, and <c>PdfOperations</c> does not call them at all.</para>
        /// </remarks>
        private byte[] ThreadThroughProcessingHooks(
            System.Func<IPdfProcessingHook, byte[], byte[]> stage,
            byte[] pdfBytes, System.Threading.CancellationToken cancellationToken)
        {
            IPdfPlugin[] plugins;
            lock (_lock) plugins = _plugins.ToArray();
            foreach (var p in plugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (p is IPdfProcessingHook hook)
                    pdfBytes = stage(hook, pdfBytes) ?? pdfBytes;
            }
            return pdfBytes;
        }

        /// <summary>Notifies all <see cref="ITextExtractionHook"/> plugins of extracted text.</summary>
        public void InvokeTextExtracted(int pageIndex, string text,
            System.Threading.CancellationToken cancellationToken = default)
        {
            IPdfPlugin[] plugins;
            lock (_lock) plugins = _plugins.ToArray();
            foreach (var p in plugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (p is ITextExtractionHook hook)
                {
                    // Observer notification: isolate a misbehaving plugin so it
                    // can't abort the host's extraction pipeline. Cancellation
                    // still propagates.
                    try { hook.OnTextExtracted(pageIndex, text, cancellationToken); }
                    catch (System.OperationCanceledException) { throw; }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[PdfPluginHost] {p.Name}.OnTextExtracted threw: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>Notifies all <see cref="IRenderHook"/> plugins of a rendered page.</summary>
        public void InvokePageRendered(int pageIndex, int widthPx, int heightPx,
            System.Threading.CancellationToken cancellationToken = default)
        {
            IPdfPlugin[] plugins;
            lock (_lock) plugins = _plugins.ToArray();
            foreach (var p in plugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (p is IRenderHook hook)
                {
                    // Observer notification: isolate per-plugin failures (see InvokeTextExtracted).
                    try { hook.OnPageRendered(pageIndex, widthPx, heightPx, cancellationToken); }
                    catch (System.OperationCanceledException) { throw; }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[PdfPluginHost] {p.Name}.OnPageRendered threw: {ex.Message}");
                    }
                }
            }
        }

        // ---------------------------------------------------------------
        //  Teardown
        // ---------------------------------------------------------------

        /// <summary>
        /// Shuts down every registered plugin and clears the registry. Failures in an
        /// individual <see cref="IPdfPlugin.Shutdown"/> are isolated and logged so one bad
        /// plugin cannot prevent the rest from shutting down.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This type deliberately does <b>not</b> implement <see cref="IDisposable"/>.
        /// <see cref="Instance"/> is a process-wide singleton backed by a <see cref="Lazy{T}"/>,
        /// so a disposed host could never be recreated — and the idiomatic
        /// <c>using (var h = PdfPluginHost.Instance)</c> would silently unregister every plugin
        /// process-wide, unrecoverably, in a block that reads like scoped cleanup. Teardown is
        /// therefore an explicit, obviously-global call.
        /// </para>
        /// <para>The host remains usable afterwards; plugins can be registered again.</para>
        /// </remarks>
        public void ShutdownAll()
        {
            List<IPdfPlugin> copy;
            lock (_lock)
            {
                copy = _plugins.ToList();
                _plugins.Clear();
                _byName.Clear();
                _decodeFilters.Clear();
                _decodeCache = new Dictionary<string, IImageDecodePlugin>(StringComparer.Ordinal);
            }

            // Shutdown outside the lock: it releases resources and may block, and holding the
            // registry lock across arbitrary plugin code would stall the process (same reasoning
            // as Register/FindRendererPlugin).
            foreach (var p in copy)
            {
                try { p.Shutdown(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PdfPluginHost] Shutdown threw from {p.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Clears all plugin registrations without invoking their <see cref="IPdfPlugin.Shutdown"/>
        /// hooks. Exists so tests that registered plugins can restore pristine state
        /// across test runs without relying on process lifetime.
        /// </summary>
        public void ResetForTesting()
        {
            lock (_lock)
            {
                _plugins.Clear();
                _byName.Clear();
                _decodeFilters.Clear();
                _registering.Clear();
                _decodeCache = new Dictionary<string, IImageDecodePlugin>(StringComparer.Ordinal);
            }
        }
    }
}
