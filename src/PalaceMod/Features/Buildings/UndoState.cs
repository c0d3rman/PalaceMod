using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Events;

/*
    Field-level state capture for Undo upgrade.

    ObjectState records every instance field of an object (a game MonoBehaviour), following plain [Serializable]
    objects and list/array contents a few levels deep (e.g. AutoAttackTower.targetPriorities[i].range). Diffing a state
    taken just before an upgrade against the same objects right after it, in the same frame, yields exactly the fields
    the upgrade changed (directly or through OnEnable/listener side effects), with no per-frame noise. Undo writes those
    fields back. A full state can also be written onto a fresh component, to recreate one that destroyed itself after
    applying its effect (TowerUpgrade does).
*/
namespace PalaceMod
{
    internal sealed class RefEq : IEqualityComparer<object>
    {
        public static readonly RefEq Instance = new();
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
    }

    /// <summary>One field (or list's contents) to write back on undo.</summary>
    internal abstract class StateChange
    {
        public abstract object Key { get; }
        public abstract void Restore();
        public abstract string Describe();
    }

    internal sealed class FieldChange : StateChange
    {
        public readonly object Owner;
        public readonly FieldInfo Field;
        public readonly object Before;
        public FieldChange(object owner, FieldInfo field, object before) { Owner = owner; Field = field; Before = before; }
        public override object Key => (Owner, Field);
        public override void Restore()
        {
            if (Owner is UnityEngine.Object o && o == null) return;
            Field.SetValue(Owner, Before);
        }
        public override string Describe() => $"{Owner.GetType().Name}.{Field.Name} -> {ObjectState.Show(Before)}";
    }

    internal sealed class ListChange : StateChange
    {
        public readonly IList List;
        public readonly object[] Before;
        public ListChange(IList list, object[] before) { List = list; Before = before; }
        public override object Key => List;
        public override void Restore()
        {
            if (List is Array array)
            {
                for (int i = 0; i < array.Length && i < Before.Length; i++) array.SetValue(Before[i], i);
                return;
            }
            List.Clear();
            foreach (var item in Before) List.Add(item);
        }
        public override string Describe() => $"{List.GetType().Name} contents ({Before.Length} items)";
    }

    internal sealed class ObjectState
    {
        private const int MaxDepth = 3;
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new();

        public readonly object Target;
        private readonly FieldInfo[] fields;
        private readonly object[] values;
        private readonly ObjectState[] nested;
        private readonly object[][] listItems;
        private readonly ObjectState[][] listItemStates;

        private ObjectState(object target, int depth, HashSet<object> visited)
        {
            Target = target;
            fields = FieldsOf(target.GetType());
            values = new object[fields.Length];
            nested = new ObjectState[fields.Length];
            listItems = new object[fields.Length][];
            listItemStates = new ObjectState[fields.Length][];
            for (int i = 0; i < fields.Length; i++)
            {
                object v = fields[i].GetValue(target);
                values[i] = v;
                if (v == null || depth >= MaxDepth) continue;
                if (v is IList list)
                {
                    var items = new object[list.Count];
                    list.CopyTo(items, 0);
                    listItems[i] = items;
                    ObjectState[] states = null;
                    for (int k = 0; k < items.Length; k++)
                    {
                        if (!IsPlain(items[k]) || !visited.Add(items[k])) continue;
                        states ??= new ObjectState[items.Length];
                        states[k] = new ObjectState(items[k], depth + 1, visited);
                    }
                    listItemStates[i] = states;
                }
                else if (IsPlain(v) && visited.Add(v))
                {
                    nested[i] = new ObjectState(v, depth + 1, visited);
                }
            }
        }

        public static ObjectState Capture(object target) => new(target, 0, new HashSet<object>(RefEq.Instance) { target });

        /// <summary>Adds every difference between this state and the target's current values to changes; the first
        /// recorded value for a field wins, so diffs of several windows keep the oldest "before".</summary>
        public void Diff(Dictionary<object, StateChange> changes)
        {
            if (Target is UnityEngine.Object o && o == null) return;
            for (int i = 0; i < fields.Length; i++)
            {
                object now = fields[i].GetValue(Target);
                if (!Same(values[i], now))
                {
                    Add(changes, new FieldChange(Target, fields[i], values[i]));
                    continue;
                }
                nested[i]?.Diff(changes);
                if (listItems[i] != null && now is IList list)
                {
                    var before = listItems[i];
                    bool differs = list.Count != before.Length;
                    for (int k = 0; !differs && k < before.Length; k++) differs = !Same(before[k], list[k]);
                    if (differs) Add(changes, new ListChange(list, before));
                    if (listItemStates[i] != null)
                        foreach (var s in listItemStates[i]) s?.Diff(changes);
                }
            }
        }

        /// <summary>Writes the whole recorded state onto target (a fresh component of the same type), including the
        /// recorded contents of the objects it references.</summary>
        public void WriteTo(object target)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i].SetValue(target, values[i]);
                nested[i]?.WriteTo(nested[i].Target);
                if (listItems[i] != null && values[i] is IList list)
                {
                    new ListChange(list, listItems[i]).Restore();
                    if (listItemStates[i] != null)
                        foreach (var s in listItemStates[i]) s?.WriteTo(s.Target);
                }
            }
        }

        private static void Add(Dictionary<object, StateChange> changes, StateChange change)
        {
            if (!changes.ContainsKey(change.Key)) changes[change.Key] = change;
        }

        private static bool Same(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a is UnityEngine.Object || b is UnityEngine.Object) return false;
            var t = a.GetType();
            if (t.IsValueType || a is string) return a.Equals(b);
            return false;
        }

        // A game-defined plain object worth following (TargetPriority, Upgrade, Choice...), not a Unity object or a
        // collection we don't understand.
        private static bool IsPlain(object v)
        {
            if (v == null || v is UnityEngine.Object || v is string || v is IList) return false;
            var t = v.GetType();
            if (t.IsValueType || t.IsPrimitive) return false;
            return t.Assembly.GetName().Name == "Assembly-CSharp" && !typeof(Delegate).IsAssignableFrom(t);
        }

        private static FieldInfo[] FieldsOf(Type type)
        {
            if (fieldCache.TryGetValue(type, out var cached)) return cached;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour) && t != typeof(Component)
                 && t != typeof(UnityEngine.Object) && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(Fields))
                {
                    var ft = f.FieldType;
                    if (f.IsLiteral || f.IsInitOnly && ft.IsValueType) continue;
                    if (typeof(Delegate).IsAssignableFrom(ft) || typeof(UnityEventBase).IsAssignableFrom(ft)) continue;
                    if (typeof(IEnumerator).IsAssignableFrom(ft) && !typeof(IList).IsAssignableFrom(ft)) continue;
                    if (ft == typeof(Coroutine) || ft.IsPointer) continue;
                    list.Add(f);
                }
            }
            return fieldCache[type] = list.ToArray();
        }

        public static string Show(object v) => v switch
        {
            null => "null",
            UnityEngine.Object o => o == null ? "destroyed" : o.name,
            float f => f.ToString("0.###"),
            _ => v.ToString(),
        };
    }

    /// <summary>Listeners added to a UnityEvent at runtime (AddListener), read through Unity's internals.</summary>
    internal static class EventHooks
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo calls = typeof(UnityEventBase).GetField("m_Calls", Any);
        private static readonly FieldInfo runtimeCalls = calls?.FieldType.GetField("m_RuntimeCalls", Any);
        private static readonly Dictionary<Type, FieldInfo> delegateFields = new();
        private static readonly Dictionary<Type, FieldInfo[]> eventFields = new();

        public static bool Available => runtimeCalls != null;

        /// <summary>The UnityEvent fields of a component type.</summary>
        public static FieldInfo[] EventsOf(Type type)
        {
            if (eventFields.TryGetValue(type, out var cached)) return cached;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var f in t.GetFields(Any | BindingFlags.DeclaredOnly))
                    if (typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) list.Add(f);
            return eventFields[type] = list.ToArray();
        }

        public static object[] Calls(UnityEventBase e)
        {
            if (e == null || !Available || calls.GetValue(e) is not { } list || runtimeCalls.GetValue(list) is not IList items) return [];
            var copy = new object[items.Count];
            items.CopyTo(copy, 0);
            return copy;
        }

        /// <summary>The object a listener call runs on (the component for a method group), or null.</summary>
        public static object Target(object call)
        {
            var type = call.GetType();
            if (!delegateFields.TryGetValue(type, out var field))
            {
                for (var t = type; t != null && field == null; t = t.BaseType) field = t.GetField("Delegate", Any | BindingFlags.DeclaredOnly);
                delegateFields[type] = field;
            }
            return field?.GetValue(call) is Delegate d ? d.Target : null;
        }
    }

    /// <summary>A transform's placement and active flag.</summary>
    internal readonly struct TransformState
    {
        public readonly Transform Transform;
        public readonly Transform Parent;
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalRotation;
        public readonly Vector3 LocalScale;
        public readonly bool ActiveSelf;
        public readonly bool ActiveInHierarchy;

        public TransformState(Transform t)
        {
            Transform = t;
            Parent = t.parent;
            LocalPosition = t.localPosition;
            LocalRotation = t.localRotation;
            LocalScale = t.localScale;
            ActiveSelf = t.gameObject.activeSelf;
            ActiveInHierarchy = t.gameObject.activeInHierarchy;
        }

        public bool PlacementDiffers(Transform t) =>
            t.parent != Parent || t.localPosition != LocalPosition || t.localRotation != LocalRotation || t.localScale != LocalScale;

        public void RestorePlacement()
        {
            if (Transform == null) return;
            if (Transform.parent != Parent) Transform.SetParent(Parent, false);
            Transform.localPosition = LocalPosition;
            Transform.localRotation = LocalRotation;
            Transform.localScale = LocalScale;
        }
    }
}
