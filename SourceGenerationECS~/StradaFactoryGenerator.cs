using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Strada.SourceGeneration
{
    [Generator]
    public sealed class StradaFactoryGenerator : IIncrementalGenerator
    {
        private const string AutoRegisterAttribute = "Strada.Core.DI.Attributes.AutoRegisterAttribute";
        private const string AutoRegisterSingletonAttribute = "Strada.Core.DI.Attributes.AutoRegisterSingletonAttribute";
        private const string AutoRegisterTransientAttribute = "Strada.Core.DI.Attributes.AutoRegisterTransientAttribute";
        private const string AutoRegisterScopedAttribute = "Strada.Core.DI.Attributes.AutoRegisterScopedAttribute";

        // StradaServiceAttribute used to be listed here too, but no such type exists in the
        // package (in this or any other namespace), so it could never match.

        private const string DiagnosticCategory = "Strada.DI";

        // Every rejection path used to be a silent `return null`, so a service that could not be
        // generated simply never registered and only failed at runtime, with nothing in the
        // console pointing at the cause. Warnings (not errors) keep an existing build compiling.
        private static readonly DiagnosticDescriptor NotConstructibleRule = new(
            "STRADA001",
            "Auto-registered type cannot be constructed",
            "'{0}' carries an auto-registration attribute but is abstract or static, so no factory is generated for it",
            DiagnosticCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor NoPublicConstructorRule = new(
            "STRADA002",
            "Auto-registered type has no public constructor",
            "'{0}' carries an auto-registration attribute but declares no public instance constructor, so no factory is generated for it",
            DiagnosticCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor ValueTypeDependencyRule = new(
            "STRADA003",
            "Auto-registered type depends on a value type",
            "'{0}' has no public constructor whose parameters are all reference types ('{1}' cannot be resolved from the container), so no factory is generated for it",
            DiagnosticCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor OpenGenericRule = new(
            "STRADA004",
            "Auto-registered type is generic",
            "'{0}' is a generic type; generated factories are non-generic, so register it manually with the container builder",
            DiagnosticCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // ForAttributeWithMetadataName lets Roslyn filter by attribute name itself instead of
            // running a predicate over every attributed class in the compilation. Just as
            // important: the transform extracts everything the output needs into a value-equatable
            // model. Flowing a SyntaxNode (reference identity, no value equality) or the
            // CompilationProvider (a new object on every keystroke) into the final stage made every
            // edit anywhere a guaranteed cache miss and re-ran the whole generator.
            var auto = CreateProvider(context, AutoRegisterAttribute).Collect();
            var singleton = CreateProvider(context, AutoRegisterSingletonAttribute).Collect();
            var transient = CreateProvider(context, AutoRegisterTransientAttribute).Collect();
            var scoped = CreateProvider(context, AutoRegisterScopedAttribute).Collect();

            var all = auto.Combine(singleton).Combine(transient).Combine(scoped);

            context.RegisterSourceOutput(all, static (spc, source) =>
            {
                var (((fromAuto, fromSingleton), fromTransient), fromScoped) = source;
                Execute(spc, fromAuto, fromSingleton, fromTransient, fromScoped);
            });
        }

        private static IncrementalValuesProvider<ExtractionResult?> CreateProvider(
            IncrementalGeneratorInitializationContext context,
            string attributeMetadataName)
        {
            return context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    attributeMetadataName,
                    predicate: static (node, _) => node is ClassDeclarationSyntax,
                    transform: static (ctx, _) => ExtractServiceInfo(ctx))
                .Where(static r => r is not null);
        }

        private static void Execute(
            SourceProductionContext context,
            ImmutableArray<ExtractionResult?> fromAuto,
            ImmutableArray<ExtractionResult?> fromSingleton,
            ImmutableArray<ExtractionResult?> fromTransient,
            ImmutableArray<ExtractionResult?> fromScoped)
        {
            var services = new List<ServiceInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            Drain(context, fromAuto, services, seen);
            Drain(context, fromSingleton, services, seen);
            Drain(context, fromTransient, services, seen);
            Drain(context, fromScoped, services, seen);

            if (services.Count == 0)
                return;

            var source = GenerateSource(services);
            context.AddSource("Strada.Generated.Factories.g.cs", SourceText.From(source, Encoding.UTF8));
        }

        private static void Drain(
            SourceProductionContext context,
            ImmutableArray<ExtractionResult?> results,
            List<ServiceInfo> services,
            HashSet<string> seen)
        {
            foreach (var result in results)
            {
                if (result == null)
                    continue;

                if (result.Diagnostic != null)
                    context.ReportDiagnostic(result.Diagnostic.ToDiagnostic());

                // A type that somehow reaches two of the four providers would otherwise emit two
                // factory classes with the same name (CS0101) and register itself twice.
                if (result.Service != null && seen.Add(result.Service.TypeName))
                    services.Add(result.Service);
            }
        }

        private static ExtractionResult? ExtractServiceInfo(GeneratorAttributeSyntaxContext context)
        {
            if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Class)
                return null;

            var attributeData = context.Attributes.FirstOrDefault();
            if (attributeData?.AttributeClass == null)
                return null;

            var location = LocationInfo.From(context.TargetNode);
            var displayName = symbol.ToDisplayString();

            if (symbol.IsAbstract || symbol.IsStatic)
                return ExtractionResult.Failure(NotConstructibleRule, location, displayName);

            // The factory is emitted as a non-generic static class in namespace Strada.Generated,
            // so a type parameter borrowed from the service would not be in scope at any of the
            // `new Foo<T>(...)` / `Register<Foo<T>>()` sites.
            if (IsGenericOrNestedInGeneric(symbol))
                return ExtractionResult.Failure(OpenGenericRule, location, displayName);

            var attrName = attributeData.AttributeClass.ToDisplayString();
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

            var publicConstructors = symbol.Constructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsStatic)
                .OrderByDescending(c => c.Parameters.Length)
                .ToList();

            if (publicConstructors.Count == 0)
                return ExtractionResult.Failure(NoPublicConstructorRule, location, displayName);

            // Every parameter becomes c.Resolve<T>(), and IContainer.Resolve<T> is constrained to
            // `where T : class`. A struct, primitive or enum parameter would emit code that does
            // not compile (CS0452), so take the largest constructor that the container can
            // actually satisfy rather than blindly taking the largest one.
            IMethodSymbol? constructor = null;
            IParameterSymbol? unresolvable = null;

            foreach (var candidate in publicConstructors)
            {
                var valueTypeParameter = candidate.Parameters.FirstOrDefault(p => !p.Type.IsReferenceType);
                if (valueTypeParameter == null)
                {
                    constructor = candidate;
                    break;
                }

                unresolvable ??= valueTypeParameter;
            }

            if (constructor == null)
            {
                return ExtractionResult.Failure(
                    ValueTypeDependencyRule,
                    location,
                    displayName,
                    unresolvable!.Type.ToDisplayString());
            }

            var dependencies = constructor.Parameters
                .Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .ToArray();

            return ExtractionResult.Success(new ServiceInfo(
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                dependencies,
                lifetime,
                interfaceType,
                priority,
                registerSelf));
        }

        private static bool IsGenericOrNestedInGeneric(INamedTypeSymbol symbol)
        {
            for (INamedTypeSymbol? t = symbol; t != null; t = t.ContainingType)
            {
                if (t.Arity > 0)
                    return true;
            }

            return false;
        }

        private static string GenerateSource(List<ServiceInfo> services)
        {
            var sb = new StringBuilder();

            // Ties are broken on the type name so the emitted file is byte-identical between
            // compilations regardless of the order the four attribute providers ran in.
            var sortedServices = services
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.TypeName, StringComparer.Ordinal)
                .ToList();

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
            var factoryName = GetFactoryName(service);
            var deps = service.Dependencies;

            sb.AppendLine($"    internal static class {factoryName}");
            sb.AppendLine("    {");

            sb.AppendLine("        [MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.Append($"        internal static {service.TypeName} Create(IContainer c) => new {service.TypeName}(");

            for (int i = 0; i < deps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"c.Resolve<{deps[i]}>()");
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
                var factoryName = GetFactoryName(service);
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

        // Derives the factory class name from the fully-qualified type name, which — unlike
        // ContainingNamespace + Name — includes the containing type chain: Game.Outer.Config and
        // Game.Other.Config both report namespace "Game" and name "Config" and would otherwise
        // produce the same factory class twice (CS0101). Sanitising '.' to '_' can still merge two
        // distinct chains (My_App.Thing and My.App.Thing), so a hash of the original name is
        // appended. Example: "global::Game.Player.Foo" -> "Game_Player_Foo__1a2b3c4d__Factory".
        private static string GetFactoryName(ServiceInfo service)
        {
            const string globalPrefix = "global::";
            var fullName = service.TypeName;
            var name = fullName.StartsWith(globalPrefix, StringComparison.Ordinal)
                ? fullName.Substring(globalPrefix.Length)
                : fullName;

            var sanitized = new StringBuilder(name.Length + 20);
            foreach (var c in name)
                sanitized.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

            sanitized.Append("__");
            sanitized.Append(StableHash(fullName).ToString("x8"));
            sanitized.Append("__Factory");
            return sanitized.ToString();
        }

        // string.GetHashCode is randomised per process on .NET Core, which would make the generated
        // file differ between compilations of identical input. FNV-1a is stable.
        private static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (var c in value)
                {
                    hash ^= c;
                    hash *= 16777619u;
                }

                return hash;
            }
        }

        private enum ServiceLifetime
        {
            Transient = 0,
            Singleton = 1,
            Scoped = 2
        }

        /// <summary>
        /// Either an extracted service or the diagnostic explaining why it was rejected. Value
        /// equality is what lets Roslyn skip regeneration when nothing relevant changed.
        /// </summary>
        private sealed class ExtractionResult : IEquatable<ExtractionResult>
        {
            private ExtractionResult(ServiceInfo? service, DiagnosticInfo? diagnostic)
            {
                Service = service;
                Diagnostic = diagnostic;
            }

            public ServiceInfo? Service { get; }
            public DiagnosticInfo? Diagnostic { get; }

            public static ExtractionResult Success(ServiceInfo service) => new(service, null);

            public static ExtractionResult Failure(
                DiagnosticDescriptor descriptor,
                LocationInfo? location,
                params string[] messageArgs) => new(null, new DiagnosticInfo(descriptor, location, messageArgs));

            public bool Equals(ExtractionResult? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;
                return Equals(Service, other.Service) && Equals(Diagnostic, other.Diagnostic);
            }

            public override bool Equals(object? obj) => Equals(obj as ExtractionResult);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Service?.GetHashCode() ?? 0) * 397) ^ (Diagnostic?.GetHashCode() ?? 0);
                }
            }
        }

        private sealed class ServiceInfo : IEquatable<ServiceInfo>
        {
            public ServiceInfo(
                string typeName,
                string[] dependencies,
                ServiceLifetime lifetime,
                string? interfaceType,
                int priority,
                bool registerSelf)
            {
                TypeName = typeName;
                Dependencies = dependencies;
                Lifetime = lifetime;
                InterfaceType = interfaceType;
                Priority = priority;
                RegisterSelf = registerSelf;
            }

            public string TypeName { get; }
            public string[] Dependencies { get; }
            public ServiceLifetime Lifetime { get; }
            public string? InterfaceType { get; }
            public int Priority { get; }
            public bool RegisterSelf { get; }

            public bool Equals(ServiceInfo? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;
                // An array compares by reference by default, so the elements have to be compared
                // explicitly or every rebuild of this model looks like a change to Roslyn.
                return TypeName == other.TypeName &&
                       Lifetime == other.Lifetime &&
                       InterfaceType == other.InterfaceType &&
                       Priority == other.Priority &&
                       RegisterSelf == other.RegisterSelf &&
                       Dependencies.Length == other.Dependencies.Length &&
                       Dependencies.SequenceEqual(other.Dependencies, StringComparer.Ordinal);
            }

            public override bool Equals(object? obj) => Equals(obj as ServiceInfo);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = TypeName.GetHashCode();
                    hash = (hash * 397) ^ (int)Lifetime;
                    hash = (hash * 397) ^ (InterfaceType?.GetHashCode() ?? 0);
                    hash = (hash * 397) ^ Priority;
                    hash = (hash * 397) ^ (RegisterSelf ? 1 : 0);
                    hash = (hash * 397) ^ Dependencies.Length;
                    return hash;
                }
            }
        }

        private sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
        {
            public DiagnosticInfo(DiagnosticDescriptor descriptor, LocationInfo? location, string[] messageArgs)
            {
                Descriptor = descriptor;
                Location = location;
                MessageArgs = messageArgs;
            }

            public DiagnosticDescriptor Descriptor { get; }
            public LocationInfo? Location { get; }
            public string[] MessageArgs { get; }

            public Diagnostic ToDiagnostic() =>
                Diagnostic.Create(Descriptor, Location?.ToLocation(), MessageArgs);

            public bool Equals(DiagnosticInfo? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;
                return Equals(Descriptor, other.Descriptor) &&
                       Nullable.Equals(Location, other.Location) &&
                       MessageArgs.SequenceEqual(other.MessageArgs, StringComparer.Ordinal);
            }

            public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Descriptor.Id.GetHashCode();
                    hash = (hash * 397) ^ (Location?.GetHashCode() ?? 0);
                    foreach (var arg in MessageArgs)
                        hash = (hash * 397) ^ arg.GetHashCode();
                    return hash;
                }
            }
        }

        /// <summary>
        /// A value-equatable stand-in for <see cref="Location"/>. Holding a Location (or the
        /// SyntaxNode it came from) in the incremental model would tie the cache entry to the
        /// syntax tree instance and defeat it.
        /// </summary>
        private readonly struct LocationInfo : IEquatable<LocationInfo>
        {
            private LocationInfo(string filePath, TextSpan textSpan, LinePositionSpan lineSpan)
            {
                FilePath = filePath;
                TextSpan = textSpan;
                LineSpan = lineSpan;
            }

            public string FilePath { get; }
            public TextSpan TextSpan { get; }
            public LinePositionSpan LineSpan { get; }

            public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

            public static LocationInfo? From(SyntaxNode node)
            {
                var identifier = node is ClassDeclarationSyntax classDecl
                    ? classDecl.Identifier.GetLocation()
                    : node.GetLocation();

                if (identifier.SourceTree == null)
                    return null;

                return new LocationInfo(
                    identifier.SourceTree.FilePath,
                    identifier.SourceSpan,
                    identifier.GetLineSpan().Span);
            }

            public bool Equals(LocationInfo other) =>
                FilePath == other.FilePath && TextSpan == other.TextSpan && LineSpan == other.LineSpan;

            public override bool Equals(object? obj) => obj is LocationInfo other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = FilePath?.GetHashCode() ?? 0;
                    hash = (hash * 397) ^ TextSpan.GetHashCode();
                    hash = (hash * 397) ^ LineSpan.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
