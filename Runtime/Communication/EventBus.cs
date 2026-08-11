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
        Strada.Core.SubscriptionToken RegisterAsyncSignalHandler<TSignal>(IAsyncSignalHandler<TSignal> handler) where TSignal : struct;
        Strada.Core.SubscriptionToken RegisterAsyncSignalHandler<TSignal>(Func<TSignal, CancellationToken, ValueTask> handler) where TSignal : struct;
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
        Strada.Core.SubscriptionToken RegisterAsyncQueryHandler<TQuery, TResult>(IAsyncQueryHandler<TQuery, TResult> handler) where TQuery : struct, IAsyncQuery<TResult>;
        Strada.Core.SubscriptionToken RegisterAsyncQueryHandler<TQuery, TResult>(Func<TQuery, CancellationToken, ValueTask<TResult>> handler) where TQuery : struct, IAsyncQuery<TResult>;
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

        // Written by Dispose on one thread and read by every dispatch on others; without
        // volatile the JIT may hoist the read out of a loop and keep dispatching into a
        // disposed bus. Matches Container._disposed.
        private volatile bool _disposed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Send<TSignal>(ref TSignal signal) where TSignal : struct
        {
            if (_disposed) ThrowDisposed();

            var id = SignalTypeId<TSignal>.Id;
            var handlers = Volatile.Read(ref _signalHandlers);
            // The slot must be loaded exactly once: a concurrent token disposal or Clear()
            // nulls the element of this very array, so re-reading it after the null test
            // could hand us a null delegate to invoke.
            var handler = id < handlers.Length ? handlers[id] as Action<TSignal> : null;
            if (handler != null)
            {
                handler(signal);
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
            // Single load of the slot — see Send for why a second read is unsafe.
            var handler = id < handlers.Length ? handlers[id] as IQueryHandler<TQuery, TResult> : null;
            if (handler != null)
                return handler.Handle(ref query);

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

        /// <summary>
        /// Registers a delegate query handler and returns a <see cref="Strada.Core.SubscriptionToken"/>
        /// whose disposal removes it.
        /// </summary>
        /// <remarks>
        /// Note the aliasing difference from the <see cref="IQueryHandler{TQuery,TResult}"/>
        /// overload: <paramref name="handler"/> receives a <i>copy</i> of the query, so writes
        /// to it are not visible to a caller of <c>Query(ref TQuery)</c>, whereas an
        /// <c>IQueryHandler</c> receives the caller's struct by reference and can write through
        /// to it.
        /// </remarks>
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
                    // Release store: Publish and GetSubscriberCount read this slot without
                    // taking _lock, so on a weakly ordered CPU (ARM64 — Android/iOS/Switch)
                    // a plain store could publish the reference ahead of the channel's own
                    // field initialisers.
                    Volatile.Write(ref _eventChannels[id], channel);
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
            // The check and the set have to be atomic together, otherwise two threads
            // disposing concurrently both fall through into Clear().
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            Clear();
        }

        public async ValueTask SendAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default) where TSignal : struct
        {
            if (_disposed) ThrowDisposed();

            var id = AsyncSignalTypeId<TSignal>.Id;
            var handlers = Volatile.Read(ref _asyncSignalHandlers);
            // Single load of the slot — see Send for why a second read is unsafe.
            var handler = id < handlers.Length
                ? handlers[id] as Func<TSignal, CancellationToken, ValueTask>
                : null;
            if (handler != null)
            {
                await handler(signal, cancellationToken);
                return;
            }

            ThrowHandlerNotFoundException<TSignal>("async signal");
        }

        /// <summary>
        /// Registers an async signal handler and returns a <see cref="Strada.Core.SubscriptionToken"/>
        /// whose disposal removes it from the single-handler slot for <typeparamref name="TSignal"/>.
        /// </summary>
        public Strada.Core.SubscriptionToken RegisterAsyncSignalHandler<TSignal>(IAsyncSignalHandler<TSignal> handler) where TSignal : struct
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            // What lands in the slot is this wrapper, not the caller's handler, so the token
            // has to be built around the local — otherwise its ReferenceEquals guard could
            // never match and the handler could never be unregistered.
            Func<TSignal, CancellationToken, ValueTask> wrapper = (signal, ct) => handler.HandleAsync(signal, ct);
            return RegisterAsyncSignalHandler<TSignal>(wrapper);
        }

        /// <summary>
        /// Registers an async signal handler and returns a <see cref="Strada.Core.SubscriptionToken"/>
        /// whose disposal removes <paramref name="handler"/> from the single-handler slot for
        /// <typeparamref name="TSignal"/>. Disposal is race-safe: if a later registration has
        /// already replaced the slot, the token does not clear it.
        /// </summary>
        public Strada.Core.SubscriptionToken RegisterAsyncSignalHandler<TSignal>(Func<TSignal, CancellationToken, ValueTask> handler) where TSignal : struct
        {
            if (_disposed) ThrowDisposed();
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (_lock)
            {
                var id = AsyncSignalTypeId<TSignal>.Id;
                EnsureCapacity(ref _asyncSignalHandlers, id);
                Volatile.Write(ref _asyncSignalHandlers[id], handler);
            }

            return new Strada.Core.SubscriptionToken(() =>
            {
                if (_disposed) return;
                lock (_lock)
                {
                    var id = AsyncSignalTypeId<TSignal>.Id;
                    var arr = _asyncSignalHandlers;
                    if (id < arr.Length && ReferenceEquals(arr[id], handler))
                        Volatile.Write(ref arr[id], null);
                }
            });
        }

        public async ValueTask<TResult> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
            where TQuery : struct, IAsyncQuery<TResult>
        {
            if (_disposed) ThrowDisposed();

            var id = AsyncQueryTypeId<TQuery>.Id;
            var handlers = Volatile.Read(ref _asyncQueryHandlers);
            // Single load of the slot — see Send for why a second read is unsafe.
            var handler = id < handlers.Length
                ? handlers[id] as Func<TQuery, CancellationToken, ValueTask<TResult>>
                : null;
            if (handler != null)
                return await handler(query, cancellationToken);

            ThrowHandlerNotFoundException<TQuery>("async query");
            return default;
        }

        /// <summary>
        /// Registers an async query handler and returns a <see cref="Strada.Core.SubscriptionToken"/>
        /// whose disposal removes it from the single-handler slot for <typeparamref name="TQuery"/>.
        /// </summary>
        public Strada.Core.SubscriptionToken RegisterAsyncQueryHandler<TQuery, TResult>(IAsyncQueryHandler<TQuery, TResult> handler)
            where TQuery : struct, IAsyncQuery<TResult>
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            // The wrapper, not the caller's handler, is what the token must compare against.
            Func<TQuery, CancellationToken, ValueTask<TResult>> wrapper = (query, ct) => handler.HandleAsync(query, ct);
            return RegisterAsyncQueryHandler<TQuery, TResult>(wrapper);
        }

        /// <summary>
        /// Registers an async query handler and returns a <see cref="Strada.Core.SubscriptionToken"/>
        /// whose disposal removes <paramref name="handler"/> from the single-handler slot for
        /// <typeparamref name="TQuery"/>. Disposal is race-safe: if a later registration has
        /// already replaced the slot, the token does not clear it.
        /// </summary>
        public Strada.Core.SubscriptionToken RegisterAsyncQueryHandler<TQuery, TResult>(Func<TQuery, CancellationToken, ValueTask<TResult>> handler)
            where TQuery : struct, IAsyncQuery<TResult>
        {
            if (_disposed) ThrowDisposed();
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (_lock)
            {
                var id = AsyncQueryTypeId<TQuery>.Id;
                EnsureCapacity(ref _asyncQueryHandlers, id);
                Volatile.Write(ref _asyncQueryHandlers[id], handler);
            }

            return new Strada.Core.SubscriptionToken(() =>
            {
                if (_disposed) return;
                lock (_lock)
                {
                    var id = AsyncQueryTypeId<TQuery>.Id;
                    var arr = _asyncQueryHandlers;
                    if (id < arr.Length && ReferenceEquals(arr[id], handler))
                        Volatile.Write(ref arr[id], null);
                }
            });
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
            private const int MaxLoggedFailures = 3;

            private HandlerSnapshot<T> _snapshot = HandlerSnapshot<T>.Empty;
            private readonly object _lock = new object();
            private int _consecutiveFailures;

            public int Count => Volatile.Read(ref _snapshot).Count;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Publish(ref T message)
            {
                // This wrapper stays free of exception handling on purpose: RyuJIT refuses to
                // inline any method containing an EH clause regardless of AggressiveInlining,
                // so the try/catch lives in PublishCore and publishing to a channel with no
                // subscribers costs nothing but the snapshot read.
                var snapshot = Volatile.Read(ref _snapshot);
                if (snapshot.Count == 0) return;

                PublishCore(snapshot, ref message);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void PublishCore(HandlerSnapshot<T> snapshot, ref T message)
            {
                // The snapshot is never mutated below its own Count once published, so the
                // single volatile read above yields a consistent (array, count) pair and no
                // lock is needed here.
                var handlers = snapshot.Handlers;
                int count = snapshot.Count;
                bool faulted = false;

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        handlers[i](message);
                    }
                    catch (Exception ex)
                    {
                        faulted = true;
                        LogHandlerException(ex);
                    }
                }

                // A clean publish re-arms the log budget, so an intermittent failure keeps
                // being reported while a permanently broken handler goes quiet.
                if (!faulted && _consecutiveFailures != 0)
                    Volatile.Write(ref _consecutiveFailures, 0);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void LogHandlerException(Exception ex)
            {
                // Interpolating the exception itself calls Exception.ToString(), which
                // materialises the whole stack trace — hundreds of bytes per publish for a
                // handler that throws every frame. Pay for that once, then log only type and
                // message, then stop.
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures == 1)
                    Debug.LogError($"[EventBus] Handler for '{typeof(T).Name}' threw: {ex}");
                else if (failures <= MaxLoggedFailures)
                    Debug.LogError($"[EventBus] Handler for '{typeof(T).Name}' threw: {ex.GetType().Name}: {ex.Message}");
                else if (failures == MaxLoggedFailures + 1)
                    Debug.LogError($"[EventBus] Handler for '{typeof(T).Name}' keeps throwing; further exceptions suppressed until a publish succeeds.");
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
