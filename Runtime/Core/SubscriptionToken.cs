using System;
using System.Threading;

namespace Strada.Core
{
    /// <summary>
    /// Opaque disposable handle for a subscription created by Strada's reactive
    /// primitives (<see cref="Strada.Core.Communication.EventBus"/>,
    /// <see cref="Strada.Core.Sync.ReactiveProperty{T}"/>, etc.).
    /// </summary>
    /// <remarks>
    /// <para>Disposing the token removes <i>exactly</i> the handler it represents —
    /// no other subscribers on the same signal/property are affected.</para>
    /// <para>Disposal is idempotent: a second <see cref="Dispose"/> call is a
    /// no-op, so it is safe to add the token to a <see cref="Strada.Core.Sync.BindingScope"/>
    /// and also keep a local reference.</para>
    /// </remarks>
    public sealed class SubscriptionToken : IDisposable
    {
        private Action _dispose;

        internal SubscriptionToken(Action dispose)
        {
            _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        }

        /// <summary>True until the token has been disposed.</summary>
        public bool IsActive => _dispose != null;

        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _dispose, null);
            d?.Invoke();
        }
    }
}
