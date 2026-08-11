using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.DI;
using Strada.Core.ECS;
using Strada.Core.Patterns;

namespace Strada.Core.Sync
{
    public interface IMediatorRegistry : IDisposable
    {
        TMediator Create<TMediator, TView>(Entity entity, TView view)
            where TMediator : EntityMediator<TView>, new()
            where TView : View;
        void Release<TMediator, TView>(TMediator mediator)
            where TMediator : EntityMediator<TView>, new()
            where TView : View;
        void SyncAll();
        void ReleaseAll();
    }

    public sealed class MediatorRegistry : IMediatorRegistry
    {
        private readonly IContainer _container;
        private readonly List<IDisposable> _activeMediators = new(64);
        // Index-parallel with _activeMediators. EntityMediator<TView> has no non-generic base,
        // so a List<IDisposable> cannot be walked as mediators; capturing SyncBindings at
        // Create time is what makes SyncAll implementable at all.
        private readonly List<Action> _syncCallbacks = new(64);
        private bool _disposed;

        public int ActiveCount => _activeMediators.Count;

        public MediatorRegistry(IContainer container)
        {
            _container = container;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TMediator Create<TMediator, TView>(Entity entity, TView view)
            where TMediator : EntityMediator<TView>, new()
            where TView : View
        {
            var mediator = MediatorPool<TMediator, TView>.Instance.Rent();
            mediator.Initialize(_container);
            mediator.Bind(entity, view);
            _activeMediators.Add(mediator);
            _syncCallbacks.Add(mediator.SyncBindings);
            return mediator;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release<TMediator, TView>(TMediator mediator)
            where TMediator : EntityMediator<TView>, new()
            where TView : View
        {
            mediator.Unbind();
            int index = _activeMediators.IndexOf(mediator);
            if (index >= 0)
            {
                _activeMediators.RemoveAt(index);
                _syncCallbacks.RemoveAt(index);
            }
            MediatorPool<TMediator, TView>.Instance.Return(mediator);
        }

        /// <summary>
        /// Syncs the bindings of every active mediator. This used to be an empty body, so any
        /// caller that wired it into an update loop got zero mediators synced instead of N.
        /// </summary>
        public void SyncAll()
        {
            if (_disposed) return;

            for (int i = 0; i < _syncCallbacks.Count; i++)
            {
                _syncCallbacks[i]();
            }
        }

        public void ReleaseAll()
        {
            for (int i = _activeMediators.Count - 1; i >= 0; i--)
            {
                _activeMediators[i].Dispose();
            }
            _activeMediators.Clear();
            _syncCallbacks.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseAll();
        }

    }

    internal static class MediatorPool<TMediator, TView>
        where TMediator : EntityMediator<TView>, new()
        where TView : View
    {
        private static MediatorPoolInstance _instance;
        public static MediatorPoolInstance Instance => _instance ??= new MediatorPoolInstance();

        internal sealed class MediatorPoolInstance
        {
            // Cap pool size to prevent unbounded growth if mediators are released
            // faster than they are rented (eg. during teardown of large scenes).
            private const int MaxPoolSize = 256;

            private readonly Stack<TMediator> _available = new(16);
            private readonly object _lock = new();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public TMediator Rent()
            {
                lock (_lock)
                {
                    return _available.Count > 0 ? _available.Pop() : new TMediator();
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Return(TMediator mediator)
            {
                lock (_lock)
                {
                    if (_available.Count >= MaxPoolSize) return;  // pool full — drop the instance
                    _available.Push(mediator);
                }
            }
        }
    }
}
