using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Strada.Core.Communication
{
    /// <summary>
    /// Composable, async-capable signal sequence builder.
    /// Allows chaining multiple signals, including nested sequences,
    /// with optional multi-bus targeting.
    /// </summary>
    public sealed class SignalSequence : IDisposable
    {
        private readonly List<ISequenceEntry> _entries;
        private IEventBus _defaultBus;
        private bool _disposed;

        public SignalSequence()
        {
            _entries = new List<ISequenceEntry>(8);
        }

        public SignalSequence(IEventBus defaultBus) : this()
        {
            _defaultBus = defaultBus;
        }

        /// <summary>
        /// Sets the default EventBus for signals in this sequence.
        /// </summary>
        public SignalSequence WithBus(IEventBus bus)
        {
            _defaultBus = bus;
            return this;
        }

        /// <summary>
        /// Adds a signal to the sequence using the default bus.
        /// </summary>
        public SignalSequence Then<TSignal>(TSignal signal) where TSignal : struct
        {
            _entries.Add(new SignalEntry<TSignal>(signal, null));
            return this;
        }

        /// <summary>
        /// Adds a signal to the sequence targeting a specific bus.
        /// </summary>
        public SignalSequence Then<TSignal>(TSignal signal, IEventBus targetBus) where TSignal : struct
        {
            _entries.Add(new SignalEntry<TSignal>(signal, targetBus));
            return this;
        }

        /// <summary>
        /// Includes another sequence in this sequence.
        /// The included sequence will be executed at this point in the chain.
        /// </summary>
        public SignalSequence Include(SignalSequence other)
        {
            if (other == null || ReferenceEquals(other, this)) return this;

            // An indirect cycle (A includes B, B includes A) recurses through
            // SequenceEntry.Execute with no depth limit and takes the process down with an
            // uncatchable StackOverflowException, so refuse the edge that would close it.
            if (other.Reaches(this))
            {
                UnityEngine.Debug.LogWarning(
                    "[SignalSequence] Include ignored: the included sequence already reaches this one, which would form a cycle.");
                return this;
            }

            _entries.Add(new SequenceEntry(other));
            return this;
        }

        /// <summary>
        /// True when <paramref name="target"/> is reachable from this sequence by following
        /// Include edges. Used to keep the include graph acyclic.
        /// </summary>
        private bool Reaches(SignalSequence target)
        {
            HashSet<SignalSequence> visited = null;
            return ReachesCore(target, ref visited);
        }

        private bool ReachesCore(SignalSequence target, ref HashSet<SignalSequence> visited)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!(_entries[i] is SequenceEntry nested)) continue;

                var sequence = nested.Sequence;
                if (sequence == null) continue;
                if (ReferenceEquals(sequence, target)) return true;

                // A sub-sequence can be included from several places; without the visited set
                // the walk is exponential in the number of Include edges. Allocated lazily so
                // sequences with no nested entries pay nothing.
                visited ??= new HashSet<SignalSequence>();
                if (!visited.Add(sequence)) continue;
                if (sequence.ReachesCore(target, ref visited)) return true;
            }

            return false;
        }

        /// <summary>
        /// Adds a synchronous action to the sequence.
        /// </summary>
        public SignalSequence Then(Action action)
        {
            if (action != null)
            {
                _entries.Add(new ActionEntry(action));
            }
            return this;
        }

        /// <summary>
        /// Adds an async action to the sequence.
        /// </summary>
        /// <remarks>
        /// Only <see cref="ExecuteAsync(IEventBus, CancellationToken)"/> awaits the action.
        /// The synchronous <see cref="Execute()"/> cannot block without risking a main-thread
        /// deadlock, so it starts the action and continues with the following entries; an
        /// action that does not complete inline therefore runs out of order there (logged once).
        /// </remarks>
        public SignalSequence ThenAsync(Func<CancellationToken, ValueTask> asyncAction)
        {
            if (asyncAction != null)
            {
                _entries.Add(new AsyncActionEntry(asyncAction));
            }
            return this;
        }

        /// <summary>
        /// Adds a conditional signal that only executes if the predicate is true.
        /// </summary>
        public SignalSequence ThenIf<TSignal>(bool condition, TSignal signal) where TSignal : struct
        {
            if (condition)
            {
                _entries.Add(new SignalEntry<TSignal>(signal, null));
            }
            return this;
        }

        /// <summary>
        /// Adds a conditional signal using a predicate evaluated at execution time.
        /// </summary>
        public SignalSequence ThenIf<TSignal>(Func<bool> predicate, TSignal signal) where TSignal : struct
        {
            _entries.Add(new ConditionalSignalEntry<TSignal>(signal, predicate, null));
            return this;
        }

        /// <summary>
        /// Executes all signals in the sequence synchronously.
        /// </summary>
        public void Execute()
        {
            Execute(_defaultBus);
        }

        /// <summary>
        /// Executes all signals in the sequence synchronously with a specific default bus.
        /// </summary>
        public void Execute(IEventBus defaultBus)
        {
            if (_disposed) return;

            var bus = defaultBus ?? _defaultBus;

            // Indexed rather than foreach: an entry is free to call Then/Clear/Dispose on this
            // same sequence, and List<T>'s enumerator would throw InvalidOperationException on
            // the next MoveNext. Re-reading _disposed per entry also stops the chain cleanly
            // when an entry disposes the sequence mid-execution.
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_disposed) return;
                _entries[i].Execute(bus);
            }
        }

        /// <summary>
        /// Executes all signals in the sequence asynchronously.
        /// </summary>
        public ValueTask ExecuteAsync(CancellationToken ct = default)
        {
            return ExecuteAsync(_defaultBus, ct);
        }

        /// <summary>
        /// Executes all signals in the sequence asynchronously with a specific default bus.
        /// </summary>
        public async ValueTask ExecuteAsync(IEventBus defaultBus, CancellationToken ct = default)
        {
            if (_disposed) return;

            var bus = defaultBus ?? _defaultBus;

            // See Execute: the enumerator would additionally be held across every await here,
            // so any mutation of the sequence between continuations would throw.
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_disposed) return;
                ct.ThrowIfCancellationRequested();
                await _entries[i].ExecuteAsync(bus, ct);
            }
        }

        /// <summary>
        /// Clears all entries from the sequence for reuse.
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
        }

        /// <summary>
        /// Gets the number of entries in the sequence.
        /// </summary>
        public int Count => _entries.Count;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Clear();
        }

        #region Entry Interfaces and Implementations

        private interface ISequenceEntry
        {
            void Execute(IEventBus defaultBus);
            ValueTask ExecuteAsync(IEventBus defaultBus, CancellationToken ct);
        }

        // Dropping the signal silently is the one misconfiguration in this class that produces
        // no diagnostic at all — executing against a bus that has no handler registered already
        // throws from EventBus.Send, so a missing bus reports the same way.
        private static void ThrowNoBus<TSignal>() where TSignal : struct =>
            throw new InvalidOperationException(
                $"SignalSequence entry for '{typeof(TSignal).Name}' has no target bus. Supply one via " +
                "new SignalSequence(bus), WithBus(bus), Then(signal, bus) or Execute(bus).");

        private readonly struct SignalEntry<TSignal> : ISequenceEntry where TSignal : struct
        {
            private readonly TSignal _signal;
            private readonly IEventBus _targetBus;

            public SignalEntry(TSignal signal, IEventBus targetBus)
            {
                _signal = signal;
                _targetBus = targetBus;
            }

            public void Execute(IEventBus defaultBus)
            {
                var bus = _targetBus ?? defaultBus;
                if (bus == null) ThrowNoBus<TSignal>();

                var signal = _signal;
                bus.Send(ref signal);
            }

            public ValueTask ExecuteAsync(IEventBus defaultBus, CancellationToken ct)
            {
                var bus = _targetBus ?? defaultBus;
                if (bus == null) ThrowNoBus<TSignal>();

                return bus.SendAsync(_signal, ct);
            }
        }

        private readonly struct ConditionalSignalEntry<TSignal> : ISequenceEntry where TSignal : struct
        {
            private readonly TSignal _signal;
            private readonly Func<bool> _predicate;
            private readonly IEventBus _targetBus;

            public ConditionalSignalEntry(TSignal signal, Func<bool> predicate, IEventBus targetBus)
            {
                _signal = signal;
                _predicate = predicate;
                _targetBus = targetBus;
            }

            public void Execute(IEventBus defaultBus)
            {
                if (_predicate == null || !_predicate()) return;

                var bus = _targetBus ?? defaultBus;
                if (bus == null) ThrowNoBus<TSignal>();

                var signal = _signal;
                bus.Send(ref signal);
            }

            public ValueTask ExecuteAsync(IEventBus defaultBus, CancellationToken ct)
            {
                if (_predicate == null || !_predicate()) return default;

                var bus = _targetBus ?? defaultBus;
                if (bus == null) ThrowNoBus<TSignal>();

                return bus.SendAsync(_signal, ct);
            }
        }

        private sealed class SequenceEntry : ISequenceEntry
        {
            private readonly SignalSequence _sequence;

            public SequenceEntry(SignalSequence sequence)
            {
                _sequence = sequence;
            }

            public SignalSequence Sequence => _sequence;

            public void Execute(IEventBus defaultBus)
            {
                _sequence?.Execute(defaultBus);
            }

            public ValueTask ExecuteAsync(IEventBus defaultBus, CancellationToken ct)
            {
                return _sequence?.ExecuteAsync(defaultBus, ct) ?? default;
            }
        }

        private sealed class ActionEntry : ISequenceEntry
        {
            private readonly Action _action;

            public ActionEntry(Action action)
            {
                _action = action;
            }

            public void Execute(IEventBus defaultBus)
            {
                _action?.Invoke();
            }

            public ValueTask ExecuteAsync(IEventBus defaultBus, CancellationToken ct)
            {
                _action?.Invoke();
                return default;
            }
        }

        private sealed class AsyncActionEntry : ISequenceEntry
        {
            private readonly Func<CancellationToken, ValueTask> _asyncAction;
            private bool _warnedNotAwaited;

            public AsyncActionEntry(Func<CancellationToken, ValueTask> asyncAction)
            {
                _asyncAction = asyncAction;
            }

            public void Execute(IEventBus defaultBus)
            {
                if (_asyncAction == null) return;

                var task = _asyncAction.Invoke(CancellationToken.None);
                if (!task.IsCompleted)
                {
                    if (!_warnedNotAwaited)
                    {
                        // The synchronous path cannot wait without risking a main-thread
                        // deadlock, so the entries after this one run before it finishes.
                        // Say so once per entry rather than every frame.
                        _warnedNotAwaited = true;
                        UnityEngine.Debug.LogWarning(
                            "[SignalSequence] An async entry did not complete synchronously during Execute(); " +
                            "later entries run without waiting for it. Use ExecuteAsync to preserve ordering.");
                    }

                    // AsTask consumes the ValueTask, which is what a pooled IValueTaskSource
                    // needs in order to release its token and be recycled.
                    task.AsTask().ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            UnityEngine.Debug.LogError($"[SignalSequence] Async action failed: {t.Exception?.InnerException?.Message}");
                    }, TaskContinuationOptions.OnlyOnFaulted);
                    return;
                }

                // Completed inline. The result still has to be read exactly once: an
                // unconsumed ValueTask leaves a pooled source un-advanced and never returned
                // to its pool, and it is also how a synchronous fault surfaces.
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[SignalSequence] Async action failed: {ex.Message}");
                }
            }

            public ValueTask ExecuteAsync(IEventBus defaultBus, CancellationToken ct)
            {
                return _asyncAction?.Invoke(ct) ?? default;
            }
        }

        #endregion
    }

    /// <summary>
    /// Registry for named signal sequences that can reference each other.
    /// </summary>
    public sealed class SignalSequenceRegistry : IDisposable
    {
        private readonly Dictionary<string, SignalSequence> _sequences;
        private readonly IEventBus _defaultBus;
        private bool _disposed;

        public SignalSequenceRegistry(IEventBus defaultBus = null)
        {
            _sequences = new Dictionary<string, SignalSequence>(16);
            _defaultBus = defaultBus;
        }

        /// <summary>
        /// Registers a named sequence.
        /// </summary>
        public void Register(string name, SignalSequence sequence)
        {
            if (string.IsNullOrEmpty(name))
            {
                // Dropping the registration silently used to leave Create() returning a
                // sequence that Get/Contains would never find.
                UnityEngine.Debug.LogWarning("[SignalSequenceRegistry] Register ignored: the sequence name must be non-empty.");
                return;
            }

            _sequences[name] = sequence;
        }

        /// <summary>
        /// Creates and registers a named sequence with a builder action.
        /// </summary>
        public SignalSequence Create(string name, Action<SignalSequence> builder)
        {
            var sequence = new SignalSequence(_defaultBus);
            builder?.Invoke(sequence);
            Register(name, sequence);
            return sequence;
        }

        /// <summary>
        /// Gets a named sequence.
        /// </summary>
        public SignalSequence Get(string name)
        {
            // Register accepts (and ignores) a null or empty name, so the read side has to
            // tolerate the same input instead of surfacing Dictionary's ArgumentNullException.
            if (string.IsNullOrEmpty(name)) return null;
            return _sequences.TryGetValue(name, out var sequence) ? sequence : null;
        }

        /// <summary>
        /// Checks if a named sequence exists.
        /// </summary>
        public bool Contains(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return _sequences.ContainsKey(name);
        }

        /// <summary>
        /// Executes a named sequence synchronously.
        /// </summary>
        public void Execute(string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            if (_sequences.TryGetValue(name, out var sequence))
            {
                sequence.Execute(_defaultBus);
            }
        }

        /// <summary>
        /// Executes a named sequence asynchronously.
        /// </summary>
        public ValueTask ExecuteAsync(string name, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(name)) return default;

            if (_sequences.TryGetValue(name, out var sequence))
            {
                return sequence.ExecuteAsync(_defaultBus, ct);
            }
            return default;
        }

        /// <summary>
        /// Removes a named sequence.
        /// </summary>
        public bool Remove(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            if (_sequences.TryGetValue(name, out var sequence))
            {
                sequence.Dispose();
                return _sequences.Remove(name);
            }
            return false;
        }

        /// <summary>
        /// Clears all registered sequences.
        /// </summary>
        public void Clear()
        {
            foreach (var sequence in _sequences.Values)
            {
                sequence.Dispose();
            }
            _sequences.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }
}
