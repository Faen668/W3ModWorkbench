using System.IO;
using W3ModWorkbench.Core.BehTrees;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Trees;

/// <summary>
/// A steering graph (.w2steer), how an NPC moves towards its goal: a CMoveSteeringBehavior whose root starts a tree of
/// groups (each runs all of its children), conditions (the first child when the test passes, the second when it
/// doesn't) and tasks (each one steering rule: keep away from walls, strafe round the target, keep a distance…).
/// </summary>
public static class SteeringFile
{
    public static bool IsSteering(string path) => path.EndsWith(".w2steer", StringComparison.OrdinalIgnoreCase);

    private const uint Group = 0xFF8C8C96, Condition = 0xFFD0614A, Task = 0xFF4C8FC4;

    private static readonly TreeLegend[] Legend = [new("Group (runs all its children)", Group), new("Condition (true or false)", Condition), new("Task", Task)];

    public static TreeDocument Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var behavior = file.First("CMoveSteeringBehavior") ?? throw new InvalidDataException("This isn't a steering graph.");
        BehTreeNode? root = behavior["root"] is RedHandle { Export: int at }
            ? ObjectTree.Build(file, at, c => ObjectTree.IsA(c, "IMoveSteeringNode"), (o, _) => Look(file, o))
            : null;
        if (root is not null) Labels(root);
        int tasks = root?.All().Count(n => n.Tint == Task) ?? 0, conditions = root?.All().Count(n => n.Tint == Condition) ?? 0;
        return new TreeDocument
        {
            File = file,
            Root = root,
            Resource = behavior,
            KindName = "Steering graph",
            Legend = Legend,
            Summary = $"{Format.Count(tasks, "task")}, {Format.Count(conditions, "condition")}",
        };
    }

    /// <summary>Each child's place under its parent, as the steering editor numbers them, and a condition's true and false.</summary>
    private static void Labels(BehTreeNode node)
    {
        bool condition = node.Tint == Condition;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            string place = condition ? (i == 0 ? "(0) true" : i == 1 ? "(1) false" : $"({i})") : $"({i})";
            child.Label = child.Comment is { } comment ? $"{place}  {comment}" : place;
            Labels(child);
        }
    }

    private static TreeLook Look(Cr2wFile file, RedObject o)
    {
        string? comment = o["comment"] is string c && c.Length > 0 && c != "None" ? c : null;
        RedObject? Linked(string property) =>
            o[property] is RedHandle { Export: int i } && i < file.Exports.Count ? file.Object(i) : null;
        switch (o.ClassName)
        {
            case "CMoveSNTask":
            {
                var task = Linked("task");
                return new(Task, "Task", task is null ? "Task" : ObjectTree.Words(task.ClassName, "CMoveST", "CMoveSteeringTask", "C"),
                    task is null ? "" : Settings(task), null, comment, BehTreeNodeKind.Action, task);
            }
            case "CMoveSNCondition":
            {
                var test = Linked("condition");
                return new(Condition, "Condition", test is null ? "Condition" : ObjectTree.Words(test.ClassName, "CMoveSC", "CMoveSteeringCondition", "C"),
                    test is null ? "" : Settings(test), null, comment, BehTreeNodeKind.Condition, test);
            }
        }
        string group = o["groupName"] as string is { Length: > 0 } g && g != "None" ? g : "Group";
        return new(Group, "Group", group, Format.Count(o.Array("children").Count, "child", "children"), null, comment, BehTreeNodeKind.Composite);
    }

    /// <summary>A task's or test's settings, short.</summary>
    private static string Settings(RedObject o) =>
        string.Join("  ·  ", o.Properties.Where(p => p.Name is not ("graphPosX" or "graphPosY")).Select(p => (p.Name, Text: ObjectTree.Short(p.Value)))
            .Where(p => p.Text is { Length: > 0 and < 30 }).Take(2).Select(p => $"{p.Name} {p.Text}"));
}
