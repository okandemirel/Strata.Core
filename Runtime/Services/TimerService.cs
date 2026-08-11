using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Strada.Core.Patterns.Interfaces;
using Strada.Core.Pooling;

namespace Strada.Core.Services
{
    public sealed class TimerService : IService, IDisposable
    {
        private readonly List<TimerEntry> _timers = new(64);
        private readonly Queue<int> _freeIndices = new(32);
        private readonly ObjectPool<TimerEntry> _entryPool;
        private int _nextId = 1;

        public TimerService()
        {
            _entryPool = new ObjectPool<TimerEntry>(() => new TimerEntry(), 32);
        }

        public void Initialize() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TimerHandle After(float delay, Action callback)
        {
            return Schedule(delay, 0, 1, callback);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TimerHandle Every(float interval, Action callback, int repeatCount = -1)
        {
            return Schedule(interval, interval, repeatCount, callback);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TimerHandle Schedule(float delay, float interval, int repeatCount, Action callback)
        {
            var entry = _entryPool.Spawn();
            if (_nextId == int.MaxValue)
                throw new InvalidOperationException(
                    "TimerService ID space exhausted (int.MaxValue timers scheduled). " +
                    "Restart the application or recycle the service.");
            entry.Id = _nextId++;
            entry.Delay = delay;
            entry.Interval = interval;
            entry.RemainingTime = delay;
            entry.RemainingRepeats = repeatCount;
            entry.Callback = callback;
            entry.IsCancelled = false;
            entry.IsPaused = false;

            int index;
            if (_freeIndices.Count > 0)
            {
                index = _freeIndices.Dequeue();
                _timers[index] = entry;
            }
            else
            {
                index = _timers.Count;
                _timers.Add(entry);
            }

            entry.Index = index;
            return new TimerHandle(this, entry.Id, index);
        }

        public void Update(float deltaTime)
        {
            for (int i = _timers.Count - 1; i >= 0; i--)
            {
                var timer = _timers[i];
                if (timer == null || timer.IsCancelled || timer.IsPaused)
                    continue;

                timer.RemainingTime -= deltaTime;

                if (timer.RemainingTime > 0)
                    continue;

                // A throwing callback must not escape Update. It previously propagated out
                // before the bookkeeping below ran, so the timer was never retired and fired
                // again the next frame — wedging the frame loop permanently.
                try
                {
                    timer.Callback?.Invoke();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                    // Retire the poison timer rather than replaying it every frame.
                    RemoveAt(i);
                    continue;
                }

                if (timer.RemainingRepeats > 0)
                    timer.RemainingRepeats--;

                if (timer.RemainingRepeats == 0)
                {
                    RemoveAt(i);
                    continue;
                }

                timer.RemainingTime = timer.Interval;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Cancel(int id, int index)
        {
            if (index < 0 || index >= _timers.Count) return;
            var timer = _timers[index];
            if (timer == null || timer.Id != id) return;
            timer.IsCancelled = true;
            RemoveAt(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Pause(int id, int index)
        {
            if (index < 0 || index >= _timers.Count) return;
            var timer = _timers[index];
            if (timer != null && timer.Id == id)
                timer.IsPaused = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resume(int id, int index)
        {
            if (index < 0 || index >= _timers.Count) return;
            var timer = _timers[index];
            if (timer != null && timer.Id == id)
                timer.IsPaused = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsActive(int id, int index)
        {
            if (index < 0 || index >= _timers.Count) return false;
            var timer = _timers[index];
            return timer != null && timer.Id == id && !timer.IsCancelled;
        }

        private void RemoveAt(int index)
        {
            var timer = _timers[index];
            if (timer == null) return;

            timer.Callback = null;
            _entryPool.Despawn(timer);
            _timers[index] = null;
            _freeIndices.Enqueue(index);
        }

        public void CancelAll()
        {
            for (int i = 0; i < _timers.Count; i++)
                RemoveAt(i);
            _freeIndices.Clear();
        }

        public void Dispose()
        {
            CancelAll();
            _entryPool.Dispose();
        }

        private sealed class TimerEntry : IPoolable
        {
            public int Id;
            public int Index;
            public float Delay;
            public float Interval;
            public float RemainingTime;
            public int RemainingRepeats;
            public Action Callback;
            public bool IsCancelled;
            public bool IsPaused;

            public void OnSpawn() { }

            public void OnDespawn()
            {
                Callback = null;
                IsCancelled = false;
                IsPaused = false;
            }
        }
    }

    public readonly struct TimerHandle
    {
        private readonly TimerService _service;
        private readonly int _id;
        private readonly int _index;

        internal TimerHandle(TimerService service, int id, int index)
        {
            _service = service;
            _id = id;
            _index = index;
        }

        public bool IsValid => _service != null && _id > 0;
        public bool IsActive => IsValid && _service.IsActive(_id, _index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Cancel() => _service?.Cancel(_id, _index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Pause() => _service?.Pause(_id, _index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resume() => _service?.Resume(_id, _index);
    }
}
