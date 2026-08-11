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
        public static Configuration CreateConfig(int maxTest = DefaultMaxTest)
        {
            StradaArbitraries.RegisterAll();

            // QuickThrowOnFailure's runner throws on a falsified property, which is what makes
            // the NUnit test fail. The default Configuration runner only prints, so every
            // property test using it passed unconditionally no matter what it found.
            var config = Configuration.QuickThrowOnFailure;
            config.MaxNbOfTest = maxTest;
            return config;
        }
    }
}
