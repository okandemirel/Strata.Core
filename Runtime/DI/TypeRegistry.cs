using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Strada.Core.DI
{
    internal static class TypeRegistry
    {
        private const int MaxTypeCount = 8192;
        private static int _nextId;
        private static readonly ConcurrentDictionary<Type, int> _typeCache = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetId<T>() => TypeId<T>.Id;

        public static int GetId(Type type)
        {
            // Validated before anything dereferences `type`: the value-type guard below reads
            // type.IsValueType, so a null argument surfaced as NullReferenceException instead
            // of the ArgumentNullException every caller documents.
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            // IL2CPP shares generic code only for reference-type arguments, so MakeGenericType on a
            // value type that was never statically instantiated throws ExecutionEngineException.
            // Every public Resolve<T>/IsRegistered<T> is constrained to class; only the Type-taking
            // overloads can get here with a struct. Hand back an id above any possible _maxTypeId so
            // callers take their normal "not registered" path instead of crashing the player.
            if (type.IsValueType)
                return int.MaxValue;

            return _typeCache.GetOrAdd(type, static t =>
                (int)typeof(TypeId<>)
                    .MakeGenericType(t)
                    .GetField("Id")
                    .GetValue(null));
        }

        internal static int AllocateId()
        {
            // Reserve with a CAS rather than an unconditional Increment: a plain Increment advances
            // _nextId even when the bound check then fails, so once the limit is hit the counter runs
            // away and no id can ever be handed out again for the rest of the process.
            while (true)
            {
                int current = Volatile.Read(ref _nextId);
                if (current >= MaxTypeCount)
                    throw new InvalidOperationException(
                        $"Maximum number of registered types ({MaxTypeCount}) exceeded");

                if (Interlocked.CompareExchange(ref _nextId, current + 1, current) == current)
                    return current + 1;
            }
        }

        private static class TypeId<T>
        {
            public static readonly int Id;

            static TypeId()
            {
                Id = AllocateId();
                _typeCache[typeof(T)] = Id;
            }
        }
    }
}
