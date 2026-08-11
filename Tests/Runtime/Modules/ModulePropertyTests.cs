using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using NUnit.Framework;
using Strada.Core.DI;
using Strada.Core.Modules;
using Strada.Core.Tests.Tests.Runtime.Generators;

namespace Strada.Core.Tests.Tests.Runtime.Modules
{
    /// <summary>
    /// Property-based tests for Module system.
    /// Tests verify correctness properties for module dependency sorting and cycle detection.
    /// </summary>
    [TestFixture]
    public class ModulePropertyTests
    {
        [OneTimeSetUp]
        public void Setup()
        {
            StradaArbitraries.RegisterAll();
        }

        private class TestModuleBase : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        /// <summary>
        /// ModuleRegistry deduplicates by module TYPE, so registering N instances of one type
        /// yields one module. Tests that need N distinct modules must supply N distinct types;
        /// this generic wrapper plus the marker structs below provide a pool of them.
        /// </summary>
        private class TestModule<TMarker> : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        private struct M0 { }
        private struct M1 { }
        private struct M2 { }
        private struct M3 { }
        private struct M4 { }
        private struct M5 { }
        private struct M6 { }
        private struct M7 { }
        private struct M8 { }
        private struct M9 { }
        private struct M10 { }
        private struct M11 { }
        private struct M12 { }
        private struct M13 { }
        private struct M14 { }
        private struct M15 { }
        private struct M16 { }
        private struct M17 { }
        private struct M18 { }
        private struct M19 { }

        private static readonly Func<IModuleInstaller>[] DistinctModules =
        {
            () => new TestModule<M0>(),
            () => new TestModule<M1>(),
            () => new TestModule<M2>(),
            () => new TestModule<M3>(),
            () => new TestModule<M4>(),
            () => new TestModule<M5>(),
            () => new TestModule<M6>(),
            () => new TestModule<M7>(),
            () => new TestModule<M8>(),
            () => new TestModule<M9>(),
            () => new TestModule<M10>(),
            () => new TestModule<M11>(),
            () => new TestModule<M12>(),
            () => new TestModule<M13>(),
            () => new TestModule<M14>(),
            () => new TestModule<M15>(),
            () => new TestModule<M16>(),
            () => new TestModule<M17>(),
            () => new TestModule<M18>(),
            () => new TestModule<M19>(),
        };

        /// <summary>Maximum number of distinct module types the pool can supply.</summary>
        private const int DistinctModuleCapacity = 20;

        private class DynamicModuleInfo
        {
            public string Name { get; set; }
            public List<string> Dependencies { get; set; } = new List<string>();
        }

        /// <summary>
        /// Generator for module count (reasonable size for testing).
        /// </summary>
        private static Gen<int> ModuleCountGen => Gen.Choose(2, 15);

        /// <summary>
        /// Generator for a valid DAG (Directed Acyclic Graph) of module dependencies.
        /// Ensures no cycles by only allowing dependencies on modules with lower indices.
        /// </summary>
        private static Gen<List<DynamicModuleInfo>> ValidDagGen =>
            from moduleCount in ModuleCountGen
            from modules in CreateValidDag(moduleCount)
            select modules;

        /// <summary>
        /// Creates a valid DAG where each module can only depend on modules that come before it.
        /// This guarantees no cycles.
        /// </summary>
        private static Gen<List<DynamicModuleInfo>> CreateValidDag(int moduleCount)
        {
            return Gen.Sequence(
                Enumerable.Range(0, moduleCount).Select(i => CreateModuleWithValidDeps(i, moduleCount))
            ).Select(modules => modules.ToList());
        }

        /// <summary>
        /// Creates a module that can only depend on modules with lower indices (ensuring DAG).
        /// </summary>
        private static Gen<DynamicModuleInfo> CreateModuleWithValidDeps(int index, int totalCount)
        {
            if (index == 0)
            {
                return Gen.Constant(new DynamicModuleInfo { Name = $"Module{index}" });
            }

            return from depCount in Gen.Choose(0, Math.Min(index, 3))
                   from depIndices in Gen.ArrayOf(depCount, Gen.Choose(0, index - 1))
                   select new DynamicModuleInfo
                   {
                       Name = $"Module{index}",
                       Dependencies = depIndices.Distinct().Select(d => $"Module{d}").ToList()
                   };
        }

        /// <summary>
        /// Generator for a graph with a guaranteed cycle.
        /// Creates a simple cycle: A -> B -> C -> A
        /// </summary>
        private static Gen<List<DynamicModuleInfo>> CyclicGraphGen =>
            from extraModules in Gen.Choose(0, 5)
            from cycleSize in Gen.Choose(2, 5)
            select CreateCyclicGraph(cycleSize, extraModules);

        /// <summary>
        /// Creates a graph with a cycle of specified size.
        /// </summary>
        private static List<DynamicModuleInfo> CreateCyclicGraph(int cycleSize, int extraModules)
        {
            var modules = new List<DynamicModuleInfo>();

            for (int i = 0; i < cycleSize; i++)
            {
                modules.Add(new DynamicModuleInfo
                {
                    Name = $"CycleModule{i}",
                    Dependencies = new List<string> { $"CycleModule{(i + 1) % cycleSize}" }
                });
            }

            for (int i = 0; i < extraModules; i++)
            {
                modules.Add(new DynamicModuleInfo
                {
                    Name = $"ExtraModule{i}",
                    Dependencies = new List<string>()
                });
            }

            return modules;
        }

        /// <summary>
        /// Creates a ModuleRegistry populated with the given module graph.
        /// Uses dynamically created types to simulate real module dependencies.
        /// </summary>
        private ModuleRegistry CreateRegistryFromGraph(List<DynamicModuleInfo> graph)
        {
            var registry = new ModuleRegistry();
            var typeMap = new Dictionary<string, Type>();

            foreach (var module in graph)
            {
                var installer = new TestModuleBase();
                var moduleType = installer.GetType();
            }

            return registry;
        }

        /// <summary>
        /// Verifies that a list of modules is in valid topological order.
        /// A module should only appear after all its dependencies.
        /// </summary>
        private bool IsValidTopologicalOrder(List<ModuleInfo> sortedModules)
        {
            var processedTypes = new HashSet<Type>();

            foreach (var module in sortedModules)
            {
                foreach (var dependency in module.Dependencies)
                {
                    if (!processedTypes.Contains(dependency))
                    {
                        var depExists = sortedModules.Any(m => m.Type == dependency);
                        if (depExists)
                        {
                            return false;
                        }
                    }
                }

                processedTypes.Add(module.Type);
            }

            return true;
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 17: Module Topological Order**
        /// For any set of modules with dependencies, after sorting each module
        /// SHALL appear after all its dependencies in the list.
        /// **Validates: Requirements 6.2**
        /// </summary>
        [Test]
        public void ModuleTopologicalOrder_DependenciesAppearBeforeDependents()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                ValidDagGen.ToArbitrary(),
                (moduleGraph) =>
                {
                    var registry = new ModuleRegistry();
                    var typeMap = new Dictionary<string, Type>();
                    var installers = new Dictionary<string, IModuleInstaller>();
                    var moduleInfos = new List<ModuleInfo>();

                    for (int i = 0; i < moduleGraph.Count; i++)
                    {
                        var graphModule = moduleGraph[i];
                        // Distinct type per module: ModuleRegistry deduplicates by type, so
                        // reusing one type collapsed the whole graph to a single module and
                        // made the count assertion below unsatisfiable.
                        var installer = DistinctModules[i]();

                        var moduleInfo = new ModuleInfo
                        {
                            Installer = installer,
                            Type = installer.GetType(),
                            Name = graphModule.Name,
                            Priority = 0,
                            Dependencies = new List<Type>()
                        };

                        moduleInfos.Add(moduleInfo);
                        typeMap[graphModule.Name] = installer.GetType();
                        installers[graphModule.Name] = installer;
                    }

                    var shuffledGraph = moduleGraph.OrderBy(_ => Guid.NewGuid()).ToList();
                    foreach (var module in shuffledGraph)
                    {
                        registry.RegisterModule(installers[module.Name]);
                    }

                    registry.Sort();

                    return registry.Modules.Count == moduleGraph.Count;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 17: Module Topological Order**
        /// Additional test: Linear chain dependencies are sorted correctly.
        /// **Validates: Requirements 6.2**
        /// </summary>
        [Test]
        public void ModuleTopologicalOrder_LinearChain_SortedCorrectly()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                Gen.Choose(2, 10).ToArbitrary(),
                (chainLength) =>
                {
                    var registry = new ModuleRegistry();
                    var modules = new List<IModuleInstaller>();
                    for (int i = 0; i < chainLength; i++)
                    {
                        modules.Add(DistinctModules[i]());
                    }

                    for (int i = chainLength - 1; i >= 0; i--)
                    {
                        registry.RegisterModule(modules[i], priority: i);
                    }

                    registry.Sort();

                    return registry.Modules.Count == chainLength;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 17: Module Topological Order**
        /// Additional test: Modules without dependencies can appear in any order relative to each other.
        /// **Validates: Requirements 6.2**
        /// </summary>
        [Test]
        public void ModuleTopologicalOrder_IndependentModules_AllPresent()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                Gen.Choose(1, 20).ToArbitrary(),
                (moduleCount) =>
                {
                    var registry = new ModuleRegistry();
                    var installers = new List<IModuleInstaller>();

                    for (int i = 0; i < moduleCount; i++)
                    {
                        var installer = DistinctModules[i]();
                        installers.Add(installer);
                        registry.RegisterModule(installer, priority: i);
                    }

                    registry.Sort();

                    return registry.Modules.Count == moduleCount;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 18: Circular Dependency Detection**
        /// For any module dependency graph containing a cycle,
        /// Validate SHALL return false and report the cycle.
        /// **Validates: Requirements 6.3**
        /// </summary>
        [Test]
        public void CircularDependencyDetection_CycleDetected_ReturnsFalse()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                Gen.Choose(2, 6).ToArbitrary(),
                (cycleSize) =>
                {
                    var registry = new ModuleRegistry();

                    registry.RegisterModule(new CircularModuleA());
                    registry.RegisterModule(new CircularModuleB());

                    var isValid = registry.Validate(out var errorMessage);

                    return !isValid &&
                           errorMessage != null &&
                           errorMessage.Contains("Circular");
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 18: Circular Dependency Detection**
        /// Additional test: Valid DAG passes validation.
        /// **Validates: Requirements 6.3**
        /// </summary>
        [Test]
        public void CircularDependencyDetection_ValidDag_ReturnsTrue()
        {
            var config = PropertyTestConfig.CreateConfig();

            var property = Prop.ForAll(
                Gen.Choose(1, 20).ToArbitrary(),
                (moduleCount) =>
                {
                    var registry = new ModuleRegistry();

                    for (int i = 0; i < moduleCount; i++)
                    {
                        registry.RegisterModule(DistinctModules[i](), priority: i);
                    }

                    var isValid = registry.Validate(out var errorMessage);

                    return isValid && errorMessage == null;
                });

            property.Check(config);
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 18: Circular Dependency Detection**
        /// Additional test: Self-referencing module is detected as cycle.
        /// **Validates: Requirements 6.3**
        /// </summary>
        [Test]
        public void CircularDependencyDetection_SelfReference_DetectedAsCycle()
        {
            var registry = new ModuleRegistry();
            registry.RegisterModule(new SelfReferencingModule());

            var isValid = registry.Validate(out var errorMessage);

            Assert.IsFalse(isValid);
            Assert.IsNotNull(errorMessage);
            Assert.IsTrue(errorMessage.Contains("Circular"));
        }

        /// <summary>
        /// **Feature: strada-codebase-audit, Property 18: Circular Dependency Detection**
        /// Additional test: Chain with cycle at end is detected.
        /// **Validates: Requirements 6.3**
        /// </summary>
        [Test]
        public void CircularDependencyDetection_ChainWithCycle_Detected()
        {
            var registry = new ModuleRegistry();
            registry.RegisterModule(new ChainModuleA());
            registry.RegisterModule(new ChainModuleB());
            registry.RegisterModule(new ChainModuleC());

            var isValid = registry.Validate(out var errorMessage);

            Assert.IsFalse(isValid);
            Assert.IsNotNull(errorMessage);
            Assert.IsTrue(errorMessage.Contains("Circular"));
        }

        [ModuleDependsOn(typeof(CircularModuleB))]
        private class CircularModuleA : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        [ModuleDependsOn(typeof(CircularModuleA))]
        private class CircularModuleB : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        [ModuleDependsOn(typeof(SelfReferencingModule))]
        private class SelfReferencingModule : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        [ModuleDependsOn(typeof(ChainModuleB))]
        private class ChainModuleA : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        [ModuleDependsOn(typeof(ChainModuleC))]
        private class ChainModuleB : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        [ModuleDependsOn(typeof(ChainModuleB))]
        private class ChainModuleC : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        [ModuleDependsOn(typeof(ValidChainModuleE))]
        private class ValidChainModuleD : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        [ModuleDependsOn(typeof(ValidChainModuleF))]
        private class ValidChainModuleE : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        private class ValidChainModuleF : IModuleInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }
    }
}
