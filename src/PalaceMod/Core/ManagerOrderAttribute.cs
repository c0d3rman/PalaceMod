using System;

namespace PalaceMod
{
    /// <summary>
    /// Runs this manager's Awake/Start/Update before (lower) or after (higher) the default 0. Managers with the same
    /// order run in type-name order. Only infrastructure that others depend on should need this.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ManagerOrderAttribute(int order) : Attribute
    {
        public int Order { get; } = order;
    }
}
