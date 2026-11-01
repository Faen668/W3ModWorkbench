using System.Text.Json.Nodes;

namespace W3ModWorkbench.Core.Cr2w;

public static partial class Cr2wJson
{
    /// <summary>
    /// Puts an object's properties in the order the engine saves them: the engine's classes' properties as it declares
    /// them, its base classes' first, then the script classes' properties as the object had them. A script class is
    /// placed under the engine class it extends.
    /// </summary>
    public static void InSaveOrder(JsonObject node)
    {
        string? className = (string?)node["$class"];
        for (int depth = 0; depth < 64 && className is not null && !RttiClasses.Value.ContainsKey(className); depth++)
            className = ScriptParents.Value.GetValueOrDefault(className);
        var order = new List<string>();
        for (string? c = className; c is not null && RttiClasses.Value.TryGetValue(c, out var rtti); c = rtti.Base)
            order.InsertRange(0, rtti.Properties.Select(p => p.Name));
        if (order.Count == 0) return;
        int Rank(string key) { int i = order.IndexOf(key); return i < 0 ? int.MaxValue : i; }
        var keys = node.Select(p => p.Key).ToList();
        var props = keys.Where(k => !k.StartsWith('$')).ToList();
        var sorted = props.OrderBy(Rank).ToList(); // stable: the script classes' keep their order, last
        if (sorted.SequenceEqual(props)) return;
        // what the JSON keeps after the properties ($tail and the like) stays after them
        var after = keys.Skip(keys.FindIndex(k => !k.StartsWith('$'))).Where(k => k.StartsWith('$')).ToList();
        foreach (string key in sorted.Concat(after)) { var v = node[key]; node.Remove(key); node[key] = v; }
    }
}
