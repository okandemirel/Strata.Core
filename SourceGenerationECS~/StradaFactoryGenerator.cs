using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Strada.SourceGeneration
{
    [Generator]
    public sealed class StradaFactoryGenerator : IIncrementalGenerator
    {
        private const string StradaServiceAttribute = "Strada.Core.DI.Attributes.StradaServiceAttribute";
        private const string AutoRegisterAttribute = "Strada.Core.DI.Attributes.AutoRegisterAttribute";
        private const string AutoRegisterSingletonAttribute = "Strada.Core.DI.Attributes.AutoRegisterSingletonAttribute";
        private const string AutoRegisterTransientAttribute = "Strada.Core.DI.Attributes.AutoRegisterTransientAttribute";
        private const string AutoRegisterScopedAttribute = "Strada.Core.DI.Attributes.AutoRegisterScopedAttribute";

        private static readonly HashSet<string> SupportedAttributes = new()
        {
            StradaServiceAttribute,
            AutoRegisterAttribute,
            AutoRegisterSingletonAttribute,
            AutoRegisterTransientAttribute,
            AutoRegisterScopedAttribute
        };

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var classDeclarations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateClass(s),
                    transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
                .Where(static m => m is not null);

            var compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());

            context.RegisterSourceOutput(compilationAndClasses,
                static (spc, source) => Execute(source.Left, source.Right!, spc));
        }

        private static bool IsCandidateClass(SyntaxNode node)
        {
            return node is ClassDeclarationSyntax classDecl &&
                   classDecl.AttributeLists.Count > 0;
        }

        private static ClassDeclarationSyntax? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
        {
            var classDecl = (ClassDeclarationSyntax)context.Node;

            foreach (var attributeList in classDecl.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var symbol = context.SemanticModel.GetSymbolInfo(attribute).Symbol;
                    if (symbol is not IMethodSymbol methodSymbol)
                        continue;

                    var containingType = methodSymbol.ContainingType;
                    if (SupportedAttributes.Contains(containingType.ToDisplayString()))
                        return classDecl;
                }
            }

            return null;
        }

        private static void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax?> classes, SourceProductionContext context)
        {
            if (classes.IsDefaultOrEmpty)
                return;

            var distinctClasses = classes.Where(c => c is not null).Distinct().ToList();
            if (distinctClasses.Count == 0)
                return;

            var serviceInfos = new List<ServiceInfo>();

            foreach (var classDecl in distinctClasses)
            {
                var model = compilation.GetSemanticModel(classDecl!.SyntaxTree);
                var symbol = model.GetDeclaredSymbol(classDecl);
                if (symbol is not INamedTypeSymbol namedSymbol)
                    continue;

                var info = ExtractServiceInfo(namedSymbol);
                if (info != null)
                    serviceInfos.Add(info);
            }

            if (serviceInfos.Count == 0)
                return;

            var source = GenerateSource(serviceInfos);
            context.AddSource("Strada.Generated.Factories.g.cs", SourceText.From(source, Encoding.UTF8));
        }

        private static ServiceInfo? ExtractServiceInfo(INamedTypeSymbol symbol)
        {
            if (symbol.IsAbstract || symbol.IsStatic)
                return null;

            var attributeData = symbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass != null &&
                                     SupportedAttributes.Contains(a.AttributeClass.ToDisplayString()));

            if (attributeData == null)
                return null;

            var attrName = attributeData.AttributeClass!.ToDisplayString();
            var lifetime = ServiceLifetime.Transient;
            string? interfaceType = null;
            int priority = 0;
            bool registerSelf = false;

            if (attrName == AutoRegisterSingletonAttribute)
                lifetime = ServiceLifetime.Singleton;
            else if (attrName == AutoRegisterTransientAttribute)
                lifetime = ServiceLifetime.Transient;
            else if (attrName == AutoRegisterScopedAttribute)
                lifetime = ServiceLifetime.Scoped;
            else if (attributeData.ConstructorArguments.Length > 0)
            {
                var lifetimeArg = attributeData.ConstructorArguments[0];
                if (lifetimeArg.Value is int lifetimeInt)
                    lifetime = (ServiceLifetime)lifetimeInt;
            }

            foreach (var namedArg in attributeData.NamedArguments)
            {
                switch (namedArg.Key)
                {
                    case "InterfaceType" when namedArg.Value.Value is INamedTypeSymbol interfaceSymbol:
                        interfaceType = interfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        break;
                    case "As" when namedArg.Value.Value is INamedTypeSymbol asSymbol:
                        interfaceType = asSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        break;
                    case "Priority" when namedArg.Value.Value is int p:
                        priority = p;
                        break;
                    case "RegisterSelf" when namedArg.Value.Value is bool rs:
                        registerSelf = rs;
                        break;
                }
            }

            var constructor = symbol.Constructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsStatic)
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault();

            if (constructor == null)
                return null;

            var dependencies = constructor.Parameters
                .Select(p => new DependencyInfo
                {
                    TypeName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ParameterName = p.Name
                })
                .ToList();

            return new ServiceInfo
            {
                TypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                ClassName = symbol.Name,
                Namespace = symbol.ContainingNamespace.ToDisplayString(),
                Dependencies = dependencies,
                Lifetime = lifetime,
                InterfaceType = interfaceType,
                Priority = priority,
                RegisterSelf = registerSelf
            };
        }

        private static string GenerateSource(List<ServiceInfo> services)
        {
            var sb = new StringBuilder();

            var sortedServices = services.OrderBy(s => s.Priority).ToList();

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Strada DI Source Generator - Ultra-fast compile-time factory generation");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("#pragma warning disable CS8603");
            sb.AppendLine("#pragma warning disable CS8604");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine("using Strada.Core.DI;");
            sb.AppendLine();
            sb.AppendLine("namespace Strada.Generated");
            sb.AppendLine("{");

            foreach (var service in sortedServices)
            {
                GenerateFactory(sb, service);
            }

            GenerateRegistry(sb, sortedServices);
            GenerateInitializer(sb, sortedServices);

            sb.AppendLine("}");

            return sb.ToString();
        }

        private static void GenerateFactory(StringBuilder sb, ServiceInfo service)
        {
            var factoryName = $"{service.ClassName}__Factory";
            var deps = service.Dependencies;

            sb.AppendLine($"    internal static class {factoryName}");
            sb.AppendLine("    {");

            sb.AppendLine("        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.Append($"        internal static {service.TypeName} Create(IContainer c) => new {service.TypeName}(");

            for (int i = 0; i < deps.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"c.Resolve<{deps[i].TypeName}>()");
            }

            sb.AppendLine(");");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        private static void GenerateRegistry(StringBuilder sb, List<ServiceInfo> services)
        {
            sb.AppendLine("    public static class StradaGeneratedRegistry");
            sb.AppendLine("    {");
            sb.AppendLine("        public static int ServiceCount => " + services.Count + ";");
            sb.AppendLine("        public static bool IsSourceGenerated => true;");
            sb.AppendLine();

            sb.AppendLine("        public static void RegisterAll(IContainerBuilder builder)");
            sb.AppendLine("        {");

            foreach (var service in services)
            {
                var lifetime = service.Lifetime switch
                {
                    ServiceLifetime.Singleton => "Lifetime.Singleton",
                    ServiceLifetime.Scoped => "Lifetime.Scoped",
                    _ => "Lifetime.Transient"
                };

                if (!string.IsNullOrEmpty(service.InterfaceType))
                {
                    sb.AppendLine($"            builder.Register<{service.InterfaceType}, {service.TypeName}>({lifetime});");

                    if (service.RegisterSelf)
                    {
                        sb.AppendLine($"            builder.Register<{service.TypeName}>({lifetime});");
                    }
                }
                else
                {
                    sb.AppendLine($"            builder.Register<{service.TypeName}>({lifetime});");
                }
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        private static void GenerateInitializer(StringBuilder sb, List<ServiceInfo> services)
        {
            sb.AppendLine("    internal static class StradaGeneratedInitializer");
            sb.AppendLine("    {");
            sb.AppendLine("        private static bool _initialized;");
            sb.AppendLine();
            sb.AppendLine("        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]");
            sb.AppendLine("        internal static void Initialize()");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_initialized) return;");
            sb.AppendLine("            _initialized = true;");
            sb.AppendLine();

            foreach (var service in services)
            {
                var factoryName = $"{service.ClassName}__Factory";
                sb.AppendLine($"            DirectFactory<{service.TypeName}>.Register({factoryName}.Create);");

                if (!string.IsNullOrEmpty(service.InterfaceType))
                {
                    sb.AppendLine($"            DirectFactory<{service.InterfaceType}>.Register({factoryName}.Create);");
                }
            }

            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        internal static void Reset()");
            sb.AppendLine("        {");
            sb.AppendLine("            _initialized = false;");

            foreach (var service in services)
            {
                sb.AppendLine($"            DirectFactory<{service.TypeName}>.Clear();");
                if (!string.IsNullOrEmpty(service.InterfaceType))
                {
                    sb.AppendLine($"            DirectFactory<{service.InterfaceType}>.Clear();");
                }
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        private enum ServiceLifetime
        {
            Transient = 0,
            Singleton = 1,
            Scoped = 2
        }

        private sealed class ServiceInfo
        {
            public string TypeName { get; set; } = "";
            public string ClassName { get; set; } = "";
            public string Namespace { get; set; } = "";
            public List<DependencyInfo> Dependencies { get; set; } = new();
            public ServiceLifetime Lifetime { get; set; }
            public string? InterfaceType { get; set; }
            public int Priority { get; set; }
            public bool RegisterSelf { get; set; }
        }

        private sealed class DependencyInfo
        {
            public string TypeName { get; set; } = "";
            public string ParameterName { get; set; } = "";
        }
    }
}
