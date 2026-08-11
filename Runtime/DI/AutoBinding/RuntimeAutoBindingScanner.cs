using System;
using System.Collections.Generic;
using System.IO;
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

        // volatile: read lock-free on the fast path below. Without it a reader on another core can
        // see the published reference before the snapshot's own field writes are visible, and then
        // dereference a half-constructed object.
        private static volatile CacheSnapshot _cache;
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

            // List<T>.Sort is introsort — unstable — and its input order is itself unspecified
            // (assembly load order, then Assembly.GetTypes order). Without a tiebreak, two entries
            // with equal Priority claiming the same ServiceType resolve differently between runs
            // and between machines, and registration is last-write-wins.
            sorted.Sort(static (a, b) =>
            {
                int byPriority = a.Priority.CompareTo(b.Priority);
                if (byPriority != 0) return byPriority;
                return string.CompareOrdinal(a.ImplementationType?.FullName, b.ImplementationType?.FullName);
            });

            Dictionary<Type, AutoBindingEntry> claimed = null;

            foreach (var entry in sorted)
            {
                if (entry.ServiceType != null)
                {
                    claimed ??= new Dictionary<Type, AutoBindingEntry>();
                    if (claimed.TryGetValue(entry.ServiceType, out var previous))
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[Strada AutoBinding] '{entry.ServiceType.FullName}' is claimed by both " +
                            $"'{previous.ImplementationType?.FullName}' (priority {previous.Priority}) and " +
                            $"'{entry.ImplementationType?.FullName}' (priority {entry.Priority}). " +
                            "The later registration wins; give one of them a higher Priority to make this explicit.");
                    }
                    claimed[entry.ServiceType] = entry;
                }

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

            // The scan itself runs inside the lock. It used to sit between the two lock blocks, so
            // two threads that both missed the cache both paid for a full scan and then published
            // different List instances — leaving callers holding different results.
            lock (_lock)
            {
                cached = _cache;
                if (MatchesCachedPatterns(cached, includePatterns, excludePatterns))
                {
                    return cached.Entries;
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
                        UnityEngine.Debug.LogWarning(
                            $"Partial type load from assembly {name}: {ex.Message}{DescribeLoaderExceptions(ex)}");

                        var loadedTypes = ex.Types;
                        if (loadedTypes != null)
                        {
                            foreach (var type in loadedTypes)
                            {
                                if (type == null || type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                                    continue;

                                // These types come from an assembly that is already in a broken load
                                // state, so attribute materialisation is more likely to throw here
                                // than anywhere — and we are inside a catch block, so nothing above
                                // would catch it.
                                var entry = TryCreateEntrySafe(type, name);
                                if (entry != null)
                                    entries.Add(entry);
                            }
                        }
                    }
                    // One unloadable assembly must skip itself rather than abort the whole scan and
                    // leave the container with zero auto-bindings.
                    catch (Exception ex) when (
                        ex is TypeLoadException ||
                        ex is FileNotFoundException ||
                        ex is FileLoadException ||
                        ex is BadImageFormatException)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[Strada AutoBinding] Skipping assembly {name}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                _cache = new CacheSnapshot(CopyPatterns(includePatterns), CopyPatterns(excludePatterns), entries);
                return entries;
            }
        }

        private static string DescribeLoaderExceptions(ReflectionTypeLoadException ex)
        {
            // ReflectionTypeLoadException.Message is the fixed, information-free "Unable to load one
            // or more of the requested types." — every actionable detail is in LoaderExceptions.
            var loaderExceptions = ex.LoaderExceptions;
            if (loaderExceptions == null || loaderExceptions.Length == 0)
                return string.Empty;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();

            foreach (var loaderException in loaderExceptions)
            {
                if (loaderException == null || !seen.Add(loaderException.Message))
                    continue;

                sb.Append(sb.Length == 0 ? " Loader errors: " : "; ");
                sb.Append(loaderException.Message);

                if (seen.Count == 5)
                {
                    sb.Append("; ...");
                    break;
                }
            }

            return sb.ToString();
        }

        private static void ScanAssembly(Assembly assembly, List<AutoBindingEntry> entries)
        {
            WarnIfMissingScopeAttribute(assembly);

            var assemblyName = assembly.GetName().Name;

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                    continue;

                var entry = TryCreateEntrySafe(type, assemblyName);
                if (entry != null)
                    entries.Add(entry);
            }
        }

        /// <summary>
        /// Builds an entry for one type, isolating it from the rest of the scan.
        /// </summary>
        /// <remarks>
        /// Attribute materialisation throws TypeLoadException when a Type baked into a named argument
        /// (<c>As = typeof(IFoo)</c>) cannot be loaded, and CustomAttributeFormatException on a
        /// malformed blob. Neither is a ReflectionTypeLoadException, so both used to escape the scan
        /// loop entirely and abort container construction with zero auto-bindings registered.
        /// </remarks>
        private static AutoBindingEntry TryCreateEntrySafe(Type type, string assemblyName)
        {
            try
            {
                return TryCreateEntry(type);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Strada AutoBinding] Skipping type '{type.FullName}' in {assemblyName}: {ex.GetType().Name}: {ex.Message}");
                return null;
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

            // inherit: false is essential. ServiceAttribute's [AttributeUsage] does not set
            // Inherited=false, so the default (true) applies, and the single-argument
            // GetCustomAttribute<T>() overload also defaults to inherit: true. Together they made
            // every subclass of a [Service] class auto-register itself against the BASE's
            // InterfaceType and Lifetime, silently replacing the base's binding.
            var serviceAttr = type.GetCustomAttribute<ServiceAttribute>(inherit: false);
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
            int interior = pattern.Length > 2 ? pattern.IndexOf('*', 1, pattern.Length - 2) : -1;
            if (interior >= 0)
            {
                // Interior wildcard, e.g. "Unity.*.Tests". These used to fall through every branch
                // below to exact equality, where a literal asterisk can never match a real assembly
                // name — so the pattern matched nothing and said nothing. Harmless for an include
                // pattern; for an exclude pattern it fails open and the assembly gets scanned.
                return MatchesGlob(name, pattern);
            }

            if (pattern.StartsWith("*") && pattern.EndsWith("*"))
                return name.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
            if (pattern.StartsWith("*"))
                return name.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
            if (pattern.EndsWith("*"))
                return name.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
            return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Case-insensitive glob match supporting '*' at any position, with backtracking.
        /// </summary>
        private static bool MatchesGlob(string name, string pattern)
        {
            int n = 0, p = 0, starIndex = -1, resumeAt = 0;

            while (n < name.Length)
            {
                if (p < pattern.Length && pattern[p] != '*' &&
                    char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n]))
                {
                    n++;
                    p++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    starIndex = p;
                    resumeAt = n;
                    p++;
                }
                else if (starIndex >= 0)
                {
                    // Backtrack: let the last '*' swallow one more character.
                    p = starIndex + 1;
                    resumeAt++;
                    n = resumeAt;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
                p++;

            return p == pattern.Length;
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
