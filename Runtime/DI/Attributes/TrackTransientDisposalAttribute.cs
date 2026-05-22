using System;

namespace Strada.Core.DI.Attributes
{
    /// <summary>
    /// Marks a Transient service whose <see cref="IDisposable"/> instances should be tracked
    /// by the container and disposed when the container is disposed.
    /// </summary>
    /// <remarks>
    /// <para>By default, the container does not track transient instances — callers are
    /// responsible for disposing them. Applying this attribute opts the type into automatic
    /// disposal tracking: every resolved instance is pushed onto the container's disposal
    /// stack and disposed in LIFO order during <c>Container.Dispose()</c>.</para>
    /// <para><b>Memory implication:</b> Resolving a tracked transient in a hot loop keeps a
    /// reference for the lifetime of the container. Use this attribute only when the number
    /// of resolutions is bounded or container lifetime is short.</para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = true)]
    public sealed class TrackTransientDisposalAttribute : Attribute
    {
    }
}
