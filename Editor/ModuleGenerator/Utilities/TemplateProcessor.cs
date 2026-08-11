using Strada.Core.Editor.ModuleGenerator.Config;
using Strada.Core.Editor.ModuleGenerator.Models;
using Strada.Core.Editor.ModuleGenerator.Pipeline.Steps;

namespace Strada.Core.Editor.ModuleGenerator
{
    /// <summary>
    /// Renders the code preview shown in the module generator.
    /// </summary>
    /// <remarks>
    /// This is a thin forwarder to FileGenerationStep, which owns the templates. It previously
    /// carried its own copy of all twelve templates and had already drifted from the generator,
    /// so the preview showed code that would not be written.
    /// </remarks>
    public static class TemplateProcessor
    {
        /// <summary>
        /// Renders a preview without knowing which components were selected; the interface-backed
        /// variants are assumed. Prefer the overload that takes the selection.
        /// </summary>
        public static string GeneratePreview(string fileName, string moduleName, string ns, StradaGeneratorSettings settings)
        {
            return GeneratePreview(fileName, moduleName, ns, settings, null);
        }

        /// <summary>
        /// Renders exactly what generation would write for <paramref name="fileName"/>.
        /// </summary>
        public static string GeneratePreview(string fileName, string moduleName, string ns,
            StradaGeneratorSettings settings, ComponentSelection components)
        {
            return FileGenerationStep.GeneratePreview(fileName, moduleName, ns, settings, components);
        }
    }
}
