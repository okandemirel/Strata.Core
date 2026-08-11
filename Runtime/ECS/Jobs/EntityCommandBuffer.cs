using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Strada.Core.ECS.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Strada.Core.ECS.Jobs
{
    public enum EntityOperation : byte
    {
        CreateEntity,
        DestroyEntity,
        AddComponent,
        RemoveComponent,
        SetComponent
    }

    /// <summary>
    /// A deferred command buffer for recording ECS structural changes (create/destroy entity,
    /// add/remove/set component) outside of safe iteration contexts.
    /// </summary>
    /// <remarks>
    /// <para><b>Thread safety:</b> <see cref="EntityCommandBuffer"/> is <b>not thread-safe</b>.
    /// Each instance is intended to be used by a single thread at a time. To record commands from
    /// multiple worker threads concurrently, create one buffer per worker and merge them at
    /// playback time, or wait for the planned <c>AsParallelWriter()</c> API. Concurrent writes
    /// from multiple threads to the same buffer will corrupt the internal command stream and
    /// cause undefined behavior during playback.</para>
    /// <para><b>Burst:</b> neither half of this type is Burst-compatible, and the type-level
    /// <c>[BurstCompile]</c> it used to carry produced no code — Burst only compiles job types
    /// and static methods explicitly marked inside a <c>[BurstCompile]</c> type. The record path
    /// calls <see cref="ComponentPlayback.EnsureHandler{T}"/>, which touches a managed dictionary
    /// and a managed generic static; playback additionally does interface dispatch and throws.
    /// Record and play back from managed code.</para>
    /// <para><b>Playback:</b> <see cref="Playback"/> must run on the main thread; it mutates
    /// <see cref="Strada.Core.ECS.Core.EntityManager"/> state which is itself not thread-safe.</para>
    /// <para><b>Disposal:</b> The buffer owns native memory and must be disposed. Allocator
    /// determines lifetime (e.g. <see cref="Unity.Collections.Allocator.TempJob"/> requires
    /// disposal within 4 frames).</para>
    /// </remarks>
    public unsafe struct EntityCommandBuffer : IDisposable
    {
        private NativeList<byte> _commandStream;
        private NativeList<Entity> _createdEntities;
        private int _createEntityCount;
        private Allocator _allocator;
        private bool _isCreated;

        public int CommandCount { get; private set; }
        public int CreatedEntityCount => _createEntityCount;
        public bool IsCreated => _isCreated;

        public EntityCommandBuffer(Allocator allocator, int initialCapacity = 1024)
        {
            _allocator = allocator;
            _commandStream = new NativeList<byte>(initialCapacity, allocator);
            _createdEntities = new NativeList<Entity>(64, allocator);
            _createEntityCount = 0;
            _isCreated = true;
            CommandCount = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CreateEntity()
        {
            WriteCommand(EntityOperation.CreateEntity);
            return _createEntityCount++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DestroyEntity(Entity entity)
        {
            WriteCommand(EntityOperation.DestroyEntity);
            WriteEntity(entity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddComponent<T>(Entity entity, T component) where T : unmanaged, IComponent
        {
            // Registers the playback handler while T is still known. Playback only sees a
            // type hash, so without this the command is silently discarded at replay.
            ComponentPlayback.EnsureHandler<T>();
            WriteCommand(EntityOperation.AddComponent);
            WriteEntity(entity);
            WriteTypeHash<T>();
            WriteComponent(component);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddComponent<T>(int deferredEntityIndex, T component) where T : unmanaged, IComponent
        {
            // Registers the playback handler while T is still known. Playback only sees a
            // type hash, so without this the command is silently discarded at replay.
            ComponentPlayback.EnsureHandler<T>();
            WriteCommand(EntityOperation.AddComponent);
            WriteDeferredEntity(deferredEntityIndex);
            WriteTypeHash<T>();
            WriteComponent(component);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveComponent<T>(Entity entity) where T : unmanaged, IComponent
        {
            // Registers the playback handler while T is still known. Playback only sees a
            // type hash, so without this the command is silently discarded at replay.
            ComponentPlayback.EnsureHandler<T>();
            WriteCommand(EntityOperation.RemoveComponent);
            WriteEntity(entity);
            WriteTypeHash<T>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponent<T>(Entity entity, T component) where T : unmanaged, IComponent
        {
            // Registers the playback handler while T is still known. Playback only sees a
            // type hash, so without this the command is silently discarded at replay.
            ComponentPlayback.EnsureHandler<T>();
            WriteCommand(EntityOperation.SetComponent);
            WriteEntity(entity);
            WriteTypeHash<T>();
            WriteComponent(component);
        }

        public void Playback(EntityManager entityManager)
        {
            if (_commandStream.Length == 0) return;

            _createdEntities.Clear();

            var reader = new CommandReader(_commandStream.AsArray());
            while (reader.HasRemaining)
            {
                var cmd = reader.ReadCommand();
                switch (cmd)
                {
                    case EntityOperation.CreateEntity:
                        // Created here, in recorded order, rather than all up front. Hoisting
                        // every create ahead of the stream meant a DestroyEntity recorded
                        // between two creates always executed after both, so an index freed
                        // inside a buffer could never be recycled by a later create in the same
                        // buffer — a create/destroy-balanced workload consumed twice the entity
                        // index space it needed. The deferred index handed to the recorder is
                        // the ordinal of its CreateEntity command, so appending in stream order
                        // keeps _createdEntities indexed exactly as recorded.
                        _createdEntities.Add(entityManager.CreateEntity());
                        break;
                    case EntityOperation.DestroyEntity:
                        PlaybackDestroyEntity(ref reader, entityManager);
                        break;
                    case EntityOperation.AddComponent:
                        PlaybackAddComponent(ref reader, entityManager);
                        break;
                    case EntityOperation.RemoveComponent:
                        PlaybackRemoveComponent(ref reader, entityManager);
                        break;
                    case EntityOperation.SetComponent:
                        PlaybackSetComponent(ref reader, entityManager);
                        break;
                }
            }
        }

        public void Clear()
        {
            _commandStream.Clear();
            _createdEntities.Clear();
            _createEntityCount = 0;
            CommandCount = 0;
        }

        public void Dispose()
        {
            if (!_isCreated) return;
            if (_commandStream.IsCreated) _commandStream.Dispose();
            if (_createdEntities.IsCreated) _createdEntities.Dispose();
            _isCreated = false;
        }

        private void WriteCommand(EntityOperation cmd)
        {
            _commandStream.Add((byte)cmd);
            CommandCount++;
        }

        private void WriteEntity(Entity entity)
        {
            WriteInt(entity.Index);
            WriteInt(entity.Version);
            WriteByte(0);
        }

        private void WriteDeferredEntity(int deferredIndex)
        {
            WriteInt(deferredIndex);
            WriteInt(0);
            WriteByte(1);
        }

        // Every field below is appended with a single AddRange memcpy rather than one Add per
        // byte. NativeList<T>.Add is not free: under ENABLE_UNITY_COLLECTIONS_CHECKS (the Editor
        // and every development build) it runs an AtomicSafetyHandle check and a capacity branch
        // per call, so recording one AddComponent<Position> cost 34 safety checks to move 34
        // bytes. AddRange pays that once.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteTypeHash<T>() where T : unmanaged
        {
            ulong hash = TypeHash<T>.Value;
            _commandStream.AddRange(&hash, sizeof(ulong));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteComponent<T>(T component) where T : unmanaged
        {
            int size = sizeof(T);
            WriteInt(size);
            _commandStream.AddRange(&component, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteInt(int value)
        {
            _commandStream.AddRange(&value, sizeof(int));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteByte(byte value) => _commandStream.Add(value);

        private void PlaybackDestroyEntity(ref CommandReader reader, EntityManager entityManager)
        {
            var entity = ReadEntity(ref reader);
            entityManager.DestroyEntity(entity);
        }

        private void PlaybackAddComponent(ref CommandReader reader, EntityManager entityManager)
        {
            var entity = ReadEntity(ref reader);
            ulong typeHash = reader.ReadULong();
            int size = reader.ReadInt();
            var data = reader.ReadBytes(size);

            ComponentPlayback.AddComponent(entityManager, entity, typeHash, data, size);
        }

        private void PlaybackRemoveComponent(ref CommandReader reader, EntityManager entityManager)
        {
            var entity = ReadEntity(ref reader);
            ulong typeHash = reader.ReadULong();

            ComponentPlayback.RemoveComponent(entityManager, entity, typeHash);
        }

        private void PlaybackSetComponent(ref CommandReader reader, EntityManager entityManager)
        {
            var entity = ReadEntity(ref reader);
            ulong typeHash = reader.ReadULong();
            int size = reader.ReadInt();
            var data = reader.ReadBytes(size);

            ComponentPlayback.SetComponent(entityManager, entity, typeHash, data, size);
        }

        private Entity ReadEntity(ref CommandReader reader)
        {
            int index = reader.ReadInt();
            int version = reader.ReadInt();
            byte isDeferred = reader.ReadByte();

            if (isDeferred == 1)
            {
                if (index < 0 || index >= _createdEntities.Length)
                    throw new IndexOutOfRangeException("Invalid deferred entity index");
                return _createdEntities[index];
            }

            return new Entity(index, version);
        }

        private struct CommandReader
        {
            private NativeArray<byte> _data;
            private int _position;

            public bool HasRemaining => _position < _data.Length;
            public int Remaining => _data.Length - _position;

            public CommandReader(NativeArray<byte> data)
            {
                _data = data;
                _position = 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public EntityOperation ReadCommand()
            {
                if (Remaining < 1)
                    throw new InvalidOperationException("Command stream overflow: not enough bytes to read command");
                return (EntityOperation)_data[_position++];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public byte ReadByte()
            {
                if (Remaining < 1)
                    throw new InvalidOperationException("Command stream overflow: not enough bytes to read byte");
                return _data[_position++];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe int ReadInt()
            {
                if (Remaining < 4)
                    throw new InvalidOperationException("Command stream overflow: not enough bytes to read int");
                int value = 0;
                var ptr = (byte*)&value;
                ptr[0] = _data[_position++];
                ptr[1] = _data[_position++];
                ptr[2] = _data[_position++];
                ptr[3] = _data[_position++];
                return value;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe ulong ReadULong()
            {
                if (Remaining < 8)
                    throw new InvalidOperationException("Command stream overflow: not enough bytes to read ulong");
                ulong value = 0;
                var ptr = (byte*)&value;
                for (int i = 0; i < 8; i++)
                    ptr[i] = _data[_position++];
                return value;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public unsafe byte* ReadBytes(int count)
            {
                // count is decoded from the stream, so it is corruption-influenced. The old
                // "_position + count > _data.Length" form was unsound in both directions: the
                // sum wraps negative for a count near int.MaxValue and passes the guard, and a
                // negative count passes trivially and then rewinds _position. Comparing against
                // Remaining, as the scalar readers do, cannot overflow.
                if (count < 0 || count > Remaining)
                    throw new InvalidOperationException("Command buffer read overflow");

                byte* result = (byte*)_data.GetUnsafeReadOnlyPtr() + _position;
                _position += count;
                return result;
            }
        }
    }

    internal static class TypeHash<T> where T : unmanaged
    {
        public static readonly ulong Value = ComputeHash();

        private static ulong ComputeHash()
        {
            string name = typeof(T).FullName ?? typeof(T).Name;
            ulong hash = 14695981039346656037UL;
            foreach (char c in name)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }

    public static class ComponentPlayback
    {
        private static readonly ConcurrentDictionary<ulong, IComponentPlaybackHandler> _handlers = new();

        public static void RegisterHandler<T>(IComponentPlaybackHandler handler) where T : unmanaged, IComponent
        {
            ulong hash = TypeHash<T>.Value;
            if (_handlers.TryGetValue(hash, out var existing) && existing.GetType() != handler.GetType())
                throw new InvalidOperationException(
                    $"TypeHash collision detected: hash {hash} is already registered for {existing.GetType().Name}, cannot register {handler.GetType().Name}");
            _handlers[hash] = handler;
        }

        public static unsafe void AddComponent(EntityManager em, Entity entity, ulong typeHash, byte* data, int size)
        {
            if (_handlers.TryGetValue(typeHash, out var handler))
                handler.AddComponent(em, entity, data, size);
            else
                WarnMissingHandler(typeHash, nameof(AddComponent));
        }

        public static void RemoveComponent(EntityManager em, Entity entity, ulong typeHash)
        {
            if (_handlers.TryGetValue(typeHash, out var handler))
                handler.RemoveComponent(em, entity);
            else
                WarnMissingHandler(typeHash, nameof(RemoveComponent));
        }

        public static unsafe void SetComponent(EntityManager em, Entity entity, ulong typeHash, byte* data, int size)
        {
            if (_handlers.TryGetValue(typeHash, out var handler))
                handler.SetComponent(em, entity, data, size);
            else
                WarnMissingHandler(typeHash, nameof(SetComponent));
        }

        // A command whose type hash has no handler used to be discarded without a trace.
        // Recording now calls EnsureHandler<T>(), so reaching this means the buffer was
        // written by a path that bypassed the generic record API.
        private static void WarnMissingHandler(ulong typeHash, string operation)
        {
            UnityEngine.Debug.LogError(
                $"[Strada] EntityCommandBuffer.{operation}: no playback handler registered for type hash {typeHash}. " +
                "The command was discarded. Call ComponentPlayback.EnsureHandler<T>() for this component type.");
        }

        /// <summary>
        /// Per-closed-generic marker recording that <typeparamref name="T"/>'s playback handler
        /// is already in <c>_handlers</c>.
        /// </summary>
        /// <remarks>
        /// EnsureHandler runs once per recorded command, so without this every AddComponent /
        /// SetComponent / RemoveComponent call paid a ConcurrentDictionary hash-and-probe just to
        /// confirm a registration that cannot be undone. A static field on a closed generic is a
        /// plain load. The write is idempotent, so a race between two threads recording the same
        /// component type is benign.
        /// </remarks>
        private static class HandlerCache<T> where T : unmanaged, IComponent
        {
            internal static bool Registered;
        }

        public static void EnsureHandler<T>() where T : unmanaged, IComponent
        {
            if (HandlerCache<T>.Registered) return;

            ulong hash = TypeHash<T>.Value;
            if (_handlers.TryGetValue(hash, out var existing))
            {
                if (existing is not ComponentPlaybackHandler<T>)
                    throw new InvalidOperationException(
                        $"TypeHash collision detected for {typeof(T).Name}: hash {hash} is already registered for {existing.GetType().Name}");
                HandlerCache<T>.Registered = true;
                return;
            }

            _handlers.TryAdd(hash, new ComponentPlaybackHandler<T>());
            HandlerCache<T>.Registered = true;
        }
    }

    public interface IComponentPlaybackHandler
    {
        unsafe void AddComponent(EntityManager em, Entity entity, byte* data, int size);
        void RemoveComponent(EntityManager em, Entity entity);
        unsafe void SetComponent(EntityManager em, Entity entity, byte* data, int size);
    }

    internal unsafe class ComponentPlaybackHandler<T> : IComponentPlaybackHandler where T : unmanaged, IComponent
    {
        public unsafe void AddComponent(EntityManager em, Entity entity, byte* data, int size)
        {
            CheckComponentSize(size);
            // Copied rather than dereferenced as T*: the payload sits at whatever byte offset the
            // variable-length command stream reached (22 bytes past the start of its own command,
            // with no padding anywhere), so its alignment is arbitrary. A typed load at a
            // misaligned address is undefined in ECMA-335 and faults on ARM32 IL2CPP or on any
            // platform once a component holds a 16-byte-aligned SIMD field.
            UnsafeUtility.CopyPtrToStructure(data, out T component);
            em.AddComponent(entity, component);
        }

        public void RemoveComponent(EntityManager em, Entity entity)
        {
            em.RemoveComponent<T>(entity);
        }

        public unsafe void SetComponent(EntityManager em, Entity entity, byte* data, int size)
        {
            CheckComponentSize(size);
            // See AddComponent: the stream payload is not guaranteed to be aligned for T.
            UnsafeUtility.CopyPtrToStructure(data, out T component);
            em.SetComponent(entity, component);
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void CheckComponentSize(int size)
        {
            if (size != sizeof(T))
                throw new InvalidOperationException(
                    $"Component size mismatch during playback: expected {sizeof(T)} bytes for {typeof(T).Name}, but got {size}");
        }
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckComponentSize(int size) { }
#endif
    }
}
