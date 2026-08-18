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
        public string stringValue;
        public int intValue;
        public bool boolValue;
        public float floatValue;
        /// <summary>"reference" | "string" | "int" | "bool" | "float".</summary>
        public string kind = "string";
    }
}
