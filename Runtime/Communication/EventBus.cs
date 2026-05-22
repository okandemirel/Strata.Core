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
        void RegisterSignalHandler<TSignal>(Action<TSignal> handler) where TSignal : struct;
        void RegisterSignalHandler<TSignal>(ISignalHandler<TSignal> handler) where TSignal : struct;
        void UnregisterSignalHandler<TSignal>() where TSignal : struct;
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
        void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler) where TQuery : struct, IQuery<TResult>;
        void RegisterQueryHandler<TQuery, TResult>(Func<TQuery, TResult> handler) where TQuery : struct, IQuery<TResult>;
        void UnregisterQueryHandler<TQuery, TResult>() where TQuery : struct, IQuery<TResult>;
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
        void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
        void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : struct;
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

        public void RegisterSignalHandler<TSignal>(Action<TSignal> handler) where TSignal : struct
        {
            if (_disposed) ThrowDisposed();

            lock (_lock)
            {
                var id = SignalTypeId<TSignal>.Id;
                EnsureCapacity(ref _signalHandlers, id);
                if (_signalHandlers[id] != null)
                    Debug.LogWarning($"[EventBus] Signal handler for '{typeof(TSignal).Name}' is being replaced.");

                Volatile.Write(ref _signalHandlers[id], handler);
            }
        }

        public void RegisterSignalHandler<TSignal>(ISignalHandler<TSignal> handler) where TSignal : struct
        {
            RegisterSignalHandler<TSignal>(handler.Handle);
        }

        public void UnregisterSignalHandler<TSignal>() where TSignal : struct
        {
            if (_disposed) ThrowDisposed();

            lock (_lock)
            {
                var id = SignalTypeId<TSignal>.Id;
                var handlers = _signalHandlers;
                if (id < handlers.Length)
                    Volatile.Write(ref handlers[id], null);
            }
        }

        public bool HasSignalHandler<TSignal>() where TSignal : struct
        {
            if (_disposed) return false;

            var id = SignalTypeId<TSignal>.Id;
            var handlers = Volatile.Read(ref _signalHandlers);
            return id < handlers.Length && handlers[id] != null;
        }

        public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            if (_disposed) ThrowDisposed();

            lock (_lock)
            {
                var id = QueryTypeId<TQuery>.Id;
                EnsureCapacity(ref _queryHandlers, id);
                Volatile.Write(ref _queryHandlers[id], handler);
            }
        }

        public void RegisterQueryHandler<TQuery, TResult>(Func<TQuery, TResult> handler)
            where TQuery : struct, IQuery<TResult>
        {
            RegisterQueryHandler(new DelegateQueryHandler<TQuery, TResult>(handler));
        }

        public void UnregisterQueryHandler<TQuery, TResult>() where TQuery : struct, IQuery<TResult>
        {
            if (_disposed) ThrowDisposed();

            lock (_lock)
            {
                var id = QueryTypeId<TQuery>.Id;
                var handlers = _queryHandlers;
                if (id < handlers.Length)
                    Volatile.Write(ref handlers[id], null);
            }
        }

        public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : struct
        {
            if (_disposed) ThrowDisposed();

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
        }

        public void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : struct
        {
            if (_disposed) return;

            var id = EventTypeId<TEvent>.Id;
            var channels = Volatile.Read(ref _eventChannels);
            if (id >= channels.Length) return;

            (channels[id] as EventChannel<TEvent>)?.Unsubscribe(handler);
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

        private sealed class EventChannel<T>
        {
            private Action<T>[] _handlers = Array.Empty<Action<T>>();
            private readonly object _lock = new object();

            public int Count => Volatile.Read(ref _handlers).Length;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Publish(ref T message)
            {
                var handlers = Volatile.Read(ref _handlers);
                for (int i = 0; i < handlers.Length; i++)
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
                    var oldHandlers = _handlers;
                    var newHandlers = new Action<T>[oldHandlers.Length + 1];
                    Array.Copy(oldHandlers, newHandlers, oldHandlers.Length);
                    newHandlers[oldHandlers.Length] = handler;
                    Volatile.Write(ref _handlers, newHandlers);
                }
            }

            public void Unsubscribe(Action<T> handler)
            {
                lock (_lock)
                {
                    var oldHandlers = _handlers;
                    var index = Array.IndexOf(oldHandlers, handler);
                    if (index < 0) return;

                    var newHandlers = new Action<T>[oldHandlers.Length - 1];
                    if (index > 0)
                        Array.Copy(oldHandlers, 0, newHandlers, 0, index);

                    if (index < oldHandlers.Length - 1)
                        Array.Copy(oldHandlers, index + 1, newHandlers, index, oldHandlers.Length - index - 1);

                    Volatile.Write(ref _handlers, newHandlers);
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
