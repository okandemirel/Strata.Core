using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Strada.Core.Editor.Headless
{
    /// <summary>
    /// Assembles a Unity scene from a declarative spec with no Editor open.
    ///
    /// Every gate the assistant has for code — compile it, read the errors, fix
    /// them — stops at the file boundary. A run can produce nine modules, fifty
    /// C# files and sixteen test assemblies, all compiling, and still deliver a
    /// library rather than a game: no scene, no assets, no bootstrapper wiring.
    /// That was measured on a 104-minute run. The usual explanation is that the
    /// Unity Editor was closed, but the bridge could not have done it either:
    /// of its eighty operations none sets a serialized field, so
    /// GameBootstrapper._gameConfig was never assignable by any tool. The
    /// missing thing was a verb, not a connection.
    ///
    /// Invoked as:
    ///   Unity -batchmode -nographics -projectPath P \
    ///     -executeMethod Strada.Core.Editor.Headless.StradaSceneBuilder.Build \
    ///     -stradaSpec spec.json -stradaResult result.json -logFile build.log
    ///
    /// Never with -quit: this method owns its own exit code, and that is the
    /// only thing that makes the exit code mean anything.
    /// </summary>
    public static class StradaSceneBuilder
    {
        private const int ExitOk = 0;
        private const int ExitSpecError = 10;
        private const int ExitPreflightError = 11;
        private const int ExitAssemblyError = 12;
        private const int ExitVerificationError = 13;

        private static readonly List<string> Created = new List<string>();
        private static readonly List<string> Assigned = new List<string>();
        private static readonly List<string> Problems = new List<string>();

        public static void Build()
        {
            // Batch mode prints a full stack trace under every Debug.Log; a
            // single run reaches megabytes of log without this.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);

            string specPath = ReadArg("-stradaSpec");
            string resultPath = ReadArg("-stradaResult");
            int code;

            try
            {
                code = Run(specPath);
            }
            catch (Exception e)
            {
                Problems.Add($"{e.GetType().Name}: {e.Message}");
                code = ExitAssemblyError;
            }

            WriteResult(resultPath, code);
            // Exit is immediate and never returns — everything must already be
            // flushed by the time we get here.
            EditorApplication.Exit(code);
        }

        private static int Run(string specPath)
        {
            if (string.IsNullOrEmpty(specPath) || !File.Exists(specPath))
            {
                Problems.Add($"spec not found at '{specPath}' (pass -stradaSpec <path>)");
                return ExitSpecError;
            }

            SceneSpec spec;
            try
            {
                spec = JsonUtility.FromJson<SceneSpec>(File.ReadAllText(specPath));
            }
            catch (Exception e)
            {
                Problems.Add($"spec is not valid JSON: {e.Message}");
                return ExitSpecError;
            }
            if (spec == null || spec.scene == null || string.IsNullOrEmpty(spec.scene.path))
            {
                Problems.Add("spec has no scene.path");
                return ExitSpecError;
            }

            // ── Preflight ────────────────────────────────────────────────────
            // A .cs file holding more than one type binds only its first type to
            // a MonoScript; the rest get m_Script: {fileID: 0} with no error from
            // AddComponent, from SaveScene or from the AssetDatabase, and are
            // null at runtime. Refusing here beats building a plausible corpse.
            var types = new Dictionary<string, Type>();
            foreach (var name in AllTypeNames(spec))
            {
                var t = FindType(name);
                if (t == null)
                {
                    Problems.Add($"type not found: {name}");
                    continue;
                }
                types[name] = t;
            }
            if (Problems.Count > 0) return ExitPreflightError;

            // ── The scene comes first ────────────────────────────────────────
            // NewScene(..., Single) unloads unused assets. Anything created
            // before it is destroyed, its handle becomes Unity's fake-null, and
            // assigning that writes {fileID: 0} without throwing or logging.
            var scene = spec.scene.mode == "default"
                ? EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ── Assets ───────────────────────────────────────────────────────
            var assetPaths = new Dictionary<string, string>();
            foreach (var a in spec.assets)
            {
                if (!types.TryGetValue(a.type, out var t)) continue;
                EnsureDirectory(a.path);
                var instance = ScriptableObject.CreateInstance(t);
                AssetDatabase.CreateAsset(instance, a.path);
                assetPaths[a.id] = a.path;
                Created.Add(a.path);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // ── Objects and components ───────────────────────────────────────
            var objects = new Dictionary<string, GameObject>();
            foreach (var o in spec.objects)
            {
                var go = new GameObject(string.IsNullOrEmpty(o.name) ? o.id : o.name);
                objects[o.id] = go;
                Created.Add($"GameObject:{go.name}");
            }
            foreach (var o in spec.objects)
            {
                if (string.IsNullOrEmpty(o.parent)) continue;
                if (objects.TryGetValue(o.parent, out var parent) && objects.TryGetValue(o.id, out var child))
                    child.transform.SetParent(parent.transform);
            }

            // Keyed by position, not by type name: an object can legitimately
            // carry two components of the same type, and keying by type made the
            // second overwrite the first — every field then landed on one
            // instance while the other stayed at its defaults.
            var components = new Dictionary<string, Component>();
            foreach (var o in spec.objects)
            {
                if (!objects.TryGetValue(o.id, out var go)) continue;
                for (var ci = 0; ci < o.components.Count; ci++)
                {
                    var c = o.components[ci];
                    if (!types.TryGetValue(c.type, out var t)) continue;
                    var comp = go.AddComponent(t);
                    components[ComponentKey(o.id, ci)] = comp;
                }
            }

            // ── Wiring ───────────────────────────────────────────────────────
            foreach (var a in spec.assets)
            {
                if (!assetPaths.TryGetValue(a.id, out var path)) continue;
                var target = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (target == null) { Problems.Add($"asset did not import: {path}"); continue; }
                ApplyFields(target, a.fields, assetPaths, objects, null, $"{a.type}", "reference");
            }

            foreach (var o in spec.objects)
            {
                for (var ci = 0; ci < o.components.Count; ci++)
                {
                    var c = o.components[ci];
                    if (!components.TryGetValue(ComponentKey(o.id, ci), out var comp)) continue;
                    ApplyFields(comp, c.fields, assetPaths, objects, null, $"{o.id}.{c.type}", "reference");
                }
            }

            // ── Prefabs ──────────────────────────────────────────────────────
            // After the component fields are applied, so the saved prefab
            // carries its wiring instead of an empty shell; and before the
            // second wiring pass, because a "prefab" reference cannot resolve
            // to an asset that does not exist yet.
            var prefabPaths = new Dictionary<string, string>();
            foreach (var o in spec.objects)
            {
                if (string.IsNullOrEmpty(o.prefabPath)) continue;
                if (!objects.TryGetValue(o.id, out var go)) continue;

                EnsureDirectory(o.prefabPath);
                var prefabAsset = PrefabUtility.SaveAsPrefabAsset(go, o.prefabPath, out var ok);
                if (!ok || prefabAsset == null)
                {
                    Problems.Add($"prefab did not save: {o.prefabPath}");
                    continue;
                }
                prefabPaths[o.id] = o.prefabPath;
                Created.Add(o.prefabPath);
            }
            if (prefabPaths.Count > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            // ── Wiring, pass two: references to the prefab assets ─────────────
            foreach (var a in spec.assets)
            {
                if (!assetPaths.TryGetValue(a.id, out var path)) continue;
                if (!HasPrefabField(a.fields)) continue;
                var target = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (target == null) continue;
                ApplyFields(target, a.fields, assetPaths, objects, prefabPaths, $"{a.type}", "prefab");
            }
            foreach (var o in spec.objects)
            {
                for (var ci = 0; ci < o.components.Count; ci++)
                {
                    var c = o.components[ci];
                    if (!HasPrefabField(c.fields)) continue;
                    if (!components.TryGetValue(ComponentKey(o.id, ci), out var comp)) continue;
                    if (comp == null) continue;
                    ApplyFields(comp, c.fields, assetPaths, objects, prefabPaths, $"{o.id}.{c.type}", "prefab");
                }
            }

            // 3) Re-save every prefab after the prefab pass. A prefab saved
            // before it is idempotent; one whose component points at ANOTHER
            // prefab is not — that field is assigned after the first save, so
            // without this the reference exists only on the scene instance and
            // the prefab on disk keeps {fileID: 0}.
            foreach (var o in spec.objects)
            {
                if (string.IsNullOrEmpty(o.prefabPath)) continue;
                if (!objects.TryGetValue(o.id, out var go) || go == null) continue;
                if (!o.components.Exists(c => HasPrefabField(c.fields))) continue;

                PrefabUtility.SaveAsPrefabAsset(go, o.prefabPath, out var resaved);
                if (!resaved) Problems.Add($"prefab did not re-save after wiring: {o.prefabPath}");
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // A template the game spawns at runtime must not also be sitting in
            // the scene, or the first frame has two of it.
            var removedAny = false;
            foreach (var o in spec.objects)
            {
                if (string.IsNullOrEmpty(o.prefabPath) || o.keepInScene) continue;
                if (!objects.TryGetValue(o.id, out var go) || go == null) continue;
                UnityEngine.Object.DestroyImmediate(go);
                removedAny = true;
                Created.Add($"removed from scene (prefab only): {o.name ?? o.id}");
            }

            // A parent prefab was serialized while its children were all still
            // in the scene, so a child marked keepInScene: false was written
            // into the parent's .prefab and only removed from the scene. The
            // invariant it exists to keep — "a template the game spawns must not
            // also be sitting there, or the first frame has two of it" — was
            // then broken the moment the parent was instantiated. Re-save the
            // survivors so every prefab matches the final hierarchy.
            if (removedAny)
            {
                foreach (var o in spec.objects)
                {
                    if (string.IsNullOrEmpty(o.prefabPath)) continue;
                    if (!objects.TryGetValue(o.id, out var go) || go == null) continue;

                    PrefabUtility.SaveAsPrefabAsset(go, o.prefabPath, out var resaved);
                    if (!resaved)
                        Problems.Add($"prefab did not re-save after removing a template: {o.prefabPath}");
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            // ── Save ─────────────────────────────────────────────────────────
            EnsureDirectory(spec.scene.path);
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, spec.scene.path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (!saved)
            {
                Problems.Add($"scene did not save: {spec.scene.path}");
                return ExitAssemblyError;
            }
            Created.Add(spec.scene.path);

            if (spec.scene.addToBuildSettings) AddToBuildSettings(spec.scene.path);

            // ── Read back from disk ──────────────────────────────────────────
            // Not by reopening the scene in this process: a stale in-memory
            // instance id still resolves there, so an in-process assertion can
            // pass on a scene a fresh process cannot even load.
            VerifyOnDisk(spec);
            return Problems.Count == 0 ? ExitOk : ExitVerificationError;
        }

        /// <summary>
        /// Assign fields, re-resolving every reference immediately before use.
        ///
        /// Nothing is held across a scene or asset operation, because a handle
        /// that survives one becomes fake-null and assigns as {fileID: 0}.
        /// </summary>
        /// <summary>
        /// Assign the fields of one pass.
        ///
        /// <paramref name="pass"/> is "reference" on the first pass and "prefab"
        /// on the second. A prefab reference cannot be resolved on the first,
        /// because the asset it names is saved from an object that is still
        /// being wired; splitting the passes is what keeps a prefab field from
        /// silently assigning null.
        /// </summary>
        private static void ApplyFields(
            UnityEngine.Object target,
            List<SceneSpecField> fields,
            IReadOnlyDictionary<string, string> assetPaths,
            IReadOnlyDictionary<string, GameObject> objects,
            IReadOnlyDictionary<string, string> prefabPaths,
            string label,
            string pass)
        {
            if (fields == null || fields.Count == 0) return;

            foreach (var f in fields)
            {
                // A list runs in the second pass like a prefab does: by then
                // both the assets and the prefabs exist, and a list may name
                // either.
                var isPrefabField = f.kind == "prefab" || f.kind == "referenceList";
                if (pass == "prefab" != isPrefabField) continue;

                var field = FindSerializedField(target.GetType(), f.name);
                if (field == null)
                {
                    Problems.Add($"{label}: no serialized field '{f.name}'");
                    continue;
                }

                object value;
                switch (f.kind)
                {
                    case "reference":
                        value = ResolveReference(f.reference, assetPaths, objects, field.FieldType);
                        if (value == null)
                        {
                            Problems.Add($"{label}.{f.name}: unresolved reference '{f.reference}'");
                            continue;
                        }
                        break;
                    case "referenceList":
                        value = BuildReferenceList(f, field.FieldType, assetPaths, objects, prefabPaths);
                        if (value == null)
                        {
                            Problems.Add(
                                $"{label}.{f.name}: could not build the list — check that every id in " +
                                "'references' names an asset or object in this spec");
                            continue;
                        }
                        break;
                    case "prefab":
                        value = ResolvePrefab(f.reference, prefabPaths, field.FieldType);
                        if (value == null)
                        {
                            Problems.Add(
                                $"{label}.{f.name}: unresolved prefab '{f.reference}' — no object " +
                                "in this spec declares a prefabPath under that id");
                            continue;
                        }
                        break;
                    case "int": value = f.intValue; break;
                    case "bool": value = f.boolValue; break;
                    case "float": value = f.floatValue; break;
                    default: value = f.stringValue; break;
                }

                field.SetValue(target, value);
                Assigned.Add($"{label}.{f.name}");
            }

            EditorUtility.SetDirty(target);
        }

        /// <summary>Does any field in this list want a prefab asset?</summary>
        /// <summary>
        /// Identifies one component slot on one object.
        ///
        /// The index, not the type name: an object may carry two components of
        /// the same type, and a type-keyed map silently dropped the first.
        /// </summary>
        private static string ComponentKey(string objectId, int index) => $"{objectId}#{index}";


        /// <summary>
        /// A serialized List<T>, built from the ids the spec listed.
        ///
        /// Two shapes occur. A plain List<SomeAsset> takes the references
        /// directly. A list of wrapper structs — GameBootstrapperConfig._modules
        /// is List<ModuleEntry>, and ModuleEntry holds _config plus _enabled
        /// — needs one element constructed per id, with the reference dropped
        /// into the field the spec names and everything else left at its C#
        /// default. That second shape is the one the framework actually depends
        /// on: without it a bootstrapper can be perfectly wired to a config that
        /// starts nothing.
        /// </summary>
        private static object BuildReferenceList(
            SceneSpecField f,
            Type fieldType,
            IReadOnlyDictionary<string, string> assetPaths,
            IReadOnlyDictionary<string, GameObject> objects,
            IReadOnlyDictionary<string, string> prefabPaths)
        {
            if (f.references == null || f.references.Count == 0) return null;
            if (!fieldType.IsGenericType) return null;

            var elementType = fieldType.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance(fieldType);

            foreach (var id in f.references)
            {
                object element;

                if (string.IsNullOrEmpty(f.elementField))
                {
                    element = ResolveReference(id, assetPaths, objects, elementType)
                        ?? ResolvePrefab(id, prefabPaths, elementType);
                }
                else
                {
                    var wrapper = Activator.CreateInstance(elementType);
                    var inner = FindSerializedField(elementType, f.elementField);
                    if (inner == null)
                    {
                        Problems.Add($"{elementType.Name} has no serialized field '{f.elementField}'");
                        return null;
                    }

                    var target = ResolveReference(id, assetPaths, objects, inner.FieldType)
                        ?? ResolvePrefab(id, prefabPaths, inner.FieldType);
                    if (target == null) return null;

                    inner.SetValue(wrapper, target);
                    element = wrapper;
                }

                if (element == null) return null;
                list.Add(element);
            }

            return list;
        }

        private static bool HasPrefabField(List<SceneSpecField> fields)
        {
            if (fields == null) return false;
            foreach (var f in fields) if (f.kind == "prefab" || f.kind == "referenceList") return true;
            return false;
        }

        /// <summary>
        /// The saved prefab asset for an id, loaded fresh from its path.
        ///
        /// Never the handle SaveAsPrefabAsset returned: that is held across an
        /// AssetDatabase refresh, and a handle held across one assigns as
        /// {fileID: 0} while looking perfectly valid in the debugger.
        /// </summary>
        private static object ResolvePrefab(
            string id,
            IReadOnlyDictionary<string, string> prefabPaths,
            Type wanted)
        {
            if (string.IsNullOrEmpty(id) || prefabPaths == null) return null;
            if (!prefabPaths.TryGetValue(id, out var path)) return null;

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) return null;
            return wanted == typeof(GameObject) ? (object)go : go.GetComponent(wanted);
        }

        private static object ResolveReference(
            string id,
            IReadOnlyDictionary<string, string> assetPaths,
            IReadOnlyDictionary<string, GameObject> objects,
            Type wanted)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (assetPaths.TryGetValue(id, out var path))
                return AssetDatabase.LoadAssetAtPath(path, wanted);

            if (objects.TryGetValue(id, out var go))
                return wanted == typeof(GameObject) ? (object)go : go.GetComponent(wanted);

            return null;
        }

        /// <summary>Walks the hierarchy: a serialized field is often on a base class.</summary>
        private static FieldInfo FindSerializedField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f;
            }
            return null;
        }

        private static void VerifyOnDisk(SceneSpec spec)
        {
            var sceneText = File.Exists(spec.scene.path) ? File.ReadAllText(spec.scene.path) : null;
            if (sceneText == null)
            {
                Problems.Add($"scene missing after save: {spec.scene.path}");
                return;
            }

            // A component whose script did not bind serialises as fileID 0 and
            // is null at runtime, with nothing logged anywhere.
            foreach (Match m in Regex.Matches(sceneText, @"m_Script:\s*\{fileID:\s*(-?\d+)"))
            {
                if (m.Groups[1].Value == "0")
                {
                    Problems.Add("scene contains a component with an unbound script (m_Script fileID 0)");
                    break;
                }
            }

            // Every reference the spec asked for must be a real link, not a
            // null that looks like one.
            var sceneDemands = new Dictionary<string, int>();
            foreach (var o in spec.objects)
            {
                // An object saved as a prefab and removed from the scene has no
                // fields in the scene file to check; its wiring lives in the
                // .prefab, which is verified below.
                if (!string.IsNullOrEmpty(o.prefabPath) && !o.keepInScene) continue;

                foreach (var c in o.components)
                    foreach (var f in c.fields)
                    {
                        // A prefab reference is a link like any other, and the
                        // one most likely to be silently null: it is assigned in
                        // a second pass, after an AssetDatabase refresh.
                        if (f.kind == "referenceList")
                        {
                            // A list of wrapper structs serialises its links
                            // under the inner field, not the list's own name:
                            // "_modules:" is followed by "- _config: {fileID..."
                            // so looking for _modules beside a fileID finds
                            // nothing and reports a failure that did not happen.
                            var key = string.IsNullOrEmpty(f.elementField) ? f.name : f.elementField;
                            sceneDemands.TryGetValue(key, out var listCount);
                            sceneDemands[key] = listCount + (f.references?.Count ?? 0);
                            continue;
                        }
                        if (f.kind != "reference" && f.kind != "prefab") continue;
                        sceneDemands.TryGetValue(f.name, out var n);
                        sceneDemands[f.name] = n + 1;
                    }
            }
            VerifyLinks(sceneText, spec.scene.path, sceneDemands);

            // Prefabs get the same reading the scene does: the file has to exist,
            // its scripts have to have bound, and any reference it was asked to
            // carry has to be a real link.
            foreach (var o in spec.objects)
            {
                if (string.IsNullOrEmpty(o.prefabPath)) continue;
                if (!File.Exists(o.prefabPath))
                {
                    Problems.Add($"prefab missing after save: {o.prefabPath}");
                    continue;
                }

                var prefabText = File.ReadAllText(o.prefabPath);
                if (Regex.IsMatch(prefabText, @"m_Script:\s*\{fileID:\s*0\b"))
                    Problems.Add($"{o.prefabPath}: a component's script did not bind (m_Script fileID 0)");

                var prefabDemands = new Dictionary<string, int>();
                foreach (var c in o.components)
                    foreach (var f in c.fields)
                    {
                        if (f.kind != "reference" && f.kind != "prefab") continue;
                        prefabDemands.TryGetValue(f.name, out var n);
                        prefabDemands[f.name] = n + 1;
                    }
                VerifyLinks(prefabText, o.prefabPath, prefabDemands);
            }

            foreach (var a in spec.assets)
            {
                if (!File.Exists(a.path)) { Problems.Add($"asset missing after save: {a.path}"); continue; }
                var text = File.ReadAllText(a.path);
                if (Regex.IsMatch(text, @"m_Script:\s*\{fileID:\s*0\b"))
                    Problems.Add($"{a.path}: script did not bind (m_Script fileID 0)");

                // An asset's own reference fields were never read back at all,
                // so a config pointed at something unserialisable exited 0 with
                // {fileID: 0} on disk — the exact failure this layer exists to
                // catch, in the one file type it was not checking.
                var assetDemands = new Dictionary<string, int>();
                foreach (var f in a.fields)
                {
                    if (f.kind == "referenceList")
                    {
                        // Every entry is a link that has to be real: a config
                        // listing ten modules with one of them null starts nine.
                        // Wrapper lists serialise under the inner field name.
                        var key = string.IsNullOrEmpty(f.elementField) ? f.name : f.elementField;
                        assetDemands.TryGetValue(key, out var listCount);
                        assetDemands[key] = listCount + (f.references?.Count ?? 0);
                        continue;
                    }
                    if (f.kind != "reference" && f.kind != "prefab") continue;
                    assetDemands.TryGetValue(f.name, out var n);
                    assetDemands[f.name] = n + 1;
                }
                VerifyLinks(text, a.path, assetDemands);
            }
        }

        /// <summary>
        /// Every reference the spec asked this file to carry must be a real link.
        ///
        /// Counted, not matched. The old check took the FIRST occurrence of a
        /// field name anywhere in the file, so a scene where one object wired
        /// `_target` correctly and three others left it null read as clean: the
        /// first match was non-zero and the rest were never looked at.
        /// </summary>
        private static void VerifyLinks(string text, string label, Dictionary<string, int> demands)
        {
            foreach (var demand in demands)
            {
                var matches = Regex.Matches(text, Regex.Escape(demand.Key) + @":\s*\{fileID:\s*(-?\d+)");
                var linked = 0;
                var zeroed = 0;
                foreach (Match m in matches)
                {
                    if (m.Groups[1].Value == "0") zeroed++;
                    else linked++;
                }

                if (matches.Count == 0)
                {
                    Problems.Add($"{label}: {demand.Key} is not present in the saved file");
                }
                else if (linked < demand.Value)
                {
                    Problems.Add(
                        $"{label}: {demand.Key} was asked for {demand.Value} time(s) but only " +
                        $"{linked} is a real link ({zeroed} serialized as fileID 0)");
                }
            }
        }

        private static void AddToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == scenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static IEnumerable<string> AllTypeNames(SceneSpec spec)
        {
            var names = new HashSet<string>();
            foreach (var a in spec.assets) if (!string.IsNullOrEmpty(a.type)) names.Add(a.type);
            foreach (var o in spec.objects)
                foreach (var c in o.components)
                    if (!string.IsNullOrEmpty(c.type)) names.Add(c.type);
            return names;
        }

        /// <summary>
        /// Type.GetType needs an assembly-qualified name, which the spec author
        /// has no way to know, so every loaded assembly is searched by full name.
        /// </summary>
        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static void EnsureDirectory(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        private static string ReadArg(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return null;
        }

        private static void WriteResult(string path, int code)
        {
            if (string.IsNullOrEmpty(path)) return;
            var sb = new StringBuilder();
            sb.Append("{\"assembled\":").Append(code == ExitOk ? "true" : "false");
            sb.Append(",\"exitCode\":").Append(code);
            sb.Append(",\"created\":[").Append(string.Join(",", Created.Select(Quote))).Append(']');
            sb.Append(",\"assigned\":[").Append(string.Join(",", Assigned.Select(Quote))).Append(']');
            sb.Append(",\"problems\":[").Append(string.Join(",", Problems.Select(Quote))).Append("]}");
            try { File.WriteAllText(path, sb.ToString()); } catch { /* the exit code still carries the verdict */ }
        }

        private static string Quote(string s) =>
            "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
