using System;
using System.Runtime.CompilerServices;

namespace Strada.Core.Tests.Tests.Runtime.Performance
{
    /// <summary>
    /// Escape hatch for benchmark results, and an honest allocation meter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the sink.</b> A benchmark whose result is never observed can be deleted
    /// wholesale by the optimiser — Mono's JIT already does this to trivial bodies, and
    /// IL2CPP under OptimizeSpeed is free to do the same. Several benchmarks here computed a
    /// value and dropped it, which is exactly how a microbenchmark reports a number far below
    /// the real cost. Assigning to a public static field the optimiser cannot prove is unread
    /// forces the work to happen.
    /// </para>
    /// <para>
    /// <b>How allocation is measured.</b> Not with <c>GC.GetTotalMemory(true)</c>: that
    /// reports the live heap AFTER a collection, so the transient garbage being measured has
    /// already been collected and always reads as zero — a "0 bytes" claim backed by it would
    /// hold even for code allocating megabytes per call.
    /// Not with <see cref="GC.GetAllocatedBytesForCurrentThread"/> either: Unity's Mono does
    /// not implement it (verified — it returns a constant, so every reading is 0).
    /// The assertions therefore use Unity's own
    /// <c>UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory()</c>, which hooks the
    /// allocation callback in the runtime itself and is the only mechanism here that actually
    /// observes a managed allocation.
    /// </para>
    /// </remarks>
    public static class BenchmarkSink
    {
        /// <summary>Written by benchmarks so their result cannot be optimised away.</summary>
        public static object Reference;

        /// <summary>Written by benchmarks producing value types.</summary>
        public static long Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Consume(object value) => Reference = value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Consume(long value) => Value = value;

        /// <summary>
        /// Runs <paramref name="action"/> enough times to pay its one-time costs — JIT
        /// compilation, static constructors, lazily filled caches — so that a subsequent
        /// allocation assertion measures steady state rather than first-call setup.
        /// </summary>
        public static void Prime(Action action, int iterations = 100)
        {
            for (int i = 0; i < iterations; i++)
                action();
        }
    }
}
