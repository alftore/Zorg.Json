using System;
using System.Collections.Generic;

namespace Zorg.Json.Emission;

/// <summary>
/// Tarjan's strongly connected components, without recursion, because a chain of definitions can be longer than the stack allows.
/// Each component comes after every component it reaches, so what a definition refers to is known before the definition.
/// </summary>
internal static class StrongComponents
{
    public static List<List<T>> Find<T>(IEnumerable<T> nodes, Func<T, IReadOnlyList<T>> edges)
        where T : class
    {
        Dictionary<T, (int Order, int Low)> seen = new(ReferenceEqualityComparer.Instance);
        HashSet<T> open = new(ReferenceEqualityComparer.Instance);
        Stack<T> path = new();
        Stack<(T Node, int Edge)> calls = new();
        List<List<T>> components = [];

        foreach (T start in nodes)
        {
            if (seen.ContainsKey(start))
            {
                continue;
            }

            calls.Push((start, 0));

            while (calls.TryPop(out (T Node, int Edge) call))
            {
                (T node, int edge) = call;

                if (edge == 0)
                {
                    seen[node] = (seen.Count + 1, seen.Count + 1);
                    path.Push(node);
                    open.Add(node);
                }

                if (Descend(node, edge, edges(node), seen, open, calls))
                {
                    continue;
                }

                (int order, int low) = seen[node];

                if (calls.TryPeek(out (T Node, int Edge) parent))
                {
                    seen[parent.Node] = (seen[parent.Node].Order, Math.Min(seen[parent.Node].Low, low));
                }

                if (order == low)
                {
                    components.Add(Close(node, path, open));
                }
            }
        }

        return components;
    }

    private static bool Descend<T>(T node, int first, IReadOnlyList<T> targets, Dictionary<T, (int Order, int Low)> seen, HashSet<T> open, Stack<(T Node, int Edge)> calls)
        where T : class
    {
        for (int edge = first; edge < targets.Count; edge++)
        {
            T target = targets[edge];

            if (!seen.TryGetValue(target, out (int Order, int Low) known))
            {
                calls.Push((node, edge + 1));
                calls.Push((target, 0));
                return true;
            }

            if (open.Contains(target))
            {
                seen[node] = (seen[node].Order, Math.Min(seen[node].Low, known.Order));
            }
        }

        return false;
    }

    private static List<T> Close<T>(T node, Stack<T> path, HashSet<T> open)
        where T : class
    {
        List<T> component = [];
        T member;

        do
        {
            member = path.Pop();
            open.Remove(member);
            component.Add(member);
        }
        while (!ReferenceEquals(member, node));

        return component;
    }
}
