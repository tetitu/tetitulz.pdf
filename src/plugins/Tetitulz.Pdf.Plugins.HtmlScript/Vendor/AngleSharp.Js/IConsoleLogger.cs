// TETITULZ MODIFICATION (the plugin's VENDORED.md, item 4): was namespace AngleSharp.Js.
namespace Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs
{
    using AngleSharp;
    using System;

    /// <summary>
    /// Represents a console logger.
    /// </summary>
    public interface IConsoleLogger
    {
        /// <summary>
        /// Logs the given values.
        /// </summary>
        /// <param name="values">The values to log.</param>
        void Log(Object[] values);
    }
}
