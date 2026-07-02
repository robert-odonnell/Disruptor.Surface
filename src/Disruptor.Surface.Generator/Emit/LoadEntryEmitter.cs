using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits a <c>LoadAsync</c> extension on <see cref="Query{T}"/> for each
/// <c>[AggregateRoot]</c> table — the unified entry point for write-mode loads. Lives
/// alongside the existing <c>Workspace.Load{Root}Async</c> path
/// (<see cref="CompositionRootEmitter"/>) during the migration period; v1 just delegates
/// to the same <c>{Root}AggregateLoader.PopulateAsync</c> the legacy path uses, so a
/// caller can switch between the two without behavioural drift.
/// <para>
/// Filtered loads (queries with <c>Include*</c> calls present) are supported: they
/// route through <c>Query&lt;T&gt;.ExecuteIntoSessionAsync</c>, which compiles the
/// include tree and tracks the hydrated rows into the fresh session. Unfiltered
/// pinned-id loads take the aggregate-loader fast path
/// (<c>{Root}AggregateLoader.PopulateAsync</c>).
/// </para>
/// <para>
/// Skipped when no <c>[CompositionRoot]</c> is declared (the emitted body needs
/// <c>Workspace.ReferenceRegistry</c>) or no <c>[AggregateRoot]</c> tables exist.
/// </para>
/// </summary>
internal static class LoadEntryEmitter
{
    public static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        if (graph.CompositionRoots.Count != 1)
        {
            return;
        }

        var root = graph.CompositionRoots[0];
        if (!root.IsPartial)
        {
            return;
        }

        // CG044 losers are skipped — the {Name}QueryLoad class and its AddSource hint
        // key on the root's simple name (duplicate hint would be CS8785).
        var aggregateRoots = graph.Tables
            .Where(t => t.IsAggregateRoot)
            .Where(t => !graph.IsCollisionLoser(NameCollisionKind.AggregateRootName, t.FullName))
            .ToList();
        if (aggregateRoots.Count == 0)
        {
            return;
        }

        foreach (var aggRoot in aggregateRoots)
        {
            EmitOne(spc, root, aggRoot);
        }
    }

    private static void EmitOne(SourceProductionContext spc, CompositionRootModel root, TableModel aggRoot)
    {
        var entityFqn = CSharpText.GlobalName(aggRoot.Namespace, aggRoot.Name);
        var idFqn = $"{entityFqn}Id";
        var loaderFqn = $"global::Disruptor.Surface.Runtime.{aggRoot.Name}AggregateLoader";
        var refRegistryFqn = $"{CSharpText.GlobalName(root.Namespace, root.Name)}.ReferenceRegistry";

        var writer = new CodeWriter().Header();
        using (writer.Namespace(root.Namespace))
        {
            using (writer.Block($"public static class {aggRoot.Name}QueryLoad"))
            {
                // Two overloads: Surreal db (read-mode) and Transaction tx (write-mode load that
                // sees in-txn writes). Both call PopulateAsync / ExecuteIntoSessionAsync with
                // the SDK handle directly — no JSON bridge.
                EmitBody(Namespaces.SurrealFqn, "db");
                EmitBody(Namespaces.TransactionFqn, "tx");
            }

            void EmitBody(string paramTypeFqn, string paramName)
            {
                using (writer.Block($"public static async {Namespaces.TaskFqn}<{Namespaces.SessionFqn}> LoadAsync(this {Namespaces.QueryFqn}<{entityFqn}> query, {paramTypeFqn} {paramName}, {Namespaces.CtFqn} ct = default)"))
                {
                    using (writer.Block("if (query.PinnedId is null)"))
                    {
                        writer.Line("throw new global::System.InvalidOperationException(");
                        using (writer.Indent())
                        {
                            writer.Line($"\"LoadAsync requires a pinned root id. Call .WithId({aggRoot.Name}Id) before .LoadAsync(...).\");");
                        }
                    }

                    writer.Line($"var session = new {Namespaces.SessionFqn}({refRegistryFqn});");
                    
                    using (writer.Block("if (query.Includes.Count > 0)"))
                    {
                        writer.Line($"await query.ExecuteIntoSessionAsync(session, {paramName}, ct);");
                    }

                    writer.Line("else");
                    using (writer.BracedBlock())
                    {
                        writer.Line($"var rootId = new {idFqn}(query.PinnedId.Value.Value);");
                        writer.Line($"await {loaderFqn}.PopulateAsync(session, {paramName}, rootId, ct);");
                    }

                    writer.Line("return session;");
                }
            }
        }

        var hint = $"{root.FullName}.{aggRoot.Name}QueryLoad.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }
}
