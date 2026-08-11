using System;
using System.Collections.Generic;
using Strada.Core.Editor.ModuleGenerator.Models;
using Strada.Core.Editor.ModuleGenerator.Pipeline.Steps;
using UnityEngine;

namespace Strada.Core.Editor.ModuleGenerator.Pipeline
{
    /// <summary>
    /// Orchestrates the module generation pipeline.
    /// </summary>
    public class GenerationPipeline
    {
        private readonly List<IGenerationStep> _steps;
        private readonly List<IGenerationStep> _executedSteps;

        public GenerationPipeline()
        {
            _steps = new List<IGenerationStep>
            {
                new FolderCreationStep(),
                new AssemblyDefStep(),
                new FileGenerationStep(),
            };

            _steps.Sort((a, b) => a.Order.CompareTo(b.Order));
            _executedSteps = new List<IGenerationStep>();
        }

        public GenerationResult Execute(GenerationContext context)
        {
            _executedSteps.Clear();
            var result = new GenerationResult();

            foreach (var step in _steps)
            {
                if (!step.CanExecute(context))
                {
                    Debug.Log($"[StradaGenerator] Skipping step: {step.Name}");
                    continue;
                }

                Debug.Log($"[StradaGenerator] Executing step: {step.Name}");

                // Registered before Execute, not after: a step that throws part-way through has
                // already created folders/files, and its own Rollback is the only thing that
                // knows how to undo them.
                _executedSteps.Add(step);

                StepResult stepResult;
                try
                {
                    stepResult = step.Execute(context);
                }
                catch (Exception ex)
                {
                    // Steps throw by design (FileGenerationStep's containment check) and by
                    // accident (Directory.CreateDirectory / File.WriteAllText IO errors). Without
                    // this the exception escapes past Rollback, orphaning everything created so
                    // far and leaving the caller's generation state stuck at InProgress.
                    result.Success = false;
                    result.ErrorMessage = $"Step '{step.Name}' threw: {ex.Message}";
                    Debug.LogException(ex);
                    Rollback(context);
                    return result;
                }

                if (!stepResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Step '{step.Name}' failed: {stepResult.Message}";
                    Rollback(context);
                    return result;
                }

                result.AddStep(step.Name, stepResult.Message);
            }

            result.Success = true;
            result.CreatedFiles = new List<string>(context.CreatedFiles);
            result.CreatedFolders = new List<string>(context.CreatedFolders);

            return result;
        }

        private void Rollback(GenerationContext context)
        {
            Debug.Log("[StradaGenerator] Rolling back changes...");

            for (int i = _executedSteps.Count - 1; i >= 0; i--)
            {
                var step = _executedSteps[i];
                Debug.Log($"[StradaGenerator] Rolling back step: {step.Name}");

                // One step failing to undo itself must not prevent the earlier steps from
                // undoing theirs.
                try
                {
                    step.Rollback(context);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[StradaGenerator] Rollback of step '{step.Name}' failed: {ex.Message}");
                }
            }
        }
    }

    public class GenerationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> CreatedFiles { get; set; } = new List<string>();
        public List<string> CreatedFolders { get; set; } = new List<string>();
        public List<(string Step, string Message)> StepResults { get; } = new List<(string, string)>();

        public void AddStep(string step, string message)
        {
            StepResults.Add((step, message));
        }
    }
}
