using FileMapper.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FileMapper.Extract.CSharp;

/// <summary>Finite, flow-sensitive may-analysis. Repository methods are interpreted as compiler operations, never executed.</summary>
internal sealed class CSharpFlowEngine(ExtractionOutput output, CSharpCompilationContext context, CSharpFlowModels models,
    CSharpAnalysisOptions options, CancellationToken cancellation)
{
    private sealed record Callable(IMethodSymbol Symbol, SyntaxNode Syntax, ControlFlowGraph Graph);
    private sealed record Trace(string Rule, IReadOnlyList<FindingRouteStep> Steps, bool Truncated = false);
    private sealed class Value
    {
        public Dictionary<string, Trace> Traces { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Definitions { get; } = new(StringComparer.Ordinal);
        public string? ResourceIdentity { get; set; }
        public string? InstanceKey { get; set; }
    }
    private sealed record Summary(Value Returned, IReadOnlyDictionary<int, Value> Outputs, IReadOnlyDictionary<string, Value>? CapturedWrites = null);
    private sealed class State
    {
        public Dictionary<string, Value> Values { get; } = new(StringComparer.Ordinal);
        public bool Authorized { get; set; }
        public HashSet<string> AuthorizedResources { get; } = new(StringComparer.Ordinal);
        public State Copy() { var copy = new State { Authorized = Authorized }; copy.AuthorizedResources.UnionWith(AuthorizedResources); foreach (var pair in Values) copy.Values.Add(pair.Key, pair.Value); return copy; }
    }
    private sealed class Frame(Callable callable, int depth)
    {
        public Callable Callable { get; } = callable;
        public int Depth { get; } = depth;
        public Value Returned { get; set; } = new();
        public bool Partial { get; set; }
        public Dictionary<CaptureId, string> CaptureKeys { get; } = [];
        public string? ImplicitInstance { get; set; }
        public Dictionary<int, HashSet<int>> FinallyContinuations { get; } = [];
    }
    private readonly Dictionary<ISymbol, Callable> callables = new(SymbolEqualityComparer.Default);
    private readonly HashSet<string> diagnostics = new(StringComparer.Ordinal);
    private readonly HashSet<ISymbol> active = new(SymbolEqualityComparer.Default);
    private int operationCount;
    private int blockVisits;
    private bool exhausted;

    public void Run()
    {
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var semantic = context.Compilation.GetSemanticModel(tree, true);
            foreach (var syntax in tree.GetRoot(cancellation).DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
            {
                cancellation.ThrowIfCancellationRequested();
                if (callables.Count >= options.MaxMethods) { Partial(syntax, "method_budget", "Callable limit reached; later bodies are unobserved."); exhausted = true; break; }
                if (semantic.GetDeclaredSymbol(syntax, cancellation) is not IMethodSymbol symbol) continue;
                try
                {
                    if (ControlFlowGraph.Create(syntax, semantic, cancellation) is { } graph) Register(symbol, syntax, graph);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Partial(syntax, "cfg_unavailable", ex.Message); }
            }
            if (tree.GetRoot(cancellation) is CompilationUnitSyntax unit && unit.Members.Any(m => m is GlobalStatementSyntax) && context.Compilation.GetEntryPoint(cancellation) is { } entry)
            {
                try
                {
                    if (ControlFlowGraph.Create(unit, semantic, cancellation) is { } graph) Register(entry, unit, graph);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Partial(unit, "top_level_cfg_unavailable", ex.Message); }
            }
        }
        foreach (var callable in callables.Values.ToArray())
        {
            if (exhausted) break;
            Analyze(callable, null, 0);
        }
        if (exhausted || context.SyntaxTruncated)
            for (int i = 0; i < output.Findings.Count; i++) output.Findings[i] = output.Findings[i] with { Truncated = true, TruncationReason = output.Findings[i].TruncationReason ?? "Syntax or flow scope budget exhausted; later graph facts are unobserved." };
        output.Observe(Identity.For("analysis", context.Root), "flow_scope",
            $"model={models.Version}; callables={callables.Count}; blockVisits={blockVisits}; operations={operationCount}; maxCallDepth={options.MaxCallDepth}; publicParametersAreUntrusted={options.PublicParametersAreUntrusted}; truncated={exhausted}",
            context.Root, 0.85, "partial");
    }

    private void Register(IMethodSymbol symbol, SyntaxNode syntax, ControlFlowGraph graph)
    {
        if (graph.Blocks.Length > options.MaxBlocksPerMethod) { Partial(syntax, "cfg_budget", "Method block budget exceeded; body not analyzed."); return; }
        callables[symbol.OriginalDefinition] = new(symbol, syntax, graph);
        EmitGraph(symbol, syntax, graph);
        foreach (var local in graph.LocalFunctions)
        {
            if (callables.Count >= options.MaxMethods) { Partial(syntax, "method_budget", "Local function limit reached."); break; }
            var localSyntax = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation);
            if (localSyntax is not null) Register(local, localSyntax, graph.GetLocalFunctionControlFlowGraph(local, cancellation));
        }
    }

    private void EmitGraph(IMethodSymbol symbol, SyntaxNode syntax, ControlFlowGraph graph)
    {
        var methodId = SymbolId(symbol);
        output.Add(CSharpExtractor.SymbolNode(symbol, syntax.SyntaxTree.FilePath, syntax, context.Root));
        foreach (var block in graph.Blocks)
        {
            string id = Identity.For("block", methodId + ":" + block.Ordinal);
            var evidence = output.Observe(id, "basic_block", block.Kind.ToString(), syntax.SyntaxTree.FilePath, line: Line(syntax));
            output.Add(new(id, "BasicBlock", symbol.Name + ": " + block.Kind + " " + block.Ordinal, syntax.SyntaxTree.FilePath,
                new() { ["ordinal"] = block.Ordinal, ["reachable"] = block.IsReachable, ["blockKind"] = block.Kind.ToString(), ["region"] = block.EnclosingRegion.Kind.ToString(), ["line"] = Line(syntax) }));
            output.Link(methodId, id, "CONTAINS", evidence);
            foreach (var branch in Branches(block))
                output.Link(id, Identity.For("block", methodId + ":" + branch.Destination!.Ordinal), "FLOWS_TO", evidence,
                    properties: new() { ["flowKind"] = "control", ["semantics"] = branch.Semantics.ToString(), ["conditional"] = branch.IsConditionalSuccessor, ["finallyRegions"] = branch.FinallyRegions.Length });
            foreach (var target in ExceptionalTargets(block))
                output.Link(id, Identity.For("block", methodId + ":" + target), "FLOWS_TO", evidence, 0.7,
                    new() { ["flowKind"] = "exception", ["semantics"] = "conservative handler/finally entry", ["implicit"] = true });
        }
    }

    private Summary Analyze(Callable callable, IReadOnlyDictionary<int, Value>? inputs, int depth, State? captured = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (exhausted) return new(new(), new Dictionary<int, Value>());
        if (!active.Add(callable.Symbol.OriginalDefinition)) { Partial(callable.Syntax, "recursion_boundary", "Recursive call reached; input propagation is conservative."); return new(inputs is null ? new() : Merge(inputs.Values), new Dictionary<int, Value>()); }
        try
        {
            var frame = new Frame(callable, depth);
            var initial = captured?.Copy() ?? new State();
            foreach (var parameter in callable.Symbol.Parameters)
            {
                var syntax = parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation) ?? callable.Syntax;
                output.Add(CSharpExtractor.SymbolNode(parameter, syntax.SyntaxTree.FilePath, syntax, context.Root));
                Value value;
                if (inputs is not null && inputs.TryGetValue(parameter.Ordinal, out var argument)) value = Append(argument, syntax, "parameter", "call argument bound to parameter " + parameter.Name);
                else if (options.PublicParametersAreUntrusted && (callable.Symbol.DeclaredAccessibility == Accessibility.Public || SymbolEqualityComparer.Default.Equals(callable.Symbol, context.Compilation.GetEntryPoint(cancellation))) && IsInput(parameter.Type))
                    value = Source(syntax, "untrusted", "public parameter is an untrusted entrypoint assumption: " + parameter.Name);
                else value = Defined(syntax, "parameter");
                initial.Values[SymbolId(parameter)] = value;
            }
            var incoming = new State?[callable.Graph.Blocks.Length];
            incoming[0] = initial;
            var pending = new Queue<int>(); pending.Enqueue(0);
            var queued = new HashSet<int> { 0 };
            while (pending.TryDequeue(out int ordinal))
            {
                queued.Remove(ordinal); cancellation.ThrowIfCancellationRequested();
                if (++blockVisits > options.MaxBlockVisits) { exhausted = true; Partial(callable.Syntax, "flow_budget", "Global CFG worklist budget exhausted."); break; }
                var block = callable.Graph.Blocks[ordinal]; if (!block.IsReachable) continue;
                var state = incoming[ordinal]!.Copy();
                var before = state.Copy();
                foreach (var operation in block.Operations) Eval(operation, state, frame);
                if (block.BranchValue is { } branchValue)
                {
                    var value = Eval(branchValue, state, frame);
                    if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
                        frame.Returned = Merge([frame.Returned, Append(value, branchValue.Syntax, "return", "return value")]);
                }
                foreach (var branch in Branches(block))
                {
                    if (branch.FinallyRegions.Length == 0) { Propagate(branch.Destination!.Ordinal, state); continue; }
                    for (int i = 0; i < branch.FinallyRegions.Length; i++)
                    {
                        var region = branch.FinallyRegions[i];
                        int destination = i + 1 < branch.FinallyRegions.Length ? branch.FinallyRegions[i + 1].FirstBlockOrdinal : branch.Destination!.Ordinal;
                        for (int finalBlock = region.FirstBlockOrdinal; finalBlock <= region.LastBlockOrdinal; finalBlock++)
                        {
                            var terminal = callable.Graph.Blocks[finalBlock];
                            if (terminal.FallThroughSuccessor?.Semantics != ControlFlowBranchSemantics.StructuredExceptionHandling) continue;
                            if (!frame.FinallyContinuations.TryGetValue(finalBlock, out var destinations)) frame.FinallyContinuations.Add(finalBlock, destinations = []);
                            if (destinations.Add(destination) && incoming[finalBlock] is not null && queued.Add(finalBlock)) pending.Enqueue(finalBlock);
                        }
                    }
                    Propagate(branch.FinallyRegions[0].FirstBlockOrdinal, state);
                }
                if (frame.FinallyContinuations.TryGetValue(ordinal, out var continuations)) foreach (int destination in continuations) Propagate(destination, state);
                foreach (int target in ExceptionalTargets(block))
                {
                    frame.Partial = true;
                    var exceptional = Join(before, state); exceptional.Authorized = false; exceptional.AuthorizedResources.Clear();
                    Propagate(target, exceptional);
                }
                void Propagate(int target, State value)
                {
                    if (!callable.Graph.Blocks[target].IsReachable) return;
                    var combined = incoming[target] is { } prior ? Join(prior, value) : value.Copy();
                    if (incoming[target] is not null && Equivalent(incoming[target]!, combined)) return;
                    incoming[target] = combined;
                    if (queued.Add(target)) pending.Enqueue(target);
                }
            }
            if (frame.Partial) Partial(callable.Syntax, "conservative_flow", "Method includes conservative unknown-call, heap, or exceptional-flow propagation; routes are may-flow witnesses.");
            var exit = incoming[callable.Graph.Blocks.Last(b => b.Kind == BasicBlockKind.Exit).Ordinal];
            var outputs = callable.Symbol.Parameters.Where(p => p.RefKind is RefKind.Ref or RefKind.Out).ToDictionary(p => p.Ordinal, p => exit?.Values.GetValueOrDefault(SymbolId(p)) ?? new());
            var capturedWrites = captured is null ? null : captured.Values.Keys.Where(key => exit?.Values.ContainsKey(key) == true).ToDictionary(key => key, key => exit!.Values[key], StringComparer.Ordinal);
            return new(frame.Returned, outputs, capturedWrites);
        }
        finally { active.Remove(callable.Symbol.OriginalDefinition); }
    }

    private Value Eval(IOperation? operation, State state, Frame frame)
    {
        cancellation.ThrowIfCancellationRequested();
        if (operation is null || exhausted) return new();
        if (++operationCount > options.MaxOperations) { exhausted = true; Partial(operation.Syntax, "operation_budget", "Global operation budget exhausted."); return new(); }
        switch (operation)
        {
            case ILocalReferenceOperation local: return Read(SymbolId(local.Local), operation.Syntax, state);
            case IParameterReferenceOperation parameter: return Read(SymbolId(parameter.Parameter), operation.Syntax, state);
            case IFlowCaptureReferenceOperation capture: return Read(CaptureKey(capture.Id, frame), operation.Syntax, state);
            case IFlowCaptureOperation capture:
                var captured = Eval(capture.Value, state, frame); state.Values[CaptureKey(capture.Id, frame)] = captured; return captured;
            case IVariableDeclaratorOperation variable:
                return Write(SymbolId(variable.Symbol), Eval(variable.Initializer?.Value, state, frame), operation.Syntax, state, "local definition");
            case ISimpleAssignmentOperation assignment:
                return Assign(assignment.Target, Eval(assignment.Value, state, frame), assignment.Syntax, state, frame);
            case ICompoundAssignmentOperation compound:
                return Assign(compound.Target, Merge([Eval(compound.Target, state, frame), Eval(compound.Value, state, frame)]), compound.Syntax, state, frame);
            case IInvocationOperation call: return Call(call.TargetMethod, call.Arguments, call.Instance, call.Syntax, state, frame);
            case IObjectCreationOperation creation:
                var created = creation.Constructor is { } ctor ? Call(ctor, creation.Arguments, null, creation.Syntax, state, frame) : Merge(creation.Arguments.Select(a => Eval(a.Value, state, frame)));
                string instanceKey = Node(creation.Syntax, "allocation", "local allocation-site identity");
                var priorInstance = frame.ImplicitInstance; frame.ImplicitInstance = instanceKey;
                try { Eval(creation.Initializer, state, frame); } finally { frame.ImplicitInstance = priorInstance; }
                var constructed = Append(created, creation.Syntax, "creation", "object creation input"); constructed.InstanceKey = instanceKey; return constructed;
            case IFieldReferenceOperation field:
                return ReadMember(field.Field, field.Instance, operation.Syntax, state, frame);
            case IPropertyReferenceOperation property:
                if (models.Sources.FirstOrDefault(x => x.Member.Matches(property.Property)) is { } propertySource) return Source(operation.Syntax, propertySource.Kind, "modeled source " + property.Property.ToDisplayString());
                return Merge([ReadMember(property.Property, property.Instance, operation.Syntax, state, frame), .. property.Arguments.Select(a => Eval(a.Value, state, frame))]);
            case IReturnOperation returned:
                var value = Append(Eval(returned.ReturnedValue, state, frame), operation.Syntax, "return", "return value"); frame.Returned = Merge([frame.Returned, value]); return value;
            case IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation:
                Partial(operation.Syntax, "delegate_boundary", "Delegate/lambda bodies are not executed by expression evaluation; indirect invocation requires a model."); return Defined(operation.Syntax, "delegate");
            case IInvalidOperation:
                frame.Partial = true; Partial(operation.Syntax, "invalid_operation", "Unresolved compiler operation prevents a complete flow claim."); break;
        }
        var children = operation.ChildOperations.Select(child => Eval(child, state, frame)).ToArray();
        if (children.Length == 0) return Defined(operation.Syntax, operation.Kind.ToString());
        return Append(Merge(children), operation.Syntax, operation.Kind.ToString(), "expression " + operation.Kind);
    }

    private Value Call(IMethodSymbol method, IEnumerable<IArgumentOperation> arguments, IOperation? instance,
        SyntaxNode syntax, State state, Frame frame)
    {
        var receiver = ExpandMembers(Eval(instance, state, frame), state);
        var values = new Dictionary<int, Value>();
        foreach (var argument in arguments) values[argument.Parameter?.Ordinal ?? values.Count] = ExpandMembers(Eval(argument.Value, state, frame), state);
        Value Select(int[] indexes, bool includeReceiver = false) => Merge((indexes.Contains(-1) ? values.Values : indexes.Where(values.ContainsKey).Select(i => values[i])).Concat(includeReceiver ? [receiver] : []));
        var refArguments = arguments.Where(a => a.Parameter?.RefKind is RefKind.Ref or RefKind.Out).ToArray();
        foreach (var argument in refArguments)
        {
            // Do this before any modeled/unknown return path; known source-body summaries overwrite it below.
            var possible = Merge([Select([-1], true), Source(syntax, "untrusted", "unmodeled ref/out output assumption")]);
            Assign(argument.Value, Append(possible, syntax, "ref-write", "conservative ref/out write"), syntax, state, frame);
        }
        var sinkModels = models.Sinks.Where(x => x.Member.Matches(method)).ToArray();
        foreach (var sink in sinkModels) Sink(sink.Rule, Select(sink.Arguments, sink.IncludeReceiver), syntax, state, frame);
        if (method.ContainingType?.ToDisplayString() is "System.Security.Cryptography.MD5" or "System.Security.Cryptography.SHA1" && method.Name is "Create" or "HashData")
            ReportPolicy(syntax, "FM-CS-CRYPTO");
        var source = models.Sources.FirstOrDefault(x => x.Member.Matches(method));
        if (source is not null)
        {
            var value = Source(syntax, source.Kind, "modeled source " + method.ToDisplayString());
            if (method.ContainingType?.ToDisplayString() == "System.Environment" && method.Name == "GetEnvironmentVariable" &&
                arguments.FirstOrDefault()?.Value.ConstantValue is { HasValue: true, Value: string name } && models.IsSecretEnvironmentName(name))
                value = Merge([value, Source(syntax, "secret", "configured secret environment variable source (value not retained)")]);
            return value;
        }
        if (models.Sanitizers.FirstOrDefault(x => x.Member.Matches(method)) is { } sanitizer)
        {
            var value = Select([sanitizer.Argument]);
            var clean = Filter(value, sanitizer.Rules);
            return Append(clean, syntax, "sanitizer", "trusted sanitizer model for " + string.Join(",", sanitizer.Rules));
        }
        if (models.Barriers.FirstOrDefault(x => x.Member.Matches(method)) is { } barrier)
        {
            if (!method.ReturnsVoid) { frame.Partial = true; Partial(syntax, "invalid_barrier", "Only void checks modeled to throw on failure are barriers; Boolean return values are not assumed true."); }
            else
            {
                if (barrier.Authorizes)
                {
                    if (barrier.Argument is null) state.Authorized = true; // Explicit global authorization contract.
                    else if (values.TryGetValue(barrier.Argument.Value, out var resource) && resource.ResourceIdentity is { } identity) state.AuthorizedResources.Add(identity);
                }
                if (barrier.Argument is null)
                    foreach (string key in state.Values.Keys.ToArray()) state.Values[key] = Filter(state.Values[key], barrier.Rules);
                else
                {
                    var argument = arguments.FirstOrDefault(a => a.Parameter?.Ordinal == barrier.Argument);
                    if (argument?.Value is ILocalReferenceOperation local) state.Values[SymbolId(local.Local)] = Filter(values[barrier.Argument.Value], barrier.Rules);
                    if (argument?.Value is IParameterReferenceOperation parameter) state.Values[SymbolId(parameter.Parameter)] = Filter(values[barrier.Argument.Value], barrier.Rules);
                }
                Node(syntax, "barrier", "trusted throwing barrier " + method.ToDisplayString());
            }
            return new();
        }
        if (models.Propagators.FirstOrDefault(x => x.Member.Matches(method)) is { } propagation)
            return Append(Select(propagation.Arguments, propagation.IncludeReceiver), syntax, "call", "configured call propagation " + method.ToDisplayString());
        if (callables.TryGetValue(method.OriginalDefinition, out var target) && !method.IsVirtual && method.MethodKind != MethodKind.Constructor)
        {
            if (frame.Depth < options.MaxCallDepth && !active.Contains(method.OriginalDefinition))
            {
                var summary = Analyze(target, values, frame.Depth + 1, method.MethodKind == MethodKind.LocalFunction ? state : null);
                if (summary.CapturedWrites is not null) foreach (var pair in summary.CapturedWrites) state.Values[pair.Key] = pair.Value;
                foreach (var argument in refArguments)
                    if (summary.Outputs.TryGetValue(argument.Parameter!.Ordinal, out var written)) Assign(argument.Value, Append(written, syntax, "call-output", "source-body ref/out summary"), syntax, state, frame);
                return Append(summary.Returned, syntax, "call-return", "bounded source-body return summary " + method.Name);
            }
            Partial(syntax, "call_depth", "Call depth or recursion bound reached; propagating inputs conservatively."); frame.Partial = true;
        }
        if (!method.ReturnsVoid)
        {
            // Safe fallback cannot know external library semantics. This is explicitly a may-flow, not a sanitizer.
            frame.Partial = true;
            return Append(Select([-1], true), syntax, "unknown-call", "conservative unmodeled return propagation " + method.ToDisplayString());
        }
        if (refArguments.Length > 0) frame.Partial = true;
        if (method.MethodKind == MethodKind.Constructor) return Append(Select([-1], true), syntax, "constructor-input", "constructor argument propagation; field effects require local observations");
        return new();
    }

    private Value Assign(IOperation target, Value value, SyntaxNode syntax, State state, Frame frame)
    {
        switch (target)
        {
            case IDeclarationExpressionOperation declaration: return Assign(declaration.Expression, value, syntax, state, frame);
            case ILocalReferenceOperation local: return Write(SymbolId(local.Local), value, syntax, state, "local assignment replaces prior value");
            case IParameterReferenceOperation parameter: return Write(SymbolId(parameter.Parameter), value, syntax, state, "parameter assignment replaces prior value");
            case IFieldReferenceOperation field: return Write(MemberKey(field.Field, field.Instance, state, frame), value, syntax, state, "locally resolved field assignment");
            case IPropertyReferenceOperation property:
                foreach (var sink in models.Sinks.Where(x => x.Member.Matches(property.Property))) Sink(sink.Rule, value, syntax, state, frame);
                return Write(MemberKey(property.Property, property.Instance, state, frame), value, syntax, state, "locally resolved property assignment");
            case IFlowCaptureReferenceOperation capture: return Write(CaptureKey(capture.Id, frame), value, syntax, state, "capture assignment");
            default: frame.Partial = true; return Append(value, syntax, "heap-write", "unresolved heap write; alias precision unavailable");
        }
    }
    private string MemberKey(ISymbol member, IOperation? receiver, State state, Frame frame) => SymbolId(member) + "@" + (receiver switch
    {
        ILocalReferenceOperation local => state.Values.GetValueOrDefault(SymbolId(local.Local))?.InstanceKey ?? SymbolId(local.Local),
        IParameterReferenceOperation parameter => state.Values.GetValueOrDefault(SymbolId(parameter.Parameter))?.InstanceKey ?? SymbolId(parameter.Parameter),
        IFlowCaptureReferenceOperation capture => state.Values.GetValueOrDefault(CaptureKey(capture.Id, frame))?.InstanceKey ?? CaptureKey(capture.Id, frame),
        IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver } when frame.ImplicitInstance is not null => frame.ImplicitInstance,
        null or IInstanceReferenceOperation => "this-or-static", _ => Identity.For("receiver", receiver.Syntax.SyntaxTree.FilePath + ":" + receiver.Syntax.SpanStart)
    });
    private Value ExpandMembers(Value value, State state)
    {
        if (value.InstanceKey is not { } key) return value;
        var expanded = Merge(new[] { value }.Concat(state.Values.Where(p => p.Key.EndsWith("@" + key, StringComparison.Ordinal)).Select(p => p.Value)));
        expanded.InstanceKey = value.InstanceKey;
        return expanded;
    }
    private string CaptureKey(CaptureId id, Frame frame)
    {
        if (!frame.CaptureKeys.TryGetValue(id, out var key)) frame.CaptureKeys.Add(id, key = "capture:" + SymbolId(frame.Callable.Symbol) + ":" + frame.CaptureKeys.Count);
        return key;
    }
    private Value ReadMember(ISymbol member, IOperation? receiver, SyntaxNode syntax, State state, Frame frame)
    {
        var key = MemberKey(member, receiver, state, frame);
        if (state.Values.ContainsKey(key)) return Read(key, syntax, state);
        return Append(Eval(receiver, state, frame), syntax, "member-read", "member read; receiver input propagation");
    }
    private Value Read(string key, SyntaxNode syntax, State state)
    {
        var value = state.Values.GetValueOrDefault(key) ?? new();
        var use = Node(syntax, "use", "definition-use read");
        foreach (string definition in value.Definitions) Edge(definition, use, syntax, "reaching definition to use");
        return Append(value, syntax, "use", "definition-use read");
    }
    private Value Write(string key, Value value, SyntaxNode syntax, State state, string reason)
    {
        var result = Append(value, syntax, "definition:" + key, reason);
        string definition = Node(syntax, "definition:" + key, reason);
        foreach (string prior in value.Definitions) Edge(prior, definition, syntax, "value to definition");
        result.ResourceIdentity = value.ResourceIdentity;
        result.Definitions.Clear(); result.Definitions.Add(definition); state.Values[key] = result; return result;
    }
    private Value Source(SyntaxNode syntax, string kind, string reason)
    {
        string node = Node(syntax, "source-" + kind, reason);
        var value = new Value { ResourceIdentity = node, InstanceKey = node }; value.Definitions.Add(node);
        foreach (var rule in models.Rules.Where(x => x.SourceKind == kind)) value.Traces[node + "|" + rule.Id] = new(rule.Id, [Step(node, null, syntax, reason)]);
        return value;
    }
    private Value Defined(SyntaxNode syntax, string role) { var value = new Value(); string id = Node(syntax, role, "expression definition"); value.Definitions.Add(id); value.ResourceIdentity = id; return value; }
    private Value Filter(Value value, string[] rules)
    {
        var filtered = new Value { ResourceIdentity = value.ResourceIdentity, InstanceKey = value.InstanceKey }; filtered.Definitions.UnionWith(value.Definitions);
        foreach (var pair in value.Traces.Where(x => !rules.Contains(x.Value.Rule, StringComparer.Ordinal))) filtered.Traces.Add(pair.Key, pair.Value);
        return filtered;
    }
    private Value Append(Value value, SyntaxNode syntax, string role, string reason)
    {
        var result = new Value(); result.Definitions.UnionWith(value.Definitions);
        string node = Node(syntax, role, reason);
        result.ResourceIdentity = role == "use" ? value.ResourceIdentity : node;
        result.InstanceKey = value.InstanceKey;
        foreach (var pair in value.Traces)
        {
            var trace = pair.Value;
            if (trace.Steps[^1].NodeId == node) { result.Traces.Add(pair.Key, trace); continue; }
            bool truncated = trace.Truncated || trace.Steps.Count >= options.MaxRouteLength;
            var prefix = trace.Steps.Count >= options.MaxRouteLength ? trace.Steps.Take(options.MaxRouteLength - 1).ToArray() : trace.Steps;
            string edge = Edge(prefix[^1].NodeId, node, syntax, truncated ? "bounded route summary: " + reason : reason);
            result.Traces.Add(pair.Key, new(trace.Rule, [.. prefix, Step(node, edge, syntax, reason)], truncated));
        }
        return result;
    }
    private Value Merge(IEnumerable<Value> values)
    {
        var result = new Value();
        var identities = new HashSet<string?>(StringComparer.Ordinal);
        var instances = new HashSet<string?>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            identities.Add(value.ResourceIdentity);
            instances.Add(value.InstanceKey);
            foreach (string definition in value.Definitions.Order(StringComparer.Ordinal).Take(options.MaxDefinitionsPerValue)) result.Definitions.Add(definition);
            foreach (var pair in value.Traces.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!result.Traces.TryGetValue(pair.Key, out var prior) || pair.Value.Steps.Count < prior.Steps.Count) result.Traces[pair.Key] = pair.Value;
            }
        }
        result.ResourceIdentity = identities.Count == 1 ? identities.Single() : null;
        result.InstanceKey = instances.Count == 1 ? instances.Single() : null;
        if (result.Traces.Count > options.MaxOriginsPerValue || result.Definitions.Count > options.MaxDefinitionsPerValue)
        {
            exhausted = true; output.Error(context.Root, "value_budget", "Flow-origin or reaching-definition budget exceeded; analysis is incomplete.");
            foreach (string key in result.Traces.Keys.Order(StringComparer.Ordinal).Skip(options.MaxOriginsPerValue).ToArray()) result.Traces.Remove(key);
            foreach (string key in result.Definitions.Order(StringComparer.Ordinal).Skip(options.MaxDefinitionsPerValue).ToArray()) result.Definitions.Remove(key);
        }
        return result;
    }
    private State Join(State left, State right)
    {
        var result = new State { Authorized = left.Authorized && right.Authorized };
        result.AuthorizedResources.UnionWith(left.AuthorizedResources.Intersect(right.AuthorizedResources, StringComparer.Ordinal));
        foreach (string key in left.Values.Keys.Concat(right.Values.Keys).Distinct(StringComparer.Ordinal))
            result.Values[key] = Merge([left.Values.GetValueOrDefault(key) ?? new(), right.Values.GetValueOrDefault(key) ?? new()]);
        return result;
    }
    private static bool Equivalent(State left, State right) => left.Authorized == right.Authorized && left.AuthorizedResources.SetEquals(right.AuthorizedResources) && left.Values.Count == right.Values.Count &&
        left.Values.All(pair => right.Values.TryGetValue(pair.Key, out var value) && pair.Value.ResourceIdentity == value.ResourceIdentity && pair.Value.InstanceKey == value.InstanceKey && pair.Value.Definitions.SetEquals(value.Definitions) && pair.Value.Traces.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(value.Traces.Keys));

    private void Sink(string ruleId, Value input, SyntaxNode syntax, State state, Frame frame)
    {
        if (ruleId == "FM-CS-AUTH" && (state.Authorized || input.ResourceIdentity is { } identity && state.AuthorizedResources.Contains(identity))) return;
        var rule = models.Rules.FirstOrDefault(r => r.Id == ruleId) ?? throw new ArgumentException("Sink references unknown rule: " + ruleId);
        bool authorizationPolicy = ruleId == "FM-CS-AUTH" && input.Traces.Values.All(t => t.Rule != ruleId);
        if (authorizationPolicy) input = Source(syntax, "untrusted", "configured privileged operation has no established authorization precondition");
        var witness = Append(input, syntax, "sink-" + ruleId, "modeled sink: " + rule.Title).Traces.Values.FirstOrDefault(x => x.Rule == ruleId);
        if (witness is null) return;
        AddFinding(rule, syntax, witness, authorizationPolicy ? "policy-pattern" : "modeled-flow", frame.Partial);
    }
    private void ReportPolicy(SyntaxNode syntax, string ruleId)
    {
        var rule = models.Rules.Single(r => r.Id == ruleId); string node = Node(syntax, "policy", rule.Title);
        AddFinding(rule, syntax, new(ruleId, [Step(node, null, syntax, rule.Title)]), "policy-pattern", false);
    }
    private void AddFinding(CSharpRule rule, SyntaxNode syntax, Trace trace, string status, bool partial)
    {
        string id = Identity.For("finding", syntax.SyntaxTree.FilePath + ":" + syntax.SpanStart + ":" + rule.Id);
        if (output.Findings.Any(f => f.Id == id)) return;
        var evidence = output.Observe(trace.Steps[^1].NodeId, "modeled_finding", rule.Title, syntax.SyntaxTree.FilePath, 0.85, "partial", Line(syntax));
        output.AddFinding(new(id, rule.Id, rule.Severity, rule.Title, syntax.SyntaxTree.FilePath, Line(syntax),
            (status == "policy-pattern" ? "Resolved intrinsic policy pattern. " : "Observed a modeled " + rule.SourceKind + " flow to this sink. ") + rule.FalsePositiveNotes, status == "policy-pattern" ? 0.7 : partial ? 0.7 : 0.85, [evidence], rule.Cwe)
        {
            Status = status, ModelVersion = models.Version, Completeness = "partial", Route = trace.Steps,
            Truncated = trace.Truncated || exhausted, TruncationReason = trace.Truncated ? "Route length budget; summary edges bridge omitted propagation." : exhausted ? "Analysis budget exhausted." : null,
            Assumptions = ["Safe runtime-reference compilation, not evaluated project context.", "Flow-sensitive may-analysis; branch predicates and runtime feasibility are not proved.",
                options.PublicParametersAreUntrusted ? "Public input parameters are modeled as untrusted entrypoints." : "Only configured sources are untrusted.", "Models are trusted host configuration; external authorization and sanitization are not inferred."]
        });
    }
    private string Node(SyntaxNode syntax, string role, string reason)
    {
        string id = Identity.For("flow", syntax.SyntaxTree.FilePath + ":" + syntax.SpanStart + ":" + syntax.Span.Length + ":" + role);
        output.Add(new(id, role.StartsWith("source", StringComparison.Ordinal) ? "TaintSource" : role.StartsWith("sink", StringComparison.Ordinal) ? "TaintSink" : "FlowOperation",
            role, syntax.SyntaxTree.FilePath, new() { ["line"] = Line(syntax), ["column"] = syntax.GetLocation().GetLineSpan().StartLinePosition.Character + 1,
                ["start"] = syntax.SpanStart, ["length"] = syntax.Span.Length, ["reason"] = reason, ["modelVersion"] = models.Version }));
        return id;
    }
    private string Edge(string from, string to, SyntaxNode syntax, string reason)
    {
        string evidence = output.Observe(to, "flow_step", reason, syntax.SyntaxTree.FilePath, 0.85, "partial", Line(syntax));
        output.Link(from, to, "FLOWS_TO", evidence, 0.85, new() { ["flowKind"] = "data", ["reason"] = reason });
        return Identity.For("edge", from + ":FLOWS_TO:" + to + ":" + evidence);
    }
    private void Partial(SyntaxNode syntax, string code, string detail)
    { if (diagnostics.Add(syntax.SyntaxTree.FilePath + ":" + syntax.SpanStart + ":" + code)) output.Error(syntax.SyntaxTree.FilePath, code, detail, Line(syntax)); }
    private string SymbolId(ISymbol symbol) => CSharpExtractor.SymbolId(symbol, context.Root);
    private static int Line(SyntaxNode syntax) => syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static FindingRouteStep Step(string node, string? edge, SyntaxNode syntax, string reason) => new(node, edge, syntax.SyntaxTree.FilePath, Line(syntax), reason, syntax.GetLocation().GetLineSpan().StartLinePosition.Character + 1);
    private static bool IsInput(ITypeSymbol type) => type.SpecialType is SpecialType.System_String or SpecialType.System_Object || type is IArrayTypeSymbol || type.ToDisplayString() == "System.IO.Stream";
    private static IEnumerable<ControlFlowBranch> Branches(BasicBlock block) => new[] { block.FallThroughSuccessor, block.ConditionalSuccessor }.OfType<ControlFlowBranch>().Where(b => b.Destination is not null);
    private static IEnumerable<int> ExceptionalTargets(BasicBlock block)
    {
        var found = new HashSet<int>();
        for (var region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
        {
            if (region.Kind != ControlFlowRegionKind.Try) continue;
            var parent = region.EnclosingRegion;
            if (parent is null) continue;
            foreach (var handler in parent.NestedRegions.Where(r => r != region && r.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler or ControlFlowRegionKind.Finally))
                if (found.Add(handler.FirstBlockOrdinal)) yield return handler.FirstBlockOrdinal;
        }
        foreach (var branch in Branches(block)) foreach (var region in branch.FinallyRegions)
            if (found.Add(region.FirstBlockOrdinal)) yield return region.FirstBlockOrdinal;
    }
}

internal sealed record CSharpCompilationContext(Microsoft.CodeAnalysis.CSharp.CSharpCompilation Compilation, string Root, bool SyntaxTruncated = false);
