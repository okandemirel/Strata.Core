using System;
using FsCheck;
using Strada.Core.DI;
using Strada.Core.ECS;

namespace Strada.Core.Tests.Tests.Runtime.Generators
{
    /// <summary>
    /// Registers all Strada-specific arbitraries with FsCheck.
    /// Call RegisterAll() before running property tests.
    /// </summary>
    public static class StradaArbitraries
    {
        private static bool _registered;

        /// <summary>
        /// Registers all custom arbitraries for Strada types.
        /// Safe to call multiple times - only registers once.
        /// </summary>
        public static void RegisterAll()
        {
            if (_registered) return;

            Arb.Register<StradaArbitraryProvider>();
            _registered = true;
        }

        /// <summary>
        /// FsCheck arbitrary provider for all Strada types.
        /// </summary>
        private class StradaArbitraryProvider
        {
            public static Arbitrary<Entity> Entity() => EntityGenerator.EntityArbitrary;
            public static Arbitrary<TestComponent> TestComponent() => ComponentGenerator.TestComponentArbitrary;
            public static Arbitrary<TestComponent2> TestComponent2() => ComponentGenerator.TestComponent2Arbitrary;
            public static Arbitrary<TestComponent3> TestComponent3() => ComponentGenerator.TestComponent3Arbitrary;
            public static Arbitrary<RegistrationConfig> RegistrationConfig() => RegistrationGenerator.RegistrationConfigArbitrary;
            public static Arbitrary<Lifetime> Lifetime() => RegistrationGenerator.LifetimeArbitrary;
        }
    }

    /// <summary>
    /// Configuration for property-based tests.
    /// </summary>
    public static class PropertyTestConfig
    {
        /// <summary>
        /// Default number of test iterations (100 as per design doc).
        /// </summary>
        public const int DefaultMaxTest = 100;

        /// <summary>
        /// Creates a standard FsCheck configuration for Strada tests.
        /// </summary>
        /// <param name="maxTest">Number of cases FsCheck generates for the property.</param>
        /// <param name="seed">
        /// Seed for the generator. Omit to draw a fresh one per run; pass the seed printed by a
        /// failing run to replay exactly the inputs that falsified the property.
        /// </param>
        public static Configuration CreateConfig(int maxTest = DefaultMaxTest, int? seed = null)
        {
            StradaArbitraries.RegisterAll();

            // QuickThrowOnFailure's runner throws on a falsified property, which is what makes
            // the NUnit test fail. The default Configuration runner only prints, so every
            // property test using it passed unconditionally no matter what it found.
            var config = Configuration.QuickThrowOnFailure;
            config.MaxNbOfTest = maxTest;

            // Without a Replay the generator seeds itself from the clock, so a property that
            // fails one run in fifty cannot be reproduced from the CI log. Recording an explicit
            // seed makes every run replayable: read it out of the failure message and pass it
            // back as CreateConfig(seed: N) to regenerate the same cases.
            //
            // The counter keeps two configs created inside the same millisecond from drawing the
            // same seed, which would otherwise make separate property tests explore identical
            // generator sequences.
            int effectiveSeed = seed ?? unchecked(Environment.TickCount + _seedCounter++);
            LastSeed = effectiveSeed;

            // StdGen carries two independent 31-bit streams and FsCheck requires both to be at
            // least 1; this is the same derivation its own mkStdGen performs.
            long s = Math.Abs((long)effectiveSeed);
            config.Replay = FsCheck.Random.StdGen.NewStdGen(
                (int)(s % 2147483562L) + 1,
                (int)(s / 2147483562L % 2147483398L) + 1);
            return config;
        }

        private static int _seedCounter;

        /// <summary>
        /// Seed handed to the most recent <see cref="CreateConfig"/>. Reported by
        /// <see cref="ReplayHint"/> so a failing test can say how to reproduce itself.
        /// </summary>
        public static int LastSeed { get; private set; }

        /// <summary>
        /// Message to attach to a property assertion so a failure carries its own reproduction
        /// recipe.
        /// </summary>
        public static string ReplayHint =>
            $"Replay this exact run with PropertyTestConfig.CreateConfig(seed: {LastSeed}).";
    }
}
