using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Strada.Core.SourceGen
{
    [Generator]
    public class StradaDISourceGenerator : ISourceGenerator
    {
        private const string StradaServiceAttributeName = "Strada.Core.DI.StradaServiceAttribute";
        private const string InjectAttributeName = "Strada.Core.DI.InjectAttribute";

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new ServiceSyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.SyntaxReceiver is not ServiceSyntaxReceiver receiver)
                return;

            var compilation = context.Compilation;
            var services = new List<ServiceRegistration>();

            foreach (var candidateClass in receiver.CandidateClasses)
            {
                var model = compilation.GetSemanticModel(candidateClass.SyntaxTree);
                var classSymbol = model.GetDeclaredSymbol(candidateClass);

                if (classSymbol == null)
                    continue;

                var attribute = classSymbol.GetAttributes()
                    .FirstOrDefault(ad => ad.AttributeClass?.ToDisplayString() == StradaServiceAttributeName);

                if (attribute == null)
                    continue;

                var lifetime = GetLifetime(attribute);
                var interfaceType = GetInterfaceType(attribute, classSymbol);
                var constructor = GetInjectableConstructor(classSymbol);

                services.Add(new ServiceRegistration
                {
                    ImplementationType = classSymbol.ToDisplayString(),
                    InterfaceType = interfaceType?.ToDisplayString() ?? classSymbol.ToDisplayString(),
                    Lifetime = lifetime,
                    Constructor = constructor
                });
            }

            if (services.Count == 0)
                return;

            var source = GenerateContainerSource(services);
            context.AddSource("StradaGeneratedContainer.g.cs", SourceText.From(source, Encoding.UTF8));
        }

        private string GetLifetime(AttributeData attribute)
        {
            if (attribute.ConstructorArguments.Length > 0)
            {
                return attribute.ConstructorArguments[0].Value?.ToString() ?? "Transient";
            }
            return "Transient";
        }

        private INamedTypeSymbol? GetInterfaceType(AttributeData attribute, INamedTypeSymbol classSymbol)
        {
            var interfaceTypeArg = attribute.NamedArguments
                .FirstOrDefault(kvp => kvp.Key == "InterfaceType")
                .Value;

            if (interfaceTypeArg.Value is INamedTypeSymbol interfaceSymbol)
                return interfaceSymbol;

            var interfaces = classSymbol.AllInterfaces;
            return interfaces.Length > 0 ? interfaces[0] : null;
        }

        private ConstructorInfo? GetInjectableConstructor(INamedTypeSymbol classSymbol)
        {
            var constructors = classSymbol.Constructors
                .Where(c => !c.IsStatic)
                .ToList();

            var injectConstructor = constructors.FirstOrDefault(c =>
                c.GetAttributes().Any(ad => ad.AttributeClass?.ToDisplayString() == InjectAttributeName));

            var targetConstructor = injectConstructor ?? constructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();

            if (targetConstructor == null)
                return null;

            return new ConstructorInfo
            {
                Parameters = targetConstructor.Parameters.Select(p => new ParameterInfo
                {
                    Name = p.Name,
                    Type = p.Type.ToDisplayString()
                }).ToList()
            };
        }

        private string GenerateContainerSource(List<ServiceRegistration> services)
        {
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine("namespace Strada.Core.DI.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public sealed class StradaGeneratedContainer");
            sb.AppendLine("    {");
            sb.AppendLine("        private readonly Dictionary<Type, Func<object>> _factories;");
            sb.AppendLine("        private readonly Dictionary<Type, object> _singletons;");
            sb.AppendLine();
            sb.AppendLine("        public StradaGeneratedContainer()");
            sb.AppendLine("        {");
            sb.AppendLine("            _factories = new Dictionary<Type, Func<object>>();");
            sb.AppendLine("            _singletons = new Dictionary<Type, object>();");
            sb.AppendLine("            RegisterServices();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void RegisterServices()");
            sb.AppendLine("        {");

            foreach (var service in services)
            {
                var factoryMethod = GenerateFactoryMethod(service);
                sb.AppendLine($"            _factories[typeof({service.InterfaceType})] = () => {factoryMethod};");

                if (service.Lifetime == "Singleton")
                {
                    sb.AppendLine($"            _singletons[typeof({service.InterfaceType})] = {factoryMethod};");
                }
            }

            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public T Resolve<T>() where T : class");
            sb.AppendLine("        {");
            sb.AppendLine("            var type = typeof(T);");
            sb.AppendLine();
            sb.AppendLine("            if (_singletons.TryGetValue(type, out var singleton))");
            sb.AppendLine("                return (T)singleton;");
            sb.AppendLine();
            sb.AppendLine("            if (_factories.TryGetValue(type, out var factory))");
            sb.AppendLine("                return (T)factory();");
            sb.AppendLine();
            sb.AppendLine("            throw new InvalidOperationException($\"Service {type.Name} is not registered.\");");
            sb.AppendLine("        }");

            foreach (var service in services)
            {
                sb.AppendLine();
                sb.AppendLine($"        public {service.InterfaceType} Resolve{GetSafeName(service.InterfaceType)}()");
                sb.AppendLine("        {");

                if (service.Lifetime == "Singleton")
                {
                    sb.AppendLine($"            return ({service.InterfaceType})_singletons[typeof({service.InterfaceType})];");
                }
                else
                {
                    sb.AppendLine($"            return ({service.InterfaceType})_factories[typeof({service.InterfaceType})]();");
                }

                sb.AppendLine("        }");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private string GenerateFactoryMethod(ServiceRegistration service)
        {
            if (service.Constructor == null || service.Constructor.Parameters.Count == 0)
            {
                return $"new {service.ImplementationType}()";
            }

            var parameters = string.Join(", ",
                service.Constructor.Parameters.Select(p => $"Resolve<{p.Type}>()"));

            return $"new {service.ImplementationType}({parameters})";
        }

        private string GetSafeName(string typeName)
        {
            // Map any non-identifier character to '_' so the result is a valid C# identifier.
            // Covers '.', '<', '>', ',', ' ', '+', '[', ']', '&', and globally-qualified
            // prefixes that may appear in fully-qualified display names.
            if (string.IsNullOrEmpty(typeName)) return "_";
            var chars = new System.Text.StringBuilder(typeName.Length);
            foreach (var c in typeName)
            {
                chars.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }
            // Ensure the identifier doesn't start with a digit.
            if (chars.Length > 0 && char.IsDigit(chars[0])) chars.Insert(0, '_');
            return chars.ToString();
        }

        private class ServiceSyntaxReceiver : ISyntaxReceiver
        {
            public List<ClassDeclarationSyntax> CandidateClasses { get; } = new();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (syntaxNode is ClassDeclarationSyntax classDeclaration &&
                    classDeclaration.AttributeLists.Count > 0)
                {
                    CandidateClasses.Add(classDeclaration);
                }
            }
        }

        private class ServiceRegistration
        {
            public string ImplementationType { get; set; } = string.Empty;
            public string InterfaceType { get; set; } = string.Empty;
            public string Lifetime { get; set; } = string.Empty;
            public ConstructorInfo? Constructor { get; set; }
        }

        private class ConstructorInfo
        {
            public List<ParameterInfo> Parameters { get; set; } = new();
        }

        private class ParameterInfo
        {
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
        }
    }
}
