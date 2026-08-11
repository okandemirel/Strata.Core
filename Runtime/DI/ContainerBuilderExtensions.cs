using System;
using System.Collections.Generic;
using System.Reflection;
using Strada.Core.DI.AutoBinding;
using UnityEngine;

namespace Strada.Core.DI
{
    public static class ContainerBuilderExtensions
    {
        public static IContainerBuilder RegisterAutoBindings(this IContainerBuilder builder)
        {
            return RegisterAutoBindings(builder, null, null, false);
        }

        public static IContainerBuilder RegisterAutoBindings(
            this IContainerBuilder builder,
            IReadOnlyList<string> includePatterns,
            IReadOnlyList<string> excludePatterns,
            bool forceRuntime = false)
        {
            if (!forceRuntime && TryUseSourceGenerated(builder))
                return builder;

            RuntimeAutoBindingScanner.RegisterAll(builder, includePatterns, excludePatterns);
            return builder;
        }

        public static IContainerBuilder RegisterAutoBindingsRuntime(
            this IContainerBuilder builder,
            IReadOnlyList<string> includePatterns = null,
            IReadOnlyList<string> excludePatterns = null)
        {
            RuntimeAutoBindingScanner.RegisterAll(builder, includePatterns, excludePatterns);
            return builder;
        }

        private static bool TryUseSourceGenerated(IContainerBuilder builder)
        {
            MethodInfo registerMethod;

            try
            {
                var registryType =
                    Type.GetType("Strada.Generated.StradaGeneratedRegistry, Assembly-CSharp") ??
                    Type.GetType("Strada.Generated.StradaGeneratedRegistry, Assembly-CSharp-firstpass") ??
                    Type.GetType("Strada.Generated.StradaGeneratedRegistry");

                if (registryType == null)
                    return false;

                var isGenProp = registryType.GetProperty("IsSourceGenerated");
                if (isGenProp != null && !(bool)isGenProp.GetValue(null))
                    return false;

                registerMethod = registryType.GetMethod("RegisterAll");
                if (registerMethod == null)
                    return false;
            }
            catch (TypeLoadException ex)
            {
                Debug.LogWarning($"Strada generated registry not found, skipping auto-registration. {ex.Message}");
                return false;
            }
            catch (ReflectionTypeLoadException ex)
            {
                Debug.LogWarning($"Strada generated registry failed to load, skipping auto-registration. {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Strada generated registry not found, skipping auto-registration. {ex.Message}");
                return false;
            }

            // Deliberately outside the catch above. RegisterAll mutates the caller's builder in
            // place, so a mid-way failure leaves it partially populated; swallowing that and
            // falling through to the runtime scanner layers a second registration pass on top of
            // the wreckage and reports "registry not found", which is not what happened.
            try
            {
                registerMethod.Invoke(null, new object[] { builder });
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(
                    "Strada generated registry RegisterAll failed; the container builder is now partially populated.",
                    ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Strada generated registry RegisterAll could not be invoked; the container builder may be partially populated.",
                    ex);
            }

            return true;
        }

        public static int GetAutoBindingCount()
        {
            try
            {
                var registryType =
                    Type.GetType("Strada.Generated.StradaGeneratedRegistry, Assembly-CSharp") ??
                    Type.GetType("Strada.Generated.StradaGeneratedRegistry");

                if (registryType != null)
                {
                    var countProp = registryType.GetProperty("ServiceCount");
                    if (countProp != null)
                        return (int)countProp.GetValue(null);
                }
            }
            catch (TypeLoadException ex)
            {
                Debug.LogWarning($"Failed to load generated registry type for binding count. {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to retrieve auto-binding count from generated registry. {ex.Message}");
            }

            return RuntimeAutoBindingScanner.GetCachedCount();
        }
    }
}
