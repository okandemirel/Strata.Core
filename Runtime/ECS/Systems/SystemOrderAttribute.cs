using System;

namespace Strada.Core.ECS.Systems
{
    /// <summary>
    /// Declares the relative execution order of a system. Lower values run first.
    /// </summary>
    /// <remarks>
    /// This lives in the runtime assembly because the code generators emit it onto generated
    /// ECS systems, which are runtime code. It previously sat in the Editor-only assembly
    /// (Strada.Core.Editor.CodeGen), so every system file produced by the template menu and
    /// the module generator referenced a type the runtime assembly cannot see — the generated
    /// code never compiled. That is the tool's primary happy path.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class)]
    public class SystemOrderAttribute : Attribute
    {
        public int Order { get; }

        public SystemOrderAttribute(int order = 0)
        {
            Order = order;
        }
    }
}
