using System;
using System.Collections.Generic;
using System.Reflection;
using Strada.Core.DI.Attributes;

namespace Strada.Core.DI.AutoBinding
{
    public sealed class AutoBindingEntry
    {
        public Type ServiceType { get; set; }
        public Type ImplementationType { get; set; }
        public Lifetime Lifetime { get; set; }
        public int Priority { get; set; }
        public bool RegisterSelf { get; set; }
    }

    public static class RuntimeAutoBindingScanner
    {
        private static readonly string[] DefaultIncludePatterns = { "Strada.*", "Game.*", "Assembly-CSharp" };
        private static readonly string[] DefaultExcludePatterns = { "Unity.*", "System.*", "Mono.*", "mscorlib", "*.Tests", "*.Editor" };

        private static CacheSnapshot _cache;
        private static readonly object _lock = new();

        // One-time deprecation warning tracking: assemblies that matched an include pattern
        // but lack [assembly: AutoBindingScope]. In a future major release, scanning will
        // refuse to process such assemblies and the warning will become a hard error.
        private static readonly HashSet<string> _warnedAssemblies = new();

        public static void RegisterAll(
            IContainerBuilder builder,
            IReadOnlyList<string> includePatterns = null,
            IReadOnlyList<string> excludePatterns = null)
        {
            var entries = ScanAssemblies(includePatterns, excludePatterns);
            var sorted = new List<AutoBindingEntry>(entries);
            sorted.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            foreach (var entry in sorted)
            {
                RegisterEntry(builder, entry);
            }
        }

        public static List<AutoBindingEntry> ScanAssemblies(
            IReadOnlyList<string> includePatterns = null,
            IReadOnlyList<string> excludePatterns = null)
        {
            includePatterns ??= DefaultIncludePatterns;
            excludePatterns ??= DefaultExcludePatterns;

            var cached = _cache;
            if (MatchesCachedPatterns(cached, includePatterns, excludePatterns))
            {
                return cached.Entries;
            }

            lock (_lock)
            {
                cached = _cache;
                if (MatchesCachedPatterns(cached, includePatterns, excludePatterns))
                {
                    return cached.Entries;
                }
            }

            var entries = new List<AutoBindingEntry>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                var name = assembly.GetName().Name;
                if (!MatchesAnyPattern(name, includePatterns) ||
                    MatchesAnyPattern(name, excludePatterns))
                    continue;

                try
                {
                    ScanAssembly(assembly, entries);
                }
                catch (ReflectionTypeLoadException ex)
                {
                    UnityEngine.Debug.LogWarning($"Partial type load from assembly {assembly.GetName().Name}: {ex.Message}");
                    var loadedTypes = ex.Types;
                    if (loadedTypes != null)
                    {
                        foreach (var type in loadedTypes)
                        {
                            if (type == null || type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                                continue;
                            var entry = TryCreateEntry(type);
                            if (entry != null)
                                entries.Add(entry);
                        }
                    }
                }
            }

            lock (_lock)
            {
                _cache = new CacheSnapshot(CopyPatterns(includePatterns), CopyPatterns(excludePatterns), entries);
            }

            return entries;
        }

        private static void ScanAssembly(Assembly assembly, List<AutoBindingEntry> entries)
        {
            WarnIfMissingScopeAttribute(assembly);

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                    continue;

                var entry = TryCreateEntry(type);
                if (entry != null)
                    entries.Add(entry);
            }
        }

        private static void WarnIfMissingScopeAttribute(Assembly assembly)
        {
            var asmName = assembly.GetName().Name;
            if (string.IsNullOrEmpty(asmName)) return;

            // Strada's own assemblies are implicitly trusted.
            if (asmName.StartsWith("Strada.", StringComparison.OrdinalIgnoreCase)) return;

            if (assembly.IsDefined(typeof(AutoBindingScopeAttribute), inherit: false)) return;

            // Log once per assembly per session — repeated scans would otherwise spam the console.
            lock (_warnedAssemblies)
            {
                if (!_warnedAssemblies.Add(asmName)) return;
            }

            UnityEngine.Debug.LogWarning(
                $"[Strada AutoBinding] Assembly '{asmName}' matches an include pattern but lacks " +
                "[assembly: AutoBindingScope]. Auto-binding without this attribute is deprecated " +
                "and will become a hard error in a future major release. Add " +
                "'[assembly: Strada.Core.DI.AutoBinding.AutoBindingScope]' to opt in explicitly.");
        }

        private static AutoBindingEntry TryCreateEntry(Type type)
        {
            var autoReg = type.GetCustomAttribute<AutoRegisterAttribute>();
            if (autoReg != null)
            {
                return new AutoBindingEntry
                {
                    ImplementationType = type,
                    ServiceType = autoReg.As ?? type,
                    Lifetime = autoReg.Lifetime,
                    Priority = autoReg.Priority,
                    RegisterSelf = autoReg.RegisterSelf
                };
            }

            var baseAttr = type.GetCustomAttribute<AutoRegisterBaseAttribute>(inherit: false);
            if (baseAttr != null)
            {
                return new AutoBindingEntry
                {
                    ImplementationType = type,
                    ServiceType = baseAttr.As ?? type,
                    Lifetime = baseAttr.Lifetime,
                    Priority = baseAttr.Priority,
                    RegisterSelf = baseAttr.RegisterSelf
                };
            }

            var serviceAttr = type.GetCustomAttribute<ServiceAttribute>();
            if (serviceAttr != null)
            {
                return new AutoBindingEntry
                {
                    ImplementationType = type,
                    ServiceType = serviceAttr.InterfaceType ?? type,
                    Lifetime = serviceAttr.Lifetime,
                    Priority = 0,
                    RegisterSelf = false
                };
            }

            return null;
        }

        private static MethodInfo _registerOneGeneric;
        private static MethodInfo _registerTwoGeneric;

        private static MethodInfo RegisterOneGeneric =>
            _registerOneGeneric ??= Array.Find(typeof(IContainerBuilder).GetMethods(),
                m => m.Name == "Register" && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1);

        private static MethodInfo RegisterTwoGeneric =>
            _registerTwoGeneric ??= Array.Find(typeof(IContainerBuilder).GetMethods(),
                m => m.Name == "Register" && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 1);

        private static void RegisterEntry(IContainerBuilder builder, AutoBindingEntry entry)
        {
            var args = new object[] { entry.Lifetime };

            if (entry.ServiceType != entry.ImplementationType)
            {
                if (!entry.ServiceType.IsAssignableFrom(entry.ImplementationType))
                {
                    UnityEngine.Debug.LogWarning(
                        $"AutoBinding skipped: {entry.ImplementationType.FullName} is not assignable to {entry.ServiceType.FullName}");
                    return;
                }

                RegisterTwoGeneric.MakeGenericMethod(entry.ServiceType, entry.ImplementationType)
                    .Invoke(builder, args);

                if (entry.RegisterSelf)
                {
                    RegisterOneGeneric.MakeGenericMethod(entry.ImplementationType)
                        .Invoke(builder, args);
                }
            }
            else
            {
                RegisterOneGeneric.MakeGenericMethod(entry.ImplementationType)
                    .Invoke(builder, args);
            }
        }

        private static bool MatchesAnyPattern(string name, IReadOnlyList<string> patterns)
        {
            foreach (var pattern in patterns)
            {
                if (MatchesPattern(name, pattern))
                    return true;
            }
            return false;
        }

        private static bool MatchesPattern(string name, string pattern)
        {
            if (pattern.StartsWith("*") && pattern.EndsWith("*"))
                return name.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
            if (pattern.StartsWith("*"))
                return name.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
            if (pattern.EndsWith("*"))
                return name.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
            return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesCachedPatterns(CacheSnapshot cached, IReadOnlyList<string> includePatterns, IReadOnlyList<string> excludePatterns)
        {
            return cached != null &&
                   PatternListsEqual(cached.IncludePatterns, includePatterns) &&
                   PatternListsEqual(cached.ExcludePatterns, excludePatterns);
        }

        private static bool PatternListsEqual(IReadOnlyList<string> cachedPatterns, IReadOnlyList<string> patterns)
        {
            if (ReferenceEquals(cachedPatterns, patterns))
                return true;

            if (cachedPatterns == null || patterns == null || cachedPatterns.Count != patterns.Count)
                return false;

            for (int i = 0; i < cachedPatterns.Count; i++)
            {
                if (!string.Equals(cachedPatterns[i], patterns[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static string[] CopyPatterns(IReadOnlyList<string> patterns)
        {
            var copy = new string[patterns.Count];
            for (int i = 0; i < patterns.Count; i++)
                copy[i] = patterns[i];
            return copy;
        }

        public static void ClearCache()
        {
            lock (_lock)
            {
                _cache = null;
            }
        }

        public static int GetCachedCount()
        {
            return _cache?.Entries.Count ?? 0;
        }

        private sealed class CacheSnapshot
        {
            public readonly string[] IncludePatterns;
            public readonly string[] ExcludePatterns;
            public readonly List<AutoBindingEntry> Entries;

            public CacheSnapshot(string[] includePatterns, string[] excludePatterns, List<AutoBindingEntry> entries)
            {
                IncludePatterns = includePatterns;
                ExcludePatterns = excludePatterns;
                Entries = entries;
            }
        }
    }
}
