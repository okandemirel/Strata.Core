using System;

namespace Strada.Core.DI.AutoBinding
{
    /// <summary>
    /// Marks an assembly as opt-in for Strada auto-binding scanning.
    /// </summary>
    /// <remarks>
    /// <para>Apply at the assembly level: <c>[assembly: AutoBindingScope]</c>.</para>
    /// <para>Without this attribute, an assembly matching the include patterns will still
    /// be scanned but a deprecation warning is logged once per session. In a future major
    /// release the scanner will refuse to scan assemblies that lack this attribute, making
    /// pattern matches alone insufficient. Adding this attribute today is forward-compatible
    /// and silences the warning.</para>
    /// <para>Strada's own assemblies (<c>Strada.*</c>) are implicitly trusted and do not
    /// require the attribute.</para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class AutoBindingScopeAttribute : Attribute
    {
    }
}
