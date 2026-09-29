// Stand-ins for UnityEngine types, with the real full names and the field layouts the agent reads. The agent's Core never
// references UnityEngine and recognises Unity types by name, so these behave like the real ones for it.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public class Object
    {
        private static int s_nextId;
        private readonly int m_InstanceID = System.Threading.Interlocked.Decrement(ref s_nextId);

        public string name;

        /// <summary>Set by <c>Destroy</c>; Unity's <c>== null</c> reports destroyed objects as null.</summary>
        public bool destroyed;

        public int GetInstanceID() => m_InstanceID;

        public static void Destroy(Object o) => o.destroyed = true;
    }

    public class ScriptableObject : Object
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeReference : Attribute
    {
    }

    public sealed class GameObject : Object
    {
        public readonly List<GameObject> children = new();
        public readonly List<Component> components = new();
        public string scene;
        public GameObject parent;

        public GameObject(string name, string scene, GameObject parent = null)
        {
            this.name = name;
            this.scene = scene;
            this.parent = parent;
            parent?.children.Add(this);
        }

        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this, name = name };
            components.Add(component);
            return component;
        }

        public Component GetComponent(Type type) => components.FirstOrDefault(c => type.IsInstanceOfType(c) && !c.destroyed);

        public GameObject Find(string path)
        {
            var current = this;
            foreach (var part in path.Split('/'))
            {
                current = current?.children.FirstOrDefault(c => c.name == part && !c.destroyed);
            }

            return current;
        }

        public string Path => parent is null ? name : parent.Path + "/" + name;
    }

    public class Component : Object
    {
        public GameObject gameObject;
    }

    public class Behaviour : Component
    {
        public bool m_Enabled = true;
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public struct Vector2 { public float x, y; }

    public struct Vector3
    {
        public float x, y, z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    public struct Vector4 { public float x, y, z, w; }

    public struct Quaternion { public float x, y, z, w; }

    public struct Color { public float r, g, b, a; }

    public struct Color32 { public byte r, g, b, a; }

    public struct Vector2Int { public int m_X, m_Y; }

    public struct Vector3Int { public int m_X, m_Y, m_Z; }

    public struct Rect { public float m_XMin, m_YMin, m_Width, m_Height; }

    public struct RectInt { public int m_XMin, m_YMin, m_Width, m_Height; }

    public struct Bounds { public Vector3 m_Center, m_Extents; }

    public struct LayerMask { public int m_Mask; }

    public struct Matrix4x4
    {
        public float m00, m10, m20, m30, m01, m11, m21, m31, m02, m12, m22, m32, m03, m13, m23, m33;
    }
}

namespace UnityEngine.Events
{
    public abstract class UnityEventBase
    {
        private readonly InvokableCallList m_Calls = new();
        private readonly List<(Object Target, string Method)> m_Persistent = new();

        public int GetPersistentEventCount() => m_Persistent.Count;

        public Object GetPersistentTarget(int index) => m_Persistent[index].Target;

        public string GetPersistentMethodName(int index) => m_Persistent[index].Method;

        public void AddPersistent(Object target, string method) => m_Persistent.Add((target, method));

        public void AddRuntimeListener(object listener) => m_Calls.m_RuntimeCalls.Add(listener);
    }

    public sealed class UnityEvent : UnityEventBase
    {
    }

    internal sealed class InvokableCallList
    {
        public readonly List<object> m_RuntimeCalls = new();
    }
}

namespace UnityRuntimeAnalysisAgent.TestAssemblies
{
    using UnityEngine;

    /// <summary>A fake set of loaded scenes: the fake Unity APIs of the tests search it.</summary>
    public sealed class FakeWorld
    {
        public List<GameObject> Roots { get; } = new();

        public GameObject Create(string name, string scene = "Main", GameObject parent = null)
        {
            var go = new GameObject(name, scene, parent);
            if (parent is null)
            {
                Roots.Add(go);
            }

            return go;
        }

        public GameObject Find(string path, string scene)
        {
            var parts = path.Split('/');
            foreach (var root in Roots.Where(r => !r.destroyed && r.name == parts[0] && (scene is null || r.scene == scene)))
            {
                var found = parts.Length == 1 ? root : root.Find(string.Join("/", parts.Skip(1)));
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>Loaded assets (counted like Unity's <c>Resources.FindObjectsOfTypeAll</c> counts them).</summary>
        public List<Object> Assets { get; } = new();

        /// <summary>Scene objects, their components and the assets that are of <paramref name="baseType"/>, by exact type.</summary>
        public IReadOnlyDictionary<Type, int> CountByType(Type baseType)
        {
            IEnumerable<Object> All(GameObject go) => new Object[] { go }.Concat(go.components).Concat(go.children.SelectMany(All));
            return Roots.SelectMany(All).Concat(Assets).Where(o => !o.destroyed && baseType.IsInstanceOfType(o))
                .GroupBy(o => o.GetType()).ToDictionary(g => g.Key, g => g.Count());
        }

        public static GameObject GameObjectOf(object value) => value switch
        {
            GameObject go when !go.destroyed => go,
            Component c when !c.destroyed => c.gameObject,
            _ => null,
        };

        public static object GetComponent(object value, Type type) => GameObjectOf(value)?.GetComponent(type);

        public static object FindChild(object value, string path) => GameObjectOf(value)?.Find(path);

        public static (string Scene, string Path)? Locate(object value) => GameObjectOf(value) is { } go ? (go.scene, go.Path) : null;

        public static bool IsDestroyed(object value) => value is null || value is Object { destroyed: true };

        public static (long InstanceId, string Name)? Describe(object value) => value is Object o && !o.destroyed ? (o.GetInstanceID(), o.name) : null;
    }
}
