using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Strada.Core.DI.Attributes;

namespace Strada.Core.DI
{
    public static class InjectionProcessor
    {
        private static readonly ConcurrentDictionary<Type, TypeInjectionInfo> _cache = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Inject(object target, IContainer container)
        {
            var type = target.GetType();
            var info = GetOrCreateInfo(type);

            InjectMethods(target, info.Methods, container);
            InjectProperties(target, info.Properties, container);
            InjectFields(target, info.Fields, container);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InjectInto<T>(T target, IContainer container) where T : class
            => Inject(target, container);

        private static TypeInjectionInfo GetOrCreateInfo(Type type)
        {
            return _cache.GetOrAdd(type, BuildInjectionInfo);
        }

        private static TypeInjectionInfo BuildInjectionInfo(Type type)
        {
            var methods = new List<MethodInjectionInfo>(4);
            var properties = new List<PropertyInfo>(4);
            var fields = new List<FieldInfo>(4);

            // FRAMEWORK DESIGN: BindingFlags.NonPublic is intentional. Strada's DI lets the
            // [Inject] attribute target private/protected fields, properties, and methods so
            // services can keep their dependencies out of their public API. Removing
            // NonPublic would force every dependency to be public, which is at odds with
            // standard DI conventions.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var method in type.GetMethods(flags))
            {
                if (method.GetCustomAttribute<InjectAttribute>() == null)
                    continue;

                var parameters = method.GetParameters();
                var paramTypes = new Type[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                    paramTypes[i] = parameters[i].ParameterType;

                methods.Add(new MethodInjectionInfo(method, paramTypes));
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (property.GetCustomAttribute<InjectAttribute>() == null)
                    continue;

                if (property.CanWrite)
                    properties.Add(property);
            }

            foreach (var field in type.GetFields(flags))
            {
                if (field.GetCustomAttribute<InjectAttribute>() == null)
                    continue;

                fields.Add(field);
            }

            return new TypeInjectionInfo(methods.ToArray(), properties.ToArray(), fields.ToArray());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InjectMethods(object target, MethodInjectionInfo[] methods, IContainer container)
        {
            for (int i = 0; i < methods.Length; i++)
            {
                ref var method = ref methods[i];
                var args = ResolveParameters(method.ParameterTypes, container);
                method.Method.Invoke(target, args);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InjectProperties(object target, PropertyInfo[] properties, IContainer container)
        {
            for (int i = 0; i < properties.Length; i++)
            {
                var prop = properties[i];
                var value = container.Resolve(prop.PropertyType);

                if (value != null && !prop.PropertyType.IsAssignableFrom(value.GetType()))
                {
                    UnityEngine.Debug.LogWarning(
                        $"[InjectionProcessor] Skipping property '{prop.Name}' on '{target.GetType().Name}': " +
                        $"resolved type '{value.GetType().Name}' is not assignable to '{prop.PropertyType.Name}'.");
                    continue;
                }

                prop.SetValue(target, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InjectFields(object target, FieldInfo[] fields, IContainer container)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var value = container.Resolve(field.FieldType);

                if (value != null && !field.FieldType.IsAssignableFrom(value.GetType()))
                {
                    UnityEngine.Debug.LogWarning(
                        $"[InjectionProcessor] Skipping field '{field.Name}' on '{target.GetType().Name}': " +
                        $"resolved type '{value.GetType().Name}' is not assignable to '{field.FieldType.Name}'.");
                    continue;
                }

                field.SetValue(target, value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object[] ResolveParameters(Type[] types, IContainer container)
        {
            if (types.Length == 0)
                return Array.Empty<object>();

            var args = new object[types.Length];
            for (int i = 0; i < types.Length; i++)
                args[i] = container.Resolve(types[i]);

            return args;
        }

        public static void ClearCache()
        {
            _cache.Clear();
        }

        private readonly struct TypeInjectionInfo
        {
            public readonly MethodInjectionInfo[] Methods;
            public readonly PropertyInfo[] Properties;
            public readonly FieldInfo[] Fields;

            public TypeInjectionInfo(MethodInjectionInfo[] methods, PropertyInfo[] properties, FieldInfo[] fields)
            {
                Methods = methods;
                Properties = properties;
                Fields = fields;
            }
        }

        private struct MethodInjectionInfo
        {
            public readonly MethodInfo Method;
            public readonly Type[] ParameterTypes;

            public MethodInjectionInfo(MethodInfo method, Type[] parameterTypes)
            {
                Method = method;
                ParameterTypes = parameterTypes;
            }
        }
    }
}
