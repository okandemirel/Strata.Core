using System;
using System.Threading;

namespace Strada.Core.DI
{
    /// <summary>
    /// Holds the source-generated direct factory delegate for a given service type.
    /// </summary>
    /// <remarks>
    /// This class is intended to be populated by the source-generated
    /// <c>StradaGeneratedInitializer</c>. External callers should register services via
    /// <see cref="IContainerBuilder.Register{T}"/> instead of calling <see cref="Register"/>
    /// directly — direct registration bypasses container lifetime tracking.
    /// </remarks>
    public static class DirectFactory<T> where T : class
    {
        private static Func<IContainer, T> _delegate;

        /// <summary>Gets the registered factory, or null if none is registered.</summary>
        internal static Func<IContainer, T> Get() => Volatile.Read(ref _delegate);

        /// <summary>
        /// Registers a direct factory for <typeparamref name="T"/>. Intended for the
        /// source-generated registry initializer. Subsequent calls overwrite the previous
        /// factory.
        /// </summary>
        public static void Register(Func<IContainer, T> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            Volatile.Write(ref _delegate, factory);
        }

        /// <summary>Clears the registered factory. Used at container disposal / test teardown.</summary>
        public static void Clear() => Volatile.Write(ref _delegate, null);
    }
}
