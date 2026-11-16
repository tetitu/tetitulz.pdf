// TETITULZ MODIFICATION (the plugin's VENDORED.md, item 4): was namespace AngleSharp.Js.
namespace Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs
{
    using AngleSharp;
    using Jint.Native.Object;
    using System;
    using System.Runtime.CompilerServices;

    sealed class ReferenceCache
    {
        private readonly ConditionalWeakTable<Object, ObjectInstance> _references;

        public ReferenceCache()
        {
            _references = new ConditionalWeakTable<Object, ObjectInstance>();
        }

        public ObjectInstance GetOrCreate(Object obj, Func<Object, ObjectInstance> creator) =>
            _references.GetValue(obj, creator.Invoke);
    }
}
