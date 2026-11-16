// TETITULZ MODIFICATION (the plugin's VENDORED.md, item 4): was namespace AngleSharp.Js.
namespace Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs
{
    using AngleSharp;
    using System;

    /// <summary>
    /// Options tuning the JavaScript engine.
    /// </summary>
    public sealed class JsScriptingOptions
    {
        /// <summary>
        /// Gets or sets the JavaScript call stack depth that has to be supported
        /// before the engine gives up with a "Maximum call stack size exceeded"
        /// error. Defaults to 10000, which is roughly what browsers allow. Values
        /// of zero or less remove the limit - the engine is then bounded by the
        /// native stack alone, so a runaway recursion terminates the process with
        /// an uncatchable StackOverflowException.
        /// </summary>
        public Int32 MaxCallStackDepth { get; set; } = 10000;

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 1).
        //  A hook onto the Jint options every engine is built with. Upstream exposes only the
        //  call-stack depth, and Jint's statement, memory and deadline constraints can be set
        //  only while the engine is being built (Engine.Options is internal) — so without this
        //  a page script's infinite loop could not be stopped in-process at all.
        /// <summary>
        /// Gets or sets a callback applied to the Jint options of every engine this service
        /// builds, after its own configuration — where a host adds its statement, memory and
        /// time constraints. Null leaves the engine as upstream builds it.
        /// </summary>
        public Action<Jint.Options> ConfigureEngine { get; set; }

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 6). A hook onto the finished engine,
        //  before any script of the window runs: where a host runs its prelude — the bounds on
        //  the string built-ins one call could build gigabytes with (Core.Scripting.ScriptBuiltinBounds).
        /// <summary>
        /// Gets or sets a callback applied to every engine this service builds, after the window's
        /// globals are in place and before any of its scripts runs. Null leaves the engine as built.
        /// </summary>
        public Action<Jint.Engine> PrepareEngine { get; set; }

        //  TETITULZ MODIFICATION (../../VENDORED.md, item 7). A script is prepared once and cached,
        //  and a prepared script's regular-expression literals carry the timeout they were
        //  prepared with — Jint's own ten seconds — whatever the engine that evaluates them says.
        /// <summary>
        /// Gets or sets the match timeout of the regular-expression literals of the scripts this
        /// service prepares. Null keeps Jint's default.
        /// </summary>
        public TimeSpan? RegexTimeout { get; set; }

        //  An engine is built per window, long after the options were handed over, so
        //  reading them then would let a later edit of the caller's object decide how
        //  the next document behaves. The service takes this copy instead.
        internal JsScriptingOptions Clone() => new JsScriptingOptions
        {
            MaxCallStackDepth = MaxCallStackDepth,
            ConfigureEngine = ConfigureEngine,
            PrepareEngine = PrepareEngine,
            RegexTimeout = RegexTimeout,
        };
    }
}
