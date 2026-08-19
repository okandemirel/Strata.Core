using System;
using System.Collections.Generic;

namespace Strada.Core.Editor.Headless
{
    /// <summary>
    /// The document an agent writes to describe a scene it wants assembled.
    ///
    /// Shaped so a language model gets it right on the first attempt: no
    /// assembly-qualified type names, no on-disk encodings, and every
    /// cross-reference is an author-chosen id rather than a GUID it would have
    /// to invent. The executor decides how a reference is written — asset,
    /// scene object or prefab all serialise differently — because that is
    /// knowledge the author cannot be expected to carry.
    ///
    /// Order in the document is authoring order and means nothing. The builder
    /// imposes its own phase order, which is the whole reason this is a
    /// declaration rather than a sequence of tool calls.
    /// </summary>
    [Serializable]
    public class SceneSpec
    {
        public int specVersion = 1;
        public SceneSpecScene scene = new SceneSpecScene();
        public List<SceneSpecAsset> assets = new List<SceneSpecAsset>();
        public List<SceneSpecObject> objects = new List<SceneSpecObject>();
    }

    [Serializable]
    public class SceneSpecScene
    {
        public string path;
        /// <summary>"empty" or "default".</summary>
        public string mode = "empty";
        public bool addToBuildSettings = true;
    }

    /// <summary>A ScriptableObject asset to create on disk.</summary>
    [Serializable]
    public class SceneSpecAsset
    {
        public string id;
        /// <summary>Full type name, e.g. "Game.Modules.Board.BoardModuleConfig".</summary>
        public string type;
        public string path;
        /// <summary>Field name → value. Values may be a literal or {"$ref": "id"}.</summary>
        public List<SceneSpecField> fields = new List<SceneSpecField>();
    }

    /// <summary>A GameObject to create in the scene.</summary>
    [Serializable]
    public class SceneSpecObject
    {
        public string id;
        public string name;
        /// <summary>id of the parent object, or null for a root object.</summary>
        public string parent;
        /// <summary>
        /// Where to save this object as a prefab asset, e.g.
        /// "Assets/Prefabs/Enemy.prefab". Empty means it is a scene object only.
        ///
        /// Saved after its fields are applied, so the prefab captures the wiring
        /// rather than an empty shell.
        /// </summary>
        public string prefabPath;
        /// <summary>
        /// Whether the instance stays in the scene once it has been saved as a
        /// prefab. A spawner's template usually should not: the game creates
        /// those at runtime, and one left behind is a duplicate on frame zero.
        /// </summary>
        public bool keepInScene = true;
        public List<SceneSpecComponent> components = new List<SceneSpecComponent>();
    }

    [Serializable]
    public class SceneSpecComponent
    {
        public string type;
        public List<SceneSpecField> fields = new List<SceneSpecField>();
    }

    /// <summary>
    /// One field assignment.
    ///
    /// Exactly one of the value carriers is used. They are separate fields
    /// rather than one object because JsonUtility cannot deserialise a
    /// heterogeneous value, and because being explicit about which kind of
    /// value this is removes a whole class of guessing from the executor.
    /// </summary>
    [Serializable]
    public class SceneSpecField
    {
        public string name;
        /// <summary>id of an asset or object declared elsewhere in this spec.</summary>
        public string reference;
        /// <summary>
        /// Ids for a list-valued field, in order.
        ///
        /// A serialized List<T> cannot be expressed by a single reference,
        /// and the most important field in the framework is one:
        /// GameBootstrapperConfig._modules. Without this the tool could assemble
        /// a bootstrapper wired to a config that starts no modules — measured, a
        /// run hand-wrote the asset's YAML rather than use the tool, because the
        /// tool could not say what it needed to say.
        /// </summary>
        public List<string> references = new List<string>();
        /// <summary>
        /// For a list of wrapper structs, the field inside each element that
        /// holds the reference — e.g. "_config" for ModuleEntry, whose other
        /// field (_enabled) takes its C# default.
        /// </summary>
        public string elementField;
        public string stringValue;
        public int intValue;
        public bool boolValue;
        public float floatValue;
        /// <summary>"reference" | "string" | "int" | "bool" | "float".</summary>
        /// <summary>
        /// "reference" | "referenceList" | "prefab" | "string" | "int" | "bool" | "float".
        ///
        /// "reference" and "prefab" can name the same object and mean different
        /// things: a reference to the instance living in the scene, or to the
        /// prefab asset saved from it. A field holding a template to spawn wants
        /// the asset; a field holding the thing already on screen wants the
        /// instance. Nothing in the object itself distinguishes them, so the
        /// spec says which rather than letting the executor guess.
        /// </summary>
        public string kind = "string";
    }
}
