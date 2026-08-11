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
        private int _updatePass;

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

            // Update walks the list downwards, so an appended timer is safely above the cursor,
            // but a recycled index frequently lands below it. Stamping the pass lets Update skip
            // anything scheduled from inside the pass that is currently running; otherwise a
            // timer scheduled from a callback has a full deltaTime subtracted from it before any
            // real time has elapsed.
            entry.ScheduledPass = _updatePass;

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
            unchecked { _updatePass++; }
            int pass = _updatePass;

            for (int i = _timers.Count - 1; i >= 0; i--)
            {
                // The loop bound was captured before the first callback ran, and a callback is
                // free to call CancelAll() (or Dispose()), which empties the list underneath us.
                if (i >= _timers.Count)
                    continue;

                var timer = _timers[i];
                if (timer == null || timer.IsCancelled || timer.IsPaused || timer.ScheduledPass == pass)
                    continue;

                timer.RemainingTime -= deltaTime;

                if (timer.RemainingTime > 0)
                    continue;

                // Entries are pooled and indices are recycled, so a callback that cancels its own
                // handle and then schedules a new timer gets back the very same TimerEntry at the
                // very same index. The captured id is what tells the bookkeeping below apart from
                // that replacement.
                int id = timer.Id;

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
                    // Retire the poison timer rather than replaying it every frame, unless the
                    // callback already cancelled it and something else now owns this slot.
                    var faulted = i < _timers.Count ? _timers[i] : null;
                    if (faulted != null && faulted.Id == id)
                        RemoveAt(i);
                    continue;
                }

                timer = i < _timers.Count ? _timers[i] : null;
                if (timer == null || timer.Id != id)
                    continue;

                if (timer.RemainingRepeats > 0)
                    timer.RemainingRepeats--;

                if (timer.RemainingRepeats == 0)
                {
                    RemoveAt(i);
                    continue;
                }

                // Accumulate rather than assign. The timer has normally overshot past zero by a
                // fraction of deltaTime, and resetting to the full interval discards that
                // overshoot, so a repeating timer loses up to a whole frame on every fire and
                // runs systematically slow.
                timer.RemainingTime += timer.Interval;

                // A frame longer than the interval leaves the timer behind and it can only fire
                // once per pass, so cap the accrued debt at a single interval instead of letting
                // RemainingTime drift towards negative infinity.
                if (timer.RemainingTime < -timer.Interval)
                    timer.RemainingTime = 0f;
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

            // RemoveAt only nulls the slot and enqueues its index for reuse — it never shrinks
            // the list. Clearing _freeIndices on its own therefore threw away every index that
            // had just been freed, leaving _timers full of permanently unreachable nulls that
            // Update walked every frame for the rest of the process. Reset both together.
            _timers.Clear();
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
            public int ScheduledPass;

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
