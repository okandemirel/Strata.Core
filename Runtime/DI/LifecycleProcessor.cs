using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Strada.Core.DI.Attributes;

namespace Strada.Core.DI
{
    public static class LifecycleProcessor
    {
        // Concurrent, not Dictionary + lock: the fast-path read used to run outside the lock that
        // guarded the writes, and a Dictionary resize reassigns _buckets and _entries separately —
        // a reader racing that sees a torn pair and either faults or walks a corrupted chain forever.
        private static readonly ConcurrentDictionary<Type, MethodInfo[]> PostConstructCache = new();
        private static readonly ConcurrentDictionary<Type, MethodInfo[]> DeConstructCache = new();
        private const BindingFlags MethodFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void InvokePostConstruct(object target)
        {
            if (target == null) return;

            var type = target.GetType();
            var methods = GetOrCacheMethods(type, PostConstructCache, typeof(PostConstructAttribute));

            foreach (var method in methods)
            {
                try
                {
                    method.Invoke(target, null);
                }
                // Broad catch: MethodInfo.Invoke also throws TargetException, MethodAccessException
                // and friends directly, without wrapping. Those used to escape uncaught and with no
                // indication of which member failed.
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        $"[PostConstruct] Error invoking {method.Name} on {type.Name}",
                        (e as TargetInvocationException)?.InnerException ?? e);
                }
            }
        }

        public static void InvokeDeConstruct(object target)
        {
            if (target == null) return;

            var type = target.GetType();
            var methods = GetOrCacheMethods(type, DeConstructCache, typeof(DeConstructAttribute));

            foreach (var method in methods)
            {
                try
                {
                    method.Invoke(target, null);
                }
                catch (Exception e)
                {
                    // The player log is world-readable on desktop and Android, so the release build
                    // gets the message only — not the stack trace with its source file paths.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    UnityEngine.Debug.LogError(
                        $"[DeConstruct] Error invoking {method.Name} on {type.Name}: {e}");
#else
                    UnityEngine.Debug.LogError(
                        $"[DeConstruct] Error invoking {method.Name} on {type.Name}: {e.Message}");
#endif
                }
            }
        }

        private static MethodInfo[] GetOrCacheMethods(Type type, ConcurrentDictionary<Type, MethodInfo[]> cache, Type attributeType)
        {
            if (cache.TryGetValue(type, out var methods))
                return methods;

            // GetOrAdd with an already-computed value rather than a lambda: a lambda would have to
            // capture attributeType, allocating a closure on a path that is otherwise allocation-free.
            return cache.GetOrAdd(type, FindMethodsWithAttribute(type, attributeType));
        }

        private static MethodInfo[] FindMethodsWithAttribute(Type type, Type attributeType)
        {
            var result = new List<MethodInfo>();
            var methods = type.GetMethods(MethodFlags);

            foreach (var method in methods)
            {
                if (method.GetCustomAttribute(attributeType) == null || method.GetParameters().Length != 0)
                    continue;

                // MethodInfo.Invoke on an open generic method throws InvalidOperationException rather
                // than the TargetInvocationException the call sites unwrap, so filter it out here.
                if (method.ContainsGenericParameters)
                {
                    UnityEngine.Debug.LogError(
                        $"[Strada DI] '{type.Name}.{method.Name}' carries {attributeType.Name} but is a generic method " +
                        "definition and cannot be invoked; it will be ignored.");
                    continue;
                }

                result.Add(method);
            }

            return result.ToArray();
        }

        public static void ClearCache()
        {
            PostConstructCache.Clear();
            DeConstructCache.Clear();
        }
    }
}
