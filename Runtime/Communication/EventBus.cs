using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Strada.Core.Commands;
using UnityEngine;

namespace Strada.Core.Communication
{
    public interface IQuery<TResult> { }

    public interface IQueryHandler<TQuery, TResult> where TQuery : struct, IQuery<TResult>
    {
        TResult Handle(ref TQuery query);
    }

    /// <summary>
    /// Async query marker interface for queries that return results asynchronously.
    /// </summary>
    public interface IAsyncQuery<TResult> { }

    /// <summary>
    /// Async query handler using ValueTask for optimal performance.
    /// </summary>
    public interface IAsyncQueryHandler<TQuery, TResult> where TQuery : struct, IAsyncQuery<TResult>
    {
        ValueTask<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
    }

    /// <summary>
    /// Signal bus for one-to-one command dispatching.
    /// </summary>
    public interface ISignalBus
    {
        void Send<TSignal>(ref TSignal signal) where TSignal : struct;
        void Send<TSignal>(TSignal signal) where TSignal : struct;
        Strada.Core.SubscriptionToken RegisterSignalHandler<TSignal>(Action<TSignal> handler) where TSignal : struct;
        Strada.Core.SubscriptionToken RegisterSignalHandler<TSignal>(ISignalHandler<TSignal> handler) where TSignal : struct;
        bool HasSignalHandler<TSignal>() where TSignal : struct;
        ValueTask SendAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default) where TSignal : struct;
        void RegisterAsyncSignalHandler<TSignal>(IAsyncSignalHandler<TSignal> handler) where TSignal : struct;
        void RegisterAsyncSignalHandler<TSignal>(Func<TSignal, CancellationToken, ValueTask> handler) where TSignal : struct;
    }

    /// <summary>
    /// Query bus for request-response patterns.
    /// </summary>
    public interface IQueryBus
    {
        TResult Query<TQuery, TResult>(ref TQuery query) where TQuery : struct, IQuery<TResult>;
        TResult Query<TQuery, TResult>(TQuery query) where TQuery : struct, IQuery<TResult>;
        Strada.Core.SubscriptionToken RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler) where TQuery : struct, IQuery<TResult>;
        Strada.Core.SubscriptionToken RegisterQueryHandler<TQuery, TResult>(Func<TQuery, TResult> handler) where TQuery : struct, IQuery<TResult>;
        ValueTask<TResult> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default) where TQuery : struct, IAsyncQuery<TResult>;
        void RegisterAsyncQueryHandler<TQuery, TResult>(IAsyncQueryHandler<TQuery, TResult> handler) where TQuery : struct, IAsyncQuery<TResult>;
        void RegisterAsyncQueryHandler<TQuery, TResult>(Func<TQuery, CancellationToken, ValueTask<TResult>> handler) where TQuery : struct, IAsyncQuery<TResult>;
    }

    /// <summary>
    /// Event publisher for one-to-many event broadcasting.
    /// </summary>
    public interface IEventPublisher
    {
        void Publish<TEvent>(ref TEvent message) where TEvent : struct;
        void Publish<TEvent>(TEvent message) where TEvent : struct;
        Strada.Core.SubscriptionToken Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
        int GetSubscriberCount<TEvent>() where TEvent : struct;
    }

    /// <summary>
    /// Unified event bus combining signal, query, and event functionality.
    /// </summary>
    public interface IEventBus : ISignalBus, IQueryBus, IEventPublisher, IDisposable
    {
        void Clear();
    }

    public sealed class EventBus : IEventBus
    {
        private static int _nextSignalTypeId;
        private static int _nextQueryTypeId;
        private static int _nextEventTypeId;
        private static int _nextAsyncSignalTypeId;
        private static int _nextAsyncQueryTypeId;

        private readonly object _lock = new object();

        private object[] _signalHandlers = new object[64];
        private object[] _queryHandlers = new object[64];
        private object[] _eventChannels = new object[64];
        private object[] _asyncSignalHandlers = new object[64];
        private object[] _asyncQueryHandlers = new object[64];
        private bool _disposed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send<TSignal>(ref TSignal signal) where TSignal : struct
        {
            if (_disposed) ThrowDisposed();

            var id = SignalTypeId<TSignal>.Id;
            var handlers = Volatile.Read(ref _signalHandlers);
            if (id < handlers.Length && handlers[id] != null)
            {
                ((Action<TSignal>)handlers[id])(signal);
                return;
            }

            ThrowHandlerNotFoundException<TSignal>("signal");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send<TSignal>(TSignal signal) where TSignal : struct
        {
            Send(ref signal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TResult Query<TQuery, TResult>(ref TQuery query) where TQuery : struct, IQuery<TResult>
        {
            if (_disposed) ThrowDisposed();

            var id = QueryTypeId<TQuery>.Id;
            var handlers = Volatile.Read(ref _queryHandlers);
            if (id < handlers.Length && handlers[id] != null)
                return ((IQueryHandler<TQuery, TResult>)handlers[id]).Handle(ref query);

            ThrowHandlerNotFoundException<TQuery>("query");
            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TResult Query<TQuery, TResult>(TQuery query) where TQuery : struct, IQuery<TResult>
        {
            return Query<TQuery, TResult>(ref query);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Publish<TEvent>(ref TEvent message) where TEvent : struct
        {
            if (_disposed) ThrowDisposed();

            var id = EventTypeId<TEvent>.Id;
            var channels = Volatile.Read(ref _eventChannels);
            if (id >= channels.Length) return;

            var channel = channels[id] as EventChannel<TEvent>;
            channel?.Publish(ref message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Publish<TEvent>(TEvent message) where TEvent : struct
        {
            Publish(ref message);
        }

        /// <summary>
        /// Registers a signal handler and returns a <see cref="Strada.Core.SubscriptionToken"/>
        /// whose disposal removes <paramref name="handler"/> from the single-handler slot
        /// for <typeparamref name="TSignal"/>. Disposal is race-safe: if a later
        /// <c>RegisterSignalHandler</c> call has already replaced the slot with a
        /// different handler, the token does not clear it.
        /// </summary>
        public Strada.Core.SubscriptionToken RegisterSignalHandler<TSignal>(Action<TSignal> handler) where TSignal : struct
        {
            if (_disposed) ThrowDisposed();
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (_lock)
            {
                var id = SignalTypeId<TSignal>.Id;
                EnsureCapacity(ref _signalHandlers, id);
                if (_signalHandlers[id] != null)
                    Debug.LogWarning($"[EventBus] Signal handler for '{typeof(TSignal).Name}' is being replaced.");

                Volatile.Write(ref _signalHandlers[id], handler);
            }

            return new Strada.Core.SubscriptionToken(() =>
            {
                if (_disposed) return;
                lock (_lock)
                {
                    var id = SignalTypeId<TSignal>.Id;
                    var arr = _signalHandlers;
                    if (id < arr.Length && ReferenceEquals(arr[id], handler))
                        Volatile.Write(ref arr[id], null);
                }
            });
        }

        public Strada.Core.SubscriptionToken RegisterSignalHandler<TSignal>(ISignalHandler<TSignal> handler) where TSignal : struct
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return RegisterSignalHandler<TSignal>(handler.Handle);
        }

        public bool HasSignalHandler<TSignal>() where TSignal : struct
        {
            if (_disposed) return false;

            var id = SignalTypeId<TSignal>.Id;
            var handlers = Volatile.Read(ref _signalHandlers);
            return id < handlers.Length && handlers[id] != null;
        }

        /// <summary>
        /// Registers a query handler and returns a <see cref="Strada.Core.SubscriptionToken"/>
        /// whose disposal removes <paramref name="handler"/> from the single-handler slot
        /// for <typeparamref name="TQuery"/>. Disposal is race-safe: if a later
        /// <c>RegisterQueryHandler</c> call has already replaced the slot with a
        /// different handler, the token does not clear it.
        /// </summary>
        public Strada.Core.SubscriptionToken RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            if (_disposed) ThrowDisposed();
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (_lock)
            {
                var id = QueryTypeId<TQuery>.Id;
                EnsureCapacity(ref _queryHandlers, id);
                Volatile.Write(ref _queryHandlers[id], handler);
            }

            return new Strada.Core.SubscriptionToken(() =>
            {
                if (_disposed) return;
                lock (_lock)
                {
                    var id = QueryTypeId<TQuery>.Id;
                    var arr = _queryHandlers;
                    if (id < arr.Length && ReferenceEquals(arr[id], handler))
                        Volatile.Write(ref arr[id], null);
                }
            });
        }

        public Strada.Core.SubscriptionToken RegisterQueryHandler<TQuery, TResult>(Func<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return RegisterQueryHandler(new DelegateQueryHandler<TQuery, TResult>(handler));
        }

        /// <summary>
        /// Subscribes <paramref name="handler"/> to events of type <typeparamref name="TEvent"/>
        /// and returns a <see cref="Strada.Core.SubscriptionToken"/> that, when disposed,
        /// removes exactly this handler.
        /// </summary>
        public Strada.Core.SubscriptionToken Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct
        {
            if (_disposed) ThrowDisposed();
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            var id = EventTypeId<TEvent>.Id;
            EventChannel<TEvent> channel;

            lock (_lock)
            {
                EnsureCapacity(ref _eventChannels, id);
                channel = _eventChannels[id] as EventChannel<TEvent>;
                if (channel == null)
                {
                    channel = new EventChannel<TEvent>();
                    _eventChannels[id] = channel;
                }
            }

            channel.Subscribe(handler);
            return new Strada.Core.SubscriptionToken(() =>
            {
                if (_disposed) return;
                var channels = Volatile.Read(ref _eventChannels);
                if (id >= channels.Length) return;
                (channels[id] as EventChannel<TEvent>)?.Unsubscribe(handler);
            });
        }

        public int GetSubscriberCount<TEvent>() where TEvent : struct
        {
            if (_disposed) return 0;

            var id = EventTypeId<TEvent>.Id;
            var channels = Volatile.Read(ref _eventChannels);
            if (id >= channels.Length) return 0;

            var channel = channels[id] as EventChannel<TEvent>;
            return channel?.Count ?? 0;
        }

        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_signalHandlers, 0, _signalHandlers.Length);
                Array.Clear(_queryHandlers, 0, _queryHandlers.Length);
                Array.Clear(_eventChannels, 0, _eventChannels.Length);
                Array.Clear(_asyncSignalHandlers, 0, _asyncSignalHandlers.Length);
                Array.Clear(_asyncQueryHandlers, 0, _asyncQueryHandlers.Length);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }

        public async ValueTask SendAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default) where TSignal : struct
        {
            if (_disposed) ThrowDisposed();

            var id = AsyncSignalTypeId<TSignal>.Id;
            var handlers = Volatile.Read(ref _asyncSignalHandlers);
            if (id < handlers.Length && handlers[id] != null)
            {
                await ((Func<TSignal, CancellationToken, ValueTask>)handlers[id])(signal, cancellationToken);
                return;
            }

            ThrowHandlerNotFoundException<TSignal>("async signal");
        }

        public void RegisterAsyncSignalHandler<TSignal>(IAsyncSignalHandler<TSignal> handler) where TSignal : struct
        {
            RegisterAsyncSignalHandler<TSignal>((signal, ct) => handler.HandleAsync(signal, ct));
        }

        public void RegisterAsyncSignalHandler<TSignal>(Func<TSignal, CancellationToken, ValueTask> handler) where TSignal : struct
        {
            if (_disposed) ThrowDisposed();

            lock (_lock)
            {
                var id = AsyncSignalTypeId<TSignal>.Id;
                EnsureCapacity(ref _asyncSignalHandlers, id);
                Volatile.Write(ref _asyncSignalHandlers[id], handler);
            }
        }

        public async ValueTask<TResult> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
            where TQuery : struct, IAsyncQuery<TResult>
        {
            if (_disposed) ThrowDisposed();

            var id = AsyncQueryTypeId<TQuery>.Id;
            var handlers = Volatile.Read(ref _asyncQueryHandlers);
            if (id < handlers.Length && handlers[id] != null)
                return await ((Func<TQuery, CancellationToken, ValueTask<TResult>>)handlers[id])(query, cancellationToken);

            ThrowHandlerNotFoundException<TQuery>("async query");
            return default;
        }

        public void RegisterAsyncQueryHandler<TQuery, TResult>(IAsyncQueryHandler<TQuery, TResult> handler)
            where TQuery : struct, IAsyncQuery<TResult>
        {
            RegisterAsyncQueryHandler<TQuery, TResult>((query, ct) => handler.HandleAsync(query, ct));
        }

        public void RegisterAsyncQueryHandler<TQuery, TResult>(Func<TQuery, CancellationToken, ValueTask<TResult>> handler)
            where TQuery : struct, IAsyncQuery<TResult>
        {
            if (_disposed) ThrowDisposed();

            lock (_lock)
            {
                var id = AsyncQueryTypeId<TQuery>.Id;
                EnsureCapacity(ref _asyncQueryHandlers, id);
                Volatile.Write(ref _asyncQueryHandlers[id], handler);
            }
        }

        // Static type-id allocators. Each unique closed generic T allocates one id per kind.
        // Practically bounded by the number of distinct signal/query/event types in the program,
        // but we still trip an explicit overflow rather than wrapping into negative ids.
        private static int AllocateAndCheck(ref int counter, string kind)
        {
            var id = Interlocked.Increment(ref counter);
            if (id < 0)
                throw new InvalidOperationException(
                    $"EventBus {kind} type-id counter overflowed int.MaxValue.");
            return id;
        }

        private static class SignalTypeId<T>
        {
            public static readonly int Id = AllocateAndCheck(ref _nextSignalTypeId, "signal");
        }

        private static class QueryTypeId<T>
        {
            public static readonly int Id = AllocateAndCheck(ref _nextQueryTypeId, "query");
        }

        private static class EventTypeId<T>
        {
            public static readonly int Id = AllocateAndCheck(ref _nextEventTypeId, "event");
        }

        private static class AsyncSignalTypeId<T>
        {
            public static readonly int Id = AllocateAndCheck(ref _nextAsyncSignalTypeId, "async-signal");
        }

        private static class AsyncQueryTypeId<T>
        {
            public static readonly int Id = AllocateAndCheck(ref _nextAsyncQueryTypeId, "async-query");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureCapacity(ref object[] array, int id)
        {
            if (id < array.Length) return;

            var newSize = array.Length;
            while (newSize <= id)
                newSize *= 2;

            var newArray = new object[newSize];
            Array.Copy(array, newArray, array.Length);
            Volatile.Write(ref array, newArray);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowHandlerNotFoundException<T>(string type) =>
            throw new InvalidOperationException($"No {type} handler registered for '{typeof(T).Name}'");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowDisposed() =>
            throw new ObjectDisposedException(nameof(EventBus));

        /// <summary>
        /// A snapshot of the subscriber list: the array plus the number of entries in it that
        /// are live. Publishing reads one reference and iterates <see cref="Count"/>.
        /// </summary>
        /// <remarks>
        /// Splitting the count from the array length is what makes amortised growth safe.
        /// Subscribing writes the new handler into spare capacity at index Count and only
        /// then publishes a snapshot with Count+1, so a publisher still holding the previous
        /// snapshot iterates the old Count and never observes the slot being written. Growth
        /// therefore copies only when capacity runs out — building N subscribers is amortised
        /// O(N) instead of the O(N^2) that a copy-per-subscribe produced.
        /// </remarks>
        private sealed class HandlerSnapshot<T>
        {
            public static readonly HandlerSnapshot<T> Empty = new(Array.Empty<Action<T>>(), 0);

            public readonly Action<T>[] Handlers;
            public readonly int Count;

            public HandlerSnapshot(Action<T>[] handlers, int count)
            {
                Handlers = handlers;
                Count = count;
            }
        }

        private sealed class EventChannel<T>
        {
            private HandlerSnapshot<T> _snapshot = HandlerSnapshot<T>.Empty;
            private readonly object _lock = new object();

            public int Count => Volatile.Read(ref _snapshot).Count;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Publish(ref T message)
            {
                // One volatile read yields a consistent (array, count) pair; the snapshot is
                // never mutated below its own Count once published, so no lock is needed.
                var snapshot = Volatile.Read(ref _snapshot);
                var handlers = snapshot.Handlers;
                int count = snapshot.Count;

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        handlers[i](message);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Exception in event handler: {ex}");
                    }
                }
            }

            public void Subscribe(Action<T> handler)
            {
                lock (_lock)
                {
                    var current = _snapshot;
                    var handlers = current.Handlers;
                    int count = current.Count;

                    if (count == handlers.Length)
                    {
                        // Out of capacity: grow geometrically rather than by one.
                        var grown = new Action<T>[count == 0 ? 4 : count * 2];
                        Array.Copy(handlers, grown, count);
                        handlers = grown;
                    }

                    // Written before the new snapshot is published, and beyond the Count that
                    // any concurrent publisher can currently see.
                    handlers[count] = handler;
                    Volatile.Write(ref _snapshot, new HandlerSnapshot<T>(handlers, count + 1));
                }
            }

            public void Unsubscribe(Action<T> handler)
            {
                lock (_lock)
                {
                    var current = _snapshot;
                    var handlers = current.Handlers;
                    int count = current.Count;

                    int index = Array.IndexOf(handlers, handler, 0, count);
                    if (index < 0) return;

                    // Removal must copy: shifting in place would be visible to a publisher
                    // already iterating the current snapshot.
                    var reduced = new Action<T>[count - 1];
                    if (index > 0)
                        Array.Copy(handlers, 0, reduced, 0, index);
                    if (index < count - 1)
                        Array.Copy(handlers, index + 1, reduced, index, count - index - 1);

                    Volatile.Write(ref _snapshot, new HandlerSnapshot<T>(reduced, count - 1));
                }
            }
        }

        private sealed class DelegateQueryHandler<TQuery, TResult> : IQueryHandler<TQuery, TResult>
            where TQuery : struct, IQuery<TResult>
        {
            private readonly Func<TQuery, TResult> _handler;

            public DelegateQueryHandler(Func<TQuery, TResult> handler)
            {
                _handler = handler;
            }

            public TResult Handle(ref TQuery query) => _handler(query);
        }
    }
}
