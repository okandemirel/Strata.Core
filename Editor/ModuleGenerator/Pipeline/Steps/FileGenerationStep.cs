using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Strada.Core.Editor.ModuleGenerator.Config;
using Strada.Core.Editor.ModuleGenerator.Models;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.ModuleGenerator.Pipeline.Steps
{
    /// <summary>
    /// Generates all code files for the module.
    /// </summary>
    public class FileGenerationStep : IGenerationStep
    {
        public string Name => "File Generation";
        public int Order => 30;

        private StradaGeneratorSettings _settings;

        public bool CanExecute(GenerationContext context)
        {
            return true;
        }

        private static readonly Regex ValidNamespaceRegex = new Regex(@"^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*$", RegexOptions.Compiled);

        public StepResult Execute(GenerationContext context)
        {
            _settings = StradaGeneratorSettings.GetOrCreateSettings();

            var basePath = context.Definition.FullPath;
            var name = context.Definition.ModuleName;
            var ns = context.Definition.FullNamespace;
            var components = context.Definition.Components;

            if (!ValidNamespaceRegex.IsMatch(ns))
                return StepResult.Error($"Invalid namespace '{ns}': must contain only valid C# identifier characters");

            var fileEntries = BuildFileEntries(basePath, name, ns, components, context.Definition.ModuleType);

            int filesCreated = 0;
            foreach (var entry in fileEntries)
            {
                if (entry.Enabled)
                {
                    CreateFile(entry.Path, entry.ContentGenerator(), context);
                    filesCreated++;
                }
            }

            AssetDatabase.Refresh();

            return StepResult.Ok($"Created {filesCreated} files");
        }

        private List<FileEntry> BuildFileEntries(string basePath, string name, string ns,
            ComponentSelection components, ModuleType moduleType)
        {
            return new List<FileEntry>
            {
                new FileEntry(
                    components.ModuleConfig && moduleType == ModuleType.Main,
                    $"{basePath}/{name}ModuleConfig.cs",
                    () => GenerateModuleConfig(name, ns)),
                new FileEntry(
                    components.ServiceInterface,
                    $"{basePath}/Interfaces/I{name}Service.cs",
                    () => GenerateServiceInterface(name, ns)),
                new FileEntry(
                    components.Service,
                    $"{basePath}/Services/{name}Service.cs",
                    () => GenerateService(name, ns, components.ServiceInterface)),
                new FileEntry(
                    components.Controller,
                    $"{basePath}/Controllers/{name}Controller.cs",
                    () => GenerateController(name, ns, components.ServiceInterface)),
                new FileEntry(
                    components.Model,
                    $"{basePath}/Models/{name}Model.cs",
                    () => GenerateModel(name, ns)),
                new FileEntry(
                    components.View,
                    $"{basePath}/Views/{name}View.cs",
                    () => GenerateView(name, ns)),
                new FileEntry(
                    components.EcsSystem,
                    $"{basePath}/Systems/{name}System.cs",
                    () => GenerateSystem(name, ns)),
                new FileEntry(
                    components.EcsComponent,
                    $"{basePath}/Components/{name}Component.cs",
                    () => GenerateComponent(name, ns)),
                new FileEntry(
                    components.EntityMediator,
                    $"{basePath}/Views/{name}Mediator.cs",
                    () => GenerateMediator(name, ns)),
                new FileEntry(
                    components.ConfigData,
                    $"{basePath}/Data/UnityObjects/CD_{name}.cs",
                    () => GenerateConfigData(name, ns)),
                new FileEntry(
                    components.ValueObject,
                    $"{basePath}/Data/ValueObjects/{name}Config.cs",
                    () => GenerateValueObject(name, ns)),
                new FileEntry(
                    components.Events,
                    $"{basePath}/Events/{name}Events.cs",
                    () => GenerateEvents(name, ns)),
                new FileEntry(
                    components.Signals,
                    $"{basePath}/Events/{name}Signals.cs",
                    () => GenerateSignals(name, ns)),
                new FileEntry(
                    components.RuntimeTests,
                    $"{basePath}/Tests/Runtime/{name}Tests.cs",
                    () => GenerateRuntimeTests(name, ns)),
                new FileEntry(
                    components.EditorTests,
                    $"{basePath}/Tests/Editor/{name}EditorTests.cs",
                    () => GenerateEditorTests(name, ns)),
            };
        }

        private void CreateFile(string path, string content, GenerationContext context)
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(Application.dataPath))
                throw new InvalidOperationException($"Path outside project: {fullPath}");

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, content);
            context.AddCreatedFile(fullPath);
        }

        public void Rollback(GenerationContext context)
        {
            foreach (var file in context.CreatedFiles)
            {
                if (file.EndsWith(".cs") && File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Renders the file that generation would actually write, for the preview panel.
        /// </summary>
        /// <remarks>
        /// The preview used to keep its own copy of these twelve templates, which had already
        /// drifted from what generation emits (the service base clause and the controller's
        /// injected service were unconditional in the preview but conditional here). Routing the
        /// preview through the same methods is the only way the two cannot disagree.
        ///
        /// Dispatch is on exact file names built from <paramref name="moduleName"/> rather than
        /// on suffix tests, because suffix tests are ambiguous: a module called "Inventory"
        /// produces "InventoryService.cs", which a StartsWith("I") test mistakes for the
        /// interface file.
        /// </remarks>
        internal static string GeneratePreview(string fileName, string moduleName, string ns,
            StradaGeneratorSettings settings, ComponentSelection components)
        {
            var step = new FileGenerationStep
            {
                _settings = settings ?? StradaGeneratorSettings.GetOrCreateSettings()
            };

            // A caller that does not know the selection gets the generator's common case.
            var hasServiceInterface = components?.ServiceInterface ?? true;

            if (fileName == $"{moduleName}ModuleConfig.cs") return step.GenerateModuleConfig(moduleName, ns);
            if (fileName == $"I{moduleName}Service.cs") return step.GenerateServiceInterface(moduleName, ns);
            if (fileName == $"{moduleName}Service.cs") return step.GenerateService(moduleName, ns, hasServiceInterface);
            if (fileName == $"{moduleName}Controller.cs") return step.GenerateController(moduleName, ns, hasServiceInterface);
            if (fileName == $"{moduleName}Model.cs") return step.GenerateModel(moduleName, ns);
            if (fileName == $"{moduleName}View.cs") return step.GenerateView(moduleName, ns);
            if (fileName == $"{moduleName}System.cs") return step.GenerateSystem(moduleName, ns);
            if (fileName == $"{moduleName}Component.cs") return step.GenerateComponent(moduleName, ns);
            if (fileName == $"{moduleName}Mediator.cs") return step.GenerateMediator(moduleName, ns);
            if (fileName == $"CD_{moduleName}.cs") return step.GenerateConfigData(moduleName, ns);
            if (fileName == $"{moduleName}Config.cs") return step.GenerateValueObject(moduleName, ns);
            if (fileName == $"{moduleName}Events.cs") return step.GenerateEvents(moduleName, ns);
            if (fileName == $"{moduleName}Signals.cs") return step.GenerateSignals(moduleName, ns);
            if (fileName == $"{moduleName}EditorTests.cs") return step.GenerateEditorTests(moduleName, ns);
            if (fileName == $"{moduleName}Tests.cs") return step.GenerateRuntimeTests(moduleName, ns);

            return $"// Preview not available for {fileName}";
        }

        private string WrapInNamespace(string ns, string[] usings, Action<StringBuilder> writeBody)
        {
            var sb = new StringBuilder();
            foreach (var u in usings)
                sb.AppendLine(u);
            if (usings.Length > 0)
                sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            writeBody(sb);
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenerateModuleConfig(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using UnityEngine;", "using Strada.Core.DI;", "using Strada.Core.Modules;" }, sb =>
            {
                AddSummary(sb, $"Module configuration for {name}.", 1);
                sb.AppendLine($"    [CreateAssetMenu(fileName = \"{name}ModuleConfig\", menuName = \"{name}/Module Config\")]");
                sb.AppendLine($"    public class {name}ModuleConfig : ModuleConfig");
                sb.AppendLine("    {");
                sb.AppendLine("        protected override void Configure(IModuleBuilder builder)");
                sb.AppendLine("        {");
                sb.AppendLine($"            builder.RegisterService<I{name}Service, {name}Service>();");
                sb.AppendLine($"            builder.RegisterController<{name}Controller>();");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        public override void Initialize(IServiceLocator services)");
                sb.AppendLine("        {");
                sb.AppendLine("            var container = services.Get<IContainer>();");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        public override void Shutdown()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateServiceInterface(string name, string ns)
        {
            return WrapInNamespace(ns, Array.Empty<string>(), sb =>
            {
                AddSummary(sb, $"Service interface for {name} functionality.", 1);
                sb.AppendLine($"    public interface I{name}Service");
                sb.AppendLine("    {");
                sb.AppendLine("        void Initialize();");
                sb.AppendLine("    }");
            });
        }

        private string GenerateService(string name, string ns, bool hasInterface)
        {
            var iface = hasInterface ? $" : I{name}Service" : "";
            return WrapInNamespace(ns, new[] { "using Strada.Core.DI.Attributes;", "using Strada.Core.Communication;" }, sb =>
            {
                AddSummary(sb, $"Service implementation for {name}.", 1);
                sb.AppendLine($"    public class {name}Service{iface}");
                sb.AppendLine("    {");
                sb.AppendLine("        [Inject] private readonly EventBus _eventBus;");
                sb.AppendLine();
                sb.AppendLine("        public void Initialize()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateController(string name, string ns, bool hasServiceInterface)
        {
            return WrapInNamespace(ns, new[] { "using Strada.Core.DI.Attributes;", "using Strada.Core.Communication;" }, sb =>
            {
                AddSummary(sb, $"Controller for {name} functionality.", 1);
                sb.AppendLine($"    public class {name}Controller");
                sb.AppendLine("    {");
                if (hasServiceInterface)
                {
                    sb.AppendLine($"        [Inject] private readonly I{name}Service _service;");
                }
                sb.AppendLine("        [Inject] private readonly EventBus _eventBus;");
                sb.AppendLine();
                sb.AppendLine("        public void Initialize()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        public void Tick(float deltaTime)");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateModel(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using Strada.Core.Sync;" }, sb =>
            {
                AddSummary(sb, $"Model for {name} state management.", 1);
                sb.AppendLine($"    public class {name}Model");
                sb.AppendLine("    {");
                sb.AppendLine($"        public ReactiveProperty<int> Value {{ get; }} = new(0);");
                sb.AppendLine();
                sb.AppendLine("        public void Reset()");
                sb.AppendLine("        {");
                sb.AppendLine("            Value.Value = 0;");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateView(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using UnityEngine;", "using Strada.Core.Patterns;" }, sb =>
            {
                AddSummary(sb, $"View for {name} visual representation.", 1);
                sb.AppendLine($"    public class {name}View : View");
                sb.AppendLine("    {");
                sb.AppendLine("        public override void Initialize()");
                sb.AppendLine("        {");
                sb.AppendLine("            base.Initialize();");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        protected override void OnShow()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        protected override void OnHide()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateSystem(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using Strada.Core.ECS;", "using Strada.Core.ECS.Systems;" }, sb =>
            {
                AddSummary(sb, $"ECS System for {name} processing.", 1);
                sb.AppendLine("    [SystemOrder(0)]");
                sb.AppendLine($"    public class {name}System : SystemBase");
                sb.AppendLine("    {");
                sb.AppendLine("        protected override void OnInitialize()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        protected override void OnUpdate(float deltaTime)");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        protected override void OnDispose()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateComponent(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using System.Runtime.InteropServices;", "using Strada.Core.ECS;" }, sb =>
            {
                AddSummary(sb, $"ECS Component for {name} data.", 1);
                sb.AppendLine("    [StructLayout(LayoutKind.Sequential)]");
                sb.AppendLine($"    public struct {name}Component : IComponent");
                sb.AppendLine("    {");
                sb.AppendLine("        public float Value;");
                sb.AppendLine("    }");
            });
        }

        private string GenerateMediator(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using Strada.Core.Sync;" }, sb =>
            {
                AddSummary(sb, $"Entity mediator for {name} view binding.", 1);
                sb.AppendLine($"    public class {name}Mediator : EntityMediator<{name}View>");
                sb.AppendLine("    {");
                sb.AppendLine("        protected override void OnBind()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        protected override void OnUnbind()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateConfigData(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using UnityEngine;", "using Strada.Core.Data;" }, sb =>
            {
                AddSummary(sb, $"Configuration data for {name}.", 1);
                sb.AppendLine($"    [CreateAssetMenu(fileName = \"CD_{name}\", menuName = \"{name}/Config/{name}\")]");
                sb.AppendLine($"    public class CD_{name} : ConfigData<{name}Config>");
                sb.AppendLine("    {");
                sb.AppendLine("    }");
            });
        }

        private string GenerateValueObject(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using System;", "using UnityEngine;" }, sb =>
            {
                AddSummary(sb, $"Value object for {name} configuration.", 1);
                sb.AppendLine("    [Serializable]");
                sb.AppendLine($"    public class {name}Config");
                sb.AppendLine("    {");
                sb.AppendLine("        [SerializeField] private string _id;");
                sb.AppendLine("        [SerializeField] private int _value;");
                sb.AppendLine();
                sb.AppendLine("        public string Id => _id;");
                sb.AppendLine("        public int Value => _value;");
                sb.AppendLine("    }");
            });
        }

        private string GenerateEvents(string name, string ns)
        {
            return WrapInNamespace(ns, Array.Empty<string>(), sb =>
            {
                AddSummary(sb, $"Events for {name} state changes.", 1);
                sb.AppendLine($"    public readonly struct {name}ChangedEvent");
                sb.AppendLine("    {");
                sb.AppendLine("        public readonly int Value;");
                sb.AppendLine();
                sb.AppendLine($"        public {name}ChangedEvent(int value)");
                sb.AppendLine("        {");
                sb.AppendLine("            Value = value;");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateSignals(string name, string ns)
        {
            return WrapInNamespace(ns, new[] { "using Strada.Core.ECS;" }, sb =>
            {
                AddSummary(sb, $"Signals for {name} actions.", 1);
                sb.AppendLine($"    public struct {name}Signal");
                sb.AppendLine("    {");
                sb.AppendLine("        public Entity Entity;");
                sb.AppendLine("    }");
            });
        }

        private string GenerateRuntimeTests(string name, string ns)
        {
            return WrapInNamespace($"{ns}.Tests", new[] { "using NUnit.Framework;" }, sb =>
            {
                sb.AppendLine("    [TestFixture]");
                sb.AppendLine($"    public class {name}Tests");
                sb.AppendLine("    {");
                sb.AppendLine("        [SetUp]");
                sb.AppendLine("        public void SetUp()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        [TearDown]");
                sb.AppendLine("        public void TearDown()");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void Example_Test()");
                sb.AppendLine("        {");
                sb.AppendLine("            Assert.Pass();");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private string GenerateEditorTests(string name, string ns)
        {
            return WrapInNamespace($"{ns}.Tests", new[] { "using NUnit.Framework;", "using UnityEditor;" }, sb =>
            {
                sb.AppendLine("    [TestFixture]");
                sb.AppendLine($"    public class {name}EditorTests");
                sb.AppendLine("    {");
                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void Editor_Test()");
                sb.AppendLine("        {");
                sb.AppendLine("            Assert.Pass();");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            });
        }

        private void AddSummary(StringBuilder sb, string summary, int indent)
        {
            if (_settings == null || !_settings.GenerateSummaries) return;

            var padding = new string(' ', indent * 4);
            sb.AppendLine($"{padding}/// <summary>");
            sb.AppendLine($"{padding}/// {summary}");
            sb.AppendLine($"{padding}/// </summary>");
        }

        private readonly struct FileEntry
        {
            public readonly bool Enabled;
            public readonly string Path;
            public readonly Func<string> ContentGenerator;

            public FileEntry(bool enabled, string path, Func<string> contentGenerator)
            {
                Enabled = enabled;
                Path = path;
                ContentGenerator = contentGenerator;
            }
        }
    }
}
