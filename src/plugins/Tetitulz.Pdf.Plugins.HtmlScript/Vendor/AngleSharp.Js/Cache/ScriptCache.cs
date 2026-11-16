// TETITULZ MODIFICATION (the plugin's VENDORED.md, item 4): was namespace AngleSharp.Js.Cache.
namespace Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs.Cache
{
    using AngleSharp;
    using Acornima.Ast;
    using Jint;
    using Jint.Runtime;
    using System;
    using System.Collections.Concurrent;

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

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 7): keyed by the regex timeout too, since a
        //  prepared script's regular-expression literals keep the timeout they were prepared with.
        private static readonly ConcurrentDictionary<(String Source, TimeSpan? RegexTimeout), Prepared<Script>> _scripts =
            new ConcurrentDictionary<(String Source, TimeSpan? RegexTimeout), Prepared<Script>>();

        /// <summary>
        /// Gets the prepared form of the given source. An invalid result means the
        /// source could not be prepared and should be handed to the engine as text,
        /// so that the engine reports the syntax error itself.
        /// </summary>
        public static Prepared<Script> GetOrCreate(String source, TimeSpan? regexTimeout = null)
        {
            var key = (source, regexTimeout);
            if (_scripts.TryGetValue(key, out var prepared))
            {
                return prepared;
            }

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

            if (_scripts.Count < Capacity)
            {
                _scripts.TryAdd(key, prepared);
            }

            return prepared;
        }
    }
}
