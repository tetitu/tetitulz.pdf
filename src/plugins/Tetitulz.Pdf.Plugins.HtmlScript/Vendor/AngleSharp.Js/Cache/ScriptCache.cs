// TETITULZ MODIFICATION (the plugin's VENDORED.md, item 4): was namespace AngleSharp.Js.Cache.
namespace Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs.Cache
{
    using AngleSharp;
    using Acornima.Ast;
    using Jint;
    using Jint.Runtime;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using Tetitulz.Pdf.Core.Util;   // TETITULZ MODIFICATION (../../VENDORED.md, item 11)

    /// <summary>
    /// Caches the parsed and analyzed form of scripts. A prepared script is
    /// documented by Jint as reusable and thread-safe, so the same one can serve
    /// every document that runs the same source - a page loading a library ends up
    /// parsing it once instead of once per document.
    /// </summary>
    static class ScriptCache
    {
        //  There is no bound on how many distinct scripts a process may see, so the
        //  cache does not grow without end. The scripts worth keeping are the ones
        //  seen first: libraries are referenced early and by many documents.
        private const Int32 Capacity = 32;

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 10): what the cache keeps is bounded and earned.
        //  A source longer than this is prepared and not kept: the slots hold libraries, not a page's
        //  inlined megabytes.
        internal const Int32 MaxCachedSourceLength = 512 * 1024;

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 10): a source is kept on its second preparation,
        //  so a page's one-off scripts never take a slot; the sources seen once are remembered by hash,
        //  bounded, and forgotten together when the set fills.
        private const Int32 SeenOnceCapacity = 4096;

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 11): a source hashes under a key the page cannot
        //  see, as every tuple key holding a string does here. Declared before the cache it keys: static
        //  fields are initialised in order.
        private static readonly IEqualityComparer<(String Source, TimeSpan? RegexTimeout)> SourceKeys =
            KeyedHash.Comparer<(String Source, TimeSpan? RegexTimeout)>(
                k => unchecked(KeyedHash.Of(k.Source) * 31 + k.RegexTimeout.GetHashCode()));

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 7): keyed by the regex timeout too, since a
        //  prepared script's regular-expression literals keep the timeout they were prepared with.
        private static readonly ConcurrentDictionary<(String Source, TimeSpan? RegexTimeout), Entry> _scripts =
            new ConcurrentDictionary<(String Source, TimeSpan? RegexTimeout), Entry>(SourceKeys);

        private static readonly ConcurrentDictionary<(Int32 Length, Int32 Hash, TimeSpan? RegexTimeout), Byte> _seenOnce =
            new ConcurrentDictionary<(Int32 Length, Int32 Hash, TimeSpan? RegexTimeout), Byte>();

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 10): an entry counts the times it was served, and
        //  a full cache makes room for a source seen twice by dropping the entry served least.
        private sealed class Entry
        {
            public Prepared<Script> Script;
            public Int64 Hits;
        }

        /// <summary>
        /// Gets the prepared form of the given source. An invalid result means the
        /// source could not be prepared and should be handed to the engine as text,
        /// so that the engine reports the syntax error itself.
        /// </summary>
        public static Prepared<Script> GetOrCreate(String source, TimeSpan? regexTimeout = null)
        {
            var key = (source, regexTimeout);
            if (_scripts.TryGetValue(key, out var entry))
            {
                Interlocked.Increment(ref entry.Hits);
                return entry.Script;
            }

            Prepared<Script> prepared;
            try
            {
                prepared = regexTimeout == null
                    ? Engine.PrepareScript(source)
                    : Engine.PrepareScript(source, options: ScriptPreparationOptions.Default with
                    {
                        ParsingOptions = ScriptPreparationOptions.Default.ParsingOptions with { RegexTimeout = regexTimeout },
                    });
            }
            catch (ScriptPreparationException)
            {
                return default;
            }

            Admit(key, prepared);
            return prepared;
        }

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 10): the admission rule. A source is kept when it
        //  is short enough and prepared for the second time; with every slot taken, the entry served
        //  least gives way to it.
        private static void Admit((String Source, TimeSpan? RegexTimeout) key, Prepared<Script> prepared)
        {
            if (key.Source.Length > MaxCachedSourceLength) return;

            //  TETITULZ MODIFICATION (../../VENDORED.md, item 11): KeyedHash, so a page cannot make its
            //  source pass for a library's.
            var seenKey = (key.Source.Length, KeyedHash.Of(key.Source), key.RegexTimeout);
            if (_seenOnce.TryAdd(seenKey, 0))
            {
                if (_seenOnce.Count > SeenOnceCapacity) _seenOnce.Clear();
                return;
            }
            _seenOnce.TryRemove(seenKey, out _);

            if (_scripts.Count >= Capacity)
            {
                (String Source, TimeSpan? RegexTimeout)? least = null;
                Int64 fewest = Int64.MaxValue;
                foreach (var pair in _scripts)
                {
                    Int64 hits = Interlocked.Read(ref pair.Value.Hits);
                    if (hits < fewest) { fewest = hits; least = pair.Key; }
                }
                if (least is { } gone) _scripts.TryRemove(gone, out _);
            }
            _scripts.TryAdd(key, new Entry { Script = prepared });
        }

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 10): for the plugin's tests.
        internal static Boolean Holds(String source, TimeSpan? regexTimeout = null) => _scripts.ContainsKey((source, regexTimeout));

        internal static Int32 Count => _scripts.Count;

        internal static void Clear()
        {
            _scripts.Clear();
            _seenOnce.Clear();
        }
    }
}
