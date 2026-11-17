using FileMapper.Domain;
using FileMapper.Extract.CSharp;
using System.Management.Automation.Language;

namespace FileMapper.Extract.PowerShell;

/// <summary>Bounded, ordered local interpretation over AST data, not a PowerShell evaluator.</summary>
internal sealed class PowerShellFlowAnalyzer(string snapshot,string path,string document,ExtractionOutput output,CancellationToken ct)
{
    private sealed record Trace(string Kind,List<FindingRouteStep> Steps,List<string> Evidence,bool Numeric=false,bool Partial=false,bool Truncated=false);
    private sealed record TableReference(int[] Ids,bool Exact,List<Trace> OtherTraces);
    private sealed record TableState(Dictionary<string,Value> Fields,Value Unknown,List<Trace> Keys);
    private sealed record Value(List<Trace> Traces,object? Constant=null,TableReference? Table=null,ScriptBlockAst? Block=null)
    { public static Value Empty=>new([]); }
    private sealed class State(string scope,State? parent=null)
    {
        public string Scope=scope;public State? Parent=parent;public Dictionary<string,Value> Variables=new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,FunctionDefinitionAst> Functions=new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,string?> Aliases=new(StringComparer.OrdinalIgnoreCase);
        // Call scopes share their caller's heap. A control-flow fork owns a copy of the
        // index; immutable table states are replaced on writes, never edited in place.
        public Dictionary<int,TableState> Tables=parent?.Tables??[];
        public bool Halt;
        public State Fork(){var copy=new State(Scope,Parent){Variables=new(Variables,StringComparer.OrdinalIgnoreCase),Functions=new(Functions,StringComparer.OrdinalIgnoreCase),Aliases=new(Aliases,StringComparer.OrdinalIgnoreCase),Tables=new(Tables)};return copy;}
        public State Script=>Parent?.Script??this;
        public Value? Get(string name)
        {
            if(name.StartsWith("script:",StringComparison.OrdinalIgnoreCase))return Script.Variables.GetValueOrDefault(name[7..]);
            if(name.StartsWith("global:",StringComparison.OrdinalIgnoreCase))return Script.Variables.GetValueOrDefault(name[7..]);
            return Variables.GetValueOrDefault(name)??Parent?.Get(name);
        }
        public FunctionDefinitionAst? Function(string name)=>Functions.GetValueOrDefault(name)??Parent?.Function(name);
        public bool Alias(string name,out string? target){if(Aliases.TryGetValue(name,out target))return true;return Parent?.Alias(name,out target)??false;}
    }
    private readonly HashSet<FunctionDefinitionAst> invoked=[];
    private readonly HashSet<string> findingKeys=[];
    private int operations,invocations,nextTableId;
    private bool budgetReported;
    private const int MaxOperations=100000,MaxTraces=16,MaxSteps=64,MaxDepth=6;
    public void Analyze(ScriptBlockAst ast)
    {
        var state=new State("script");
        Declare(ast,state);
        BindParameters(ast.ParamBlock?.Parameters,null,state,"script-parameter");
        RunScript(ast,state,0);
        foreach(var function in state.Functions.Values.Distinct().Where(x=>!invoked.Contains(x)).ToArray())
        {
            var functionState=new State("entry:"+function.Extent.StartOffset,state);
            BindParameters(function.Parameters??function.Body.ParamBlock?.Parameters,null,functionState,"function-entry-parameter");
            Declare(function.Body,functionState);RunScript(function.Body,functionState,1);
        }
    }
    private bool Budget(Ast ast)
    {
        ct.ThrowIfCancellationRequested();
        if(++operations<=MaxOperations)return true;
        if(!budgetReported){budgetReported=true;output.Error(path,"powershell_flow_budget","Local flow stopped at 100,000 AST/heap operations; unresolved coverage remains partial.",ast.Extent.StartLineNumber);}return false;
    }
    private string Node(Ast ast,string kind,string name,string context="")
    {
        var id=PowerShellExtractor.Id(path,ast,"ps-"+kind,context);output.Add(new(id,kind=="command"||kind=="function"?"Function":kind=="parameter"?"Parameter":"Symbol",name,path,PowerShellExtractor.Properties(ast,kind)));return id;
    }
    private void Uncertain(Ast ast,string reason)=>output.Observe(document,"flow_uncertainty",reason,path,.5,"partial",ast.Extent.StartLineNumber);
    private Value Origin(Ast ast,string kind,string context,object? constant=null,bool partial=false)
    {
        var id=Node(ast,"value",kind,context);var ev=output.Observe(id,"flow_source",kind,path,partial?.5:1,partial?"partial":"complete",ast.Extent.StartLineNumber);
        return new([new(kind,[new(id,null,path,ast.Extent.StartLineNumber,kind,ast.Extent.StartColumnNumber)],[ev],Partial:partial)],constant);
    }
    private Value Move(Value value,string target,Ast ast,string reason,bool partial=false)
    {
        var traces=new List<Trace>();
        foreach(var trace in value.Traces.Take(MaxTraces))
        {
            var prior=trace.Steps[^1].NodeId;
            if(prior==target){traces.Add(trace);continue;}
            var ev=output.Observe(target,"local_flow",reason,path,partial||trace.Partial?.6:.95,partial||trace.Partial?"partial":"complete",ast.Extent.StartLineNumber);
            output.Link(prior,target,"FLOWS_TO",ev,partial||trace.Partial?.6:.95,new(){["flowKind"]=reason,["modelVersion"]=PowerShellCommandCatalog.Version});
            var edge=Identity.For("edge",prior+":FLOWS_TO:"+target+":"+ev);
            if(trace.Steps.Count>=MaxSteps)
            {
                var suffix=trace.Steps.Skip(1).Append(new(target,edge,path,ast.Extent.StartLineNumber,reason,ast.Extent.StartColumnNumber)).ToList();
                suffix[0]=suffix[0] with{EdgeId=null,Reason="Earlier witness steps omitted at route budget"};
                traces.Add(trace with{Steps=suffix,Evidence=[..trace.Evidence.TakeLast(MaxSteps-1),ev],Partial=true,Truncated=true});continue;
            }
            traces.Add(trace with{Steps=[..trace.Steps,new(target,edge,path,ast.Extent.StartLineNumber,reason,ast.Extent.StartColumnNumber)],Evidence=[..trace.Evidence,ev],Partial=trace.Partial||partial});
        }
        return value with{Traces=traces};
    }
    private static Value Merge(params Value[] values)
    {
        if(values.Length==1)return values[0];
        var ids=values.Where(v=>v.Table is not null).SelectMany(v=>v.Table!.Ids).Distinct().Order().ToArray();
        TableReference? table=ids.Length==0?null:new(ids,ids.Length==1&&values.All(v=>v.Table?.Exact==true),
            BoundTraces(values.SelectMany(v=>v.Table?.OtherTraces??v.Traces)));
        // Multiple output values are not a scalar constant, even if their text is
        // equal. Control-flow joins may lose constant precision but must not invent
        // a key for concatenation, array output, or another unmodeled operation.
        return new(BoundTraces(values.SelectMany(v=>v.Traces)),null,table);
    }
    private static List<Trace> BoundTraces(IEnumerable<Trace> source)
    {
        var traces=source.DistinctBy(t=>t.Kind+":"+t.Steps[0].NodeId+":"+t.Steps[^1].NodeId+":"+t.Numeric).Take(MaxTraces+1).ToArray();
        return traces.Take(MaxTraces).Select(t=>traces.Length>MaxTraces?t with{Partial=true,Truncated=true}:t).ToList();
    }
    private Value At(Value value,Ast ast,string reason,State state,bool partial=false)=>Move(value,Node(ast,"value",reason,state.Scope+":"+reason),ast,reason,partial);
    private static Value Opaque(Value value)=>value.Table is null?value:value with{Table=value.Table with{Exact=false,OtherTraces=Merge(new(value.Table.OtherTraces),new(value.Traces)).Traces}};
    private Value Materialize(Value value,State state,Ast ast,HashSet<int>? seen=null,Dictionary<int,Value>? memo=null,int depth=0)
    {
        if(!Budget(ast))return Origin(ast,"table-read-budget",state.Scope,partial:true);
        if(value.Table is null)return value;
        seen??=[];memo??=[];var values=new List<Value>{new(value.Table.OtherTraces)};
        foreach(var id in value.Table.Ids)
        {
            if(budgetReported){values.Add(Origin(ast,"table-read-budget",state.Scope,partial:true));break;}
            if(memo.TryGetValue(id,out var cached)){values.Add(cached);continue;}
            if(depth>=MaxDepth||!seen.Add(id))
            {
                values.Add(Origin(ast,"unknown-table-content",state.Scope+":"+id,partial:true));
                Uncertain(ast,"Cyclic or deeply nested table content exceeded the six-level heap read boundary.");continue;
            }
            if(state.Tables.TryGetValue(id,out var table))
            {
                var content=new List<Value>{new(table.Keys),Materialize(table.Unknown,state,ast,seen,memo,depth+1)};
                foreach(var field in table.Fields.Values)
                {
                    if(budgetReported){content.Add(Origin(ast,"table-read-budget",state.Scope,partial:true));break;}
                    content.Add(Materialize(field,state,ast,seen,memo,depth+1));
                }
                var flattened=new Value(Merge(content.ToArray()).Traces);memo[id]=flattened;values.Add(flattened);
            }
            else values.Add(Origin(ast,"unknown-table-reference",state.Scope+":"+id,partial:true));
            seen.Remove(id);
        }
        return value with{Traces=Merge(values.ToArray()).Traces,Constant=null};
    }
    private Value TableRead(Value receiver,string? key,State state,Ast ast,string reason,bool member=false)
    {
        if(!Budget(ast))return Origin(ast,"table-read-budget",state.Scope,partial:true);
        if(receiver.Table is null)return At(receiver,ast,reason,state,true);
        var values=new List<Value>();if(receiver.Table.OtherTraces.Count>0)values.Add(new(receiver.Table.OtherTraces));bool partial=!receiver.Table.Exact||key is null;
        foreach(var id in receiver.Table.Ids)
        {
            if(budgetReported)break;
            if(!state.Tables.TryGetValue(id,out var table)){values.Add(receiver);partial=true;continue;}
            if(key is null){values.AddRange(table.Fields.Values);if(member&&table.Keys.Count>0)values.Add(new(table.Keys));if(table.Unknown.Traces.Count>0||table.Unknown.Table is not null)values.Add(table.Unknown);continue;}
            if(table.Unknown.Traces.Count>0||table.Unknown.Table is not null)values.Add(table.Unknown);
            if(table.Fields.TryGetValue(key,out var item))values.Add(item);
            else if(member)
            {
                // Known literal entries shadow adapted members. For missing entries,
                // Keys and Values have different content channels; an unknown member
                // can select either. Index syntax never selects adapted properties.
                if(!key.Equals("Keys",StringComparison.OrdinalIgnoreCase))values.AddRange(table.Fields.Values);
                if(!key.Equals("Values",StringComparison.OrdinalIgnoreCase)&&table.Keys.Count>0)values.Add(new(table.Keys));partial=true;
                Uncertain(ast,"Hashtable member has no known literal entry; adapted property behavior remains a candidate.");
            }
        }
        var value=Merge(values.Select(v=>Materialize(v,state,ast)).ToArray());if(partial)value=Opaque(value);
        return At(value,ast,reason,state,partial);
    }
    private bool TableWrite(AssignmentStatementAst assignment,Value value,State state,int depth)
    {
        if(!Budget(assignment))return true;
        Ast? container;Ast? keyExpression;
        switch(assignment.Left)
        {
            case MemberExpressionAst member when !member.Static:container=member.Expression;keyExpression=member.Member;break;
            case IndexExpressionAst index:container=index.Target;keyExpression=index.Index;break;
            default:return false;
        }
        var receiver=Eval(container,state,depth);var keyValue=Eval(keyExpression,state,depth);var key=keyValue.Constant as string;
        if(receiver.Table is null)return false;
        bool strong=assignment.Operator==TokenKind.Equals&&receiver.Table.Exact&&receiver.Table.Ids.Length==1&&key is not null;
        if(!strong)Uncertain(assignment,"Table write uses a weak update: compound operator, dynamic/non-string key, or alternative receiver identity.");
        foreach(var id in receiver.Table.Ids)
        {
            if(!Budget(assignment))break;
            if(!state.Tables.TryGetValue(id,out var table))continue;
            var fields=new Dictionary<string,Value>(table.Fields,StringComparer.OrdinalIgnoreCase);
            if(key is null)
            {
                var keys=At(Merge(new(table.Keys),keyValue),assignment,"table-key-write-candidate",state,true).Traces;
                state.Tables[id]=new(fields,At(Merge(table.Unknown,Opaque(value)),assignment,"table-unknown-key-write",state,true),keys);continue;
            }
            if(fields.Count>=4096&&!fields.ContainsKey(key))
            {
                Uncertain(assignment,"Table key budget exceeded (4096); write retained as unknown-key flow.");
                state.Tables[id]=new(fields,At(Merge(table.Unknown,Opaque(value)),assignment,"table-key-budget-write",state,true),table.Keys);continue;
            }
            var written=strong?value:Merge(fields.GetValueOrDefault(key)??Value.Empty,table.Unknown,value);
            // Concatenation/arithmetic can invalidate an earlier numeric sanitizer.
            if(assignment.Operator!=TokenKind.Equals)written=Opaque(written) with{Traces=written.Traces.Select(t=>t with{Numeric=false}).ToList(),Constant=null};
            fields[key]=At(written,assignment,strong?"table-field-assignment":"table-field-weak-update",state,!strong);
            state.Tables[id]=new(fields,table.Unknown,table.Keys);
        }
        return true;
    }
    private void Declare(ScriptBlockAst ast,State state)
    {
        // Stop at each nested lexical scriptblock: it owns its own declarations.
        foreach(var function in ast.FindAll(n=>n is FunctionDefinitionAst,false).OfType<FunctionDefinitionAst>())
        {
            state.Functions[function.Name]=function;var id=Node(function,"function",function.Name,state.Scope);
            output.Link(document,id,"DEFINES",output.Observe(id,"function_declaration",function.Name,path,line:function.Extent.StartLineNumber));
        }
    }
    private void BindParameters(IEnumerable<ParameterAst>? parameters,IReadOnlyDictionary<string,Value>? actual,State state,string source)
    {
        int position=0;
        foreach(var parameter in parameters??[])
        {
            var name=parameter.Name.VariablePath.UserPath;
            Value value;
            if(actual is null)value=Origin(parameter,source,state.Scope,partial:source=="function-entry-parameter");
            else if(!actual.TryGetValue(name,out value!)&&!actual.TryGetValue("#"+position,out value!))value=parameter.DefaultValue is null?Value.Empty:Eval(parameter.DefaultValue,state,0);
            var id=Node(parameter,"parameter","$"+name,state.Scope);state.Variables[name]=Move(value,id,parameter,"parameter-binding");position++;
        }
    }
    private Value RunScript(ScriptBlockAst ast,State state,int depth)
    {
        var values=new List<Value>();
        foreach(var block in new[]{ast.BeginBlock,ast.ProcessBlock,ast.EndBlock,ast.CleanBlock})
            if(block is not null&&!state.Halt){var value=Run(block.Statements,state,depth);if(HasValue(value))values.Add(value);}
        return Merge(values.ToArray());
    }
    private Value Run(IEnumerable<StatementAst> statements,State state,int depth)
    {
        var values=new List<Value>();foreach(var statement in statements){if(state.Halt||!Budget(statement))break;var value=Statement(statement,state,depth);if(HasValue(value))values.Add(value);}return Merge(values.ToArray());
    }
    private static bool HasValue(Value value)=>value.Traces.Count>0||value.Constant is not null||value.Table is not null||value.Block is not null;
    private Value Statement(StatementAst ast,State state,int depth)
    {
        switch(ast)
        {
            case FunctionDefinitionAst:return Value.Empty;
            case AssignmentStatementAst assignment:
            {
                var value=Eval(assignment.Right,state,depth);
                if(assignment.Left is VariableExpressionAst variable)
                {
                    var name=Normalize(variable.VariablePath.UserPath);
                    if(assignment.Operator!=TokenKind.Equals)value=Merge(state.Get(name)??Value.Empty,value);
                    if(state.Variables.Count>=4096&&!state.Variables.ContainsKey(name)){Uncertain(ast,"Variable state budget exceeded (4096).");return Value.Empty;}
                    var target=state;var key=name;
                    if(name.StartsWith("script:",StringComparison.OrdinalIgnoreCase)||name.StartsWith("global:",StringComparison.OrdinalIgnoreCase))
                    {target=state.Script;key=name[7..];Uncertain(ast,"Explicit script/global write shares the modeled script state; external session effects are unknown.");}
                    target.Variables[key]=Move(value,Node(assignment,"definition","$"+name,state.Scope),assignment,"assignment");
                }
                else if(!TableWrite(assignment,value,state,depth))
                {
                    Uncertain(ast,"Member/index assignment conservatively taints its local base; property-sensitive heap flow is unavailable.");
                    var baseVariable=assignment.Left.Find(n=>n is VariableExpressionAst,false) as VariableExpressionAst;
                    if(baseVariable is not null){var name=Normalize(baseVariable.VariablePath.UserPath);state.Variables[name]=At(Merge(state.Get(name)??Value.Empty,value),ast,"container-write",state,true);}
                }
                return Value.Empty;
            }
            case PipelineBaseAst pipeline:return Eval(pipeline,state,depth);
            case ReturnStatementAst ret:state.Halt=true;return ret.Pipeline is null?Value.Empty:Eval(ret.Pipeline,state,depth);
            case ThrowStatementAst thr:if(thr.Pipeline is not null)Eval(thr.Pipeline,state,depth);state.Halt=true;return Value.Empty;
            case IfStatementAst conditional:
            {
                var branches=new List<State>();var results=new List<Value>();
                foreach(var clause in conditional.Clauses){Eval(clause.Item1,state,depth);var branch=state.Fork();results.Add(Run(clause.Item2.Statements,branch,depth));branches.Add(branch);}
                var other=state.Fork();if(conditional.ElseClause is not null)results.Add(Run(conditional.ElseClause.Statements,other,depth));branches.Add(other);
                Join(state,branches,ast);return At(Merge(results.ToArray()),ast,"branch-output",state,true);
            }
            case ForEachStatementAst loop:
            {
                var collection=Eval(loop.Condition,state,depth);var branch=state.Fork();branch.Variables[Normalize(loop.Variable.VariablePath.UserPath)]=At(collection,loop.Variable,"foreach-binding",branch,true);
                var result=Run(loop.Body.Statements,branch,depth);Join(state,[state.Fork(),branch],ast);Uncertain(ast,"Loop modeled with zero/one iteration join; later iterations and heap effects are partial.");return result;
            }
            case LoopStatementAst loop:
            {
                if(loop is ForStatementAst f&&f.Initializer is not null)Eval(f.Initializer,state,depth);
                if(loop.Condition is not null)Eval(loop.Condition,state,depth);var branch=state.Fork();var result=Run(loop.Body.Statements,branch,depth);
                if(loop is ForStatementAst step&&step.Iterator is not null)Eval(step.Iterator,branch,depth);
                Join(state,[state.Fork(),branch],ast);Uncertain(ast,"Loop modeled with zero/one iteration join; later iterations are partial.");return result;
            }
            case TryStatementAst attempt:
            {
                var branches=new List<State>();var results=new List<Value>();var body=state.Fork();results.Add(Run(attempt.Body.Statements,body,depth));branches.Add(body);
                foreach(var handler in attempt.CatchClauses){var branch=state.Fork();results.Add(Run(handler.Body.Statements,branch,depth));branches.Add(branch);}
                Join(state,branches,ast);if(attempt.Finally is not null){state.Halt=false;results.Add(Run(attempt.Finally.Statements,state,depth));}Uncertain(ast,"Exception dispatch joined conservatively.");return Merge(results.ToArray());
            }
            default:
                Uncertain(ast,"Unsupported statement "+ast.GetType().Name+"; nested expressions analyzed as candidates.");
                return Merge(ast.FindAll(n=>n is CommandAst,false).OfType<CommandAst>().Select(c=>Command(c,state,Value.Empty,depth)).ToArray());
        }
    }
    private void Join(State target,IReadOnlyList<State> branches,Ast ast)
    {
        var reachable=branches.Where(x=>!x.Halt).ToArray();target.Halt=reachable.Length==0;
        // A returning branch can still mutate a shared caller-owned table. Retain
        // those effects; following local statements may therefore be conservative.
        foreach(var id in branches.SelectMany(x=>x.Tables.Keys).Distinct())
        {
            var alternatives=branches.Where(x=>x.Tables.ContainsKey(id)).Select(x=>x.Tables[id]).ToArray();
            var fields=new Dictionary<string,Value>(StringComparer.OrdinalIgnoreCase);
            foreach(var key in alternatives.SelectMany(x=>x.Fields.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                fields[key]=At(Merge(alternatives.Select(x=>x.Fields.GetValueOrDefault(key)??Value.Empty).ToArray()),ast,"table-field-join:"+id+":"+key,target,true);
            target.Tables[id]=new(fields,Merge(alternatives.Select(x=>x.Unknown).ToArray()),BoundTraces(alternatives.SelectMany(x=>x.Keys)));
        }
        foreach(var name in reachable.SelectMany(x=>x.Variables.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            target.Variables[name]=At(Merge(reachable.Select(x=>x.Get(name)??Value.Empty).ToArray()),ast,"control-flow-join:"+name,target,true);
        foreach(var name in branches.SelectMany(x=>x.Aliases.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {var aliases=branches.Select(x=>x.Aliases.GetValueOrDefault(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();target.Aliases[name]=aliases.Length==1?aliases[0]:null;}
    }
    private static string Normalize(string name)=>name.StartsWith("local:",StringComparison.OrdinalIgnoreCase)||name.StartsWith("private:",StringComparison.OrdinalIgnoreCase)?name[(name.IndexOf(':')+1)..]:name;
    private Value Eval(Ast? ast,State state,int depth)
    {
        if(ast is null||!Budget(ast))return Value.Empty;
        switch(ast)
        {
            case StatementBlockAst block:return Run(block.Statements,state,depth);
            case PipelineAst pipeline:
            {
                Value value=Value.Empty;
                foreach(var element in pipeline.PipelineElements)value=element is CommandAst cmd?Command(cmd,state,value,depth):Eval(element,state,depth);
                return value;
            }
            case CommandExpressionAst expression:return Eval(expression.Expression,state,depth);
            case CommandAst command:return Command(command,state,Value.Empty,depth);
            case StringConstantExpressionAst literal:return Origin(ast,"constant",state.Scope,literal.Value);
            case ConstantExpressionAst literal:return Origin(ast,"constant",state.Scope,literal.Value);
            case VariableExpressionAst variable:
            {
                var name=Normalize(variable.VariablePath.UserPath);
                if(name.Equals("true",StringComparison.OrdinalIgnoreCase))return Origin(ast,"constant",state.Scope,true);
                if(name.Equals("false",StringComparison.OrdinalIgnoreCase))return Origin(ast,"constant",state.Scope,false);
                if(name.Equals("null",StringComparison.OrdinalIgnoreCase))return Value.Empty;
                var value=state.Get(name);
                if(value is null)
                {
                    var kind=name.StartsWith("env:",StringComparison.OrdinalIgnoreCase)?(System.Text.RegularExpressions.Regex.IsMatch(name,"secret|token|password|api_?key|credential",System.Text.RegularExpressions.RegexOptions.IgnoreCase)?"secret":"environment"):name.Equals("args",StringComparison.OrdinalIgnoreCase)||name.Equals("input",StringComparison.OrdinalIgnoreCase)?"invocation-input":"dynamic-scope";
                    value=Origin(ast,kind,state.Scope,partial:kind=="dynamic-scope");
                    if(kind=="dynamic-scope")Uncertain(ast,"Unbound variable $"+name+" may resolve through PowerShell dynamic scope.");
                }
                return At(Materialize(value,state,ast),ast,"variable-read:"+name,state);
            }
            case UsingExpressionAst capture:
                return At(Eval(capture.SubExpression,state.Parent??state,depth),ast,"remoting-using-capture",state,true);
            case ExpandableStringExpressionAst expanded:
                return At(Merge(expanded.NestedExpressions.Select(x=>Eval(x,state,depth)).ToArray()) with{Table=null,Block=null,Constant=null},ast,"string-interpolation",state);
            case BinaryExpressionAst binary:
                return At(Opaque(Merge(Eval(binary.Left,state,depth),Eval(binary.Right,state,depth))) with{Constant=null},ast,"expression:"+binary.Operator,state);
            case UnaryExpressionAst unary:return At(Opaque(Eval(unary.Child,state,depth)) with{Constant=null},ast,"unary-expression",state);
            case ConvertExpressionAst conversion:
            {
                var value=Eval(conversion.Child,state,depth);var type=conversion.Type.TypeName.FullName.ToLowerInvariant();
                if(new[]{"int","int32","system.int32","long","int64","system.int64","uint32","uint64","short","byte","double","decimal"}.Contains(type))
                {
                    value=value with{Traces=value.Traces.Select(t=>t.Kind=="secret"?t:t with{Numeric=true}).ToList(),Table=null,Block=null,Constant=null};
                    return At(value,ast,"sanitizer:numeric-conversion-for-expression-code",state);
                }
                value=Opaque(value) with{Traces=value.Traces.Select(t=>t with{Numeric=false}).ToList(),Constant=null};
                return At(value,ast,"type-conversion-unmodeled",state,true);
            }
            case ParenExpressionAst paren:return Eval(paren.Pipeline,state,depth);
            case SubExpressionAst sub:return Run(sub.SubExpression.Statements,state,depth);
            case ArrayExpressionAst array:return Opaque(Run(array.SubExpression.Statements,state,depth)) with{Constant=null};
            case ArrayLiteralAst array:return At(Opaque(Merge(array.Elements.Select(x=>Eval(x,state,depth)).ToArray())) with{Constant=null},ast,"array-elements",state);
            case HashtableAst table:
            {
                var map=new Dictionary<string,Value>(StringComparer.OrdinalIgnoreCase);var values=new List<Value>();var unknown=Value.Empty;var keys=Value.Empty;
                foreach(var pair in table.KeyValuePairs)
                {
                    if(budgetReported)break;
                    var keyValue=Eval(pair.Item1,state,depth);var key=keyValue.Constant as string;var value=Eval(pair.Item2,state,depth);values.Add(value);
                    if(key is not null&&map.Count<4096)map[key]=value;
                    else {unknown=Merge(unknown,value);keys=Merge(keys,keyValue);Uncertain(pair.Item1,"Dynamic/non-string or over-budget hashtable key remains an unknown entry.");}
                }
                if(nextTableId>=4096){Uncertain(ast,"Literal table identity budget exceeded (4096).");return Opaque(At(Merge(values.ToArray()),ast,"hashtable-budget",state,true));}
                int id=++nextTableId;state.Tables[id]=new(map,unknown,At(keys,ast,"table-key-content",state,true).Traces);
                return At(Merge(values.ToArray()),ast,"hashtable",state) with{Constant=null,Table=new([id],true,[])};
            }
            case ScriptBlockExpressionAst block:return new([],Block:block.ScriptBlock);
            case IndexExpressionAst index:
            {
                var value=Eval(index.Target,state,depth);var key=Eval(index.Index,state,depth).Constant as string;
                return TableRead(value,key,state,ast,"index-read");
            }
            case InvokeMemberExpressionAst member:
            {
                var values=member.Arguments?.Select(x=>Eval(x,state,depth)).ToList()??[];var receiver=Eval(member.Expression,state,depth);values.Add(receiver);
                Uncertain(ast,"Method invocation modeled as argument/receiver flow; no untrusted method executes.");
                // An unmodeled method may mutate any receiver entry. Retain arguments
                // as unknown-key flow instead of later claiming an exact safe field.
                if(receiver.Table is not null)foreach(var id in receiver.Table.Ids)
                    if(state.Tables.TryGetValue(id,out var table))state.Tables[id]=new(table.Fields,At(Merge(table.Unknown,Merge(values.Take(values.Count-1).ToArray())),ast,"table-method-write-candidate",state,true),table.Keys);
                var result=Opaque(Merge(values.ToArray()));result=result with{Traces=result.Traces.Select(t=>t with{Numeric=false}).ToList(),Constant=null};
                return At(result,ast,"method-call-candidate",state,true);
            }
            case MemberExpressionAst member:
            {
                var value=Eval(member.Expression,state,depth);var key=Eval(member.Member,state,depth).Constant as string;
                return TableRead(value,key,state,ast,"member-read",member:true);
            }
            case TypeExpressionAst:return Value.Empty;
            case AssignmentStatementAst assignment:return Statement(assignment,state,depth);
            default:Uncertain(ast,"Unsupported expression "+ast.GetType().Name);return Origin(ast,"unknown-expression",state.Scope,partial:true);
        }
    }
    private Value Command(CommandAst command,State state,Value pipeline,int depth)
    {
        if(!Budget(command))return Value.Empty;
        var literal=command.GetCommandName();var name=literal;
        bool uncertain=false,qualified=false;
        if(name is not null)
        {
            if(state.Alias(name,out var alias)){name=alias;uncertain=alias is null;}
            else if(PowerShellCommandCatalog.Aliases.TryGetValue(name,out var aliasTarget))name=aliasTarget;
            // Module-qualified builtins resolve only from their controlled Microsoft module names.
            if(name?.Contains('\\')==true&&!name.Contains(':')&&!name.StartsWith('.'))
            {
                qualified=true;
                var parts=name.Split('\\');
                if(parts.Length==2&&new[]{"Microsoft.PowerShell.Utility","Microsoft.PowerShell.Management","Microsoft.PowerShell.Security","Microsoft.PowerShell.Core"}.Contains(parts[0],StringComparer.OrdinalIgnoreCase))name=parts[1];
                else uncertain=true;
            }
        }
        var localFunction=name is null||qualified?null:state.Function(name);
        PowerShellCommandCatalog.Command? model=null;
        if(name is not null)PowerShellCommandCatalog.Commands.TryGetValue(name,out model);
        var native=name is not null&&(PowerShellCommandCatalog.NativeCommands.Contains(Path.GetFileName(name))||name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase));
        var commandId=PowerShellExtractor.Id(path,command,"ps-command",state.Scope);
        var properties=PowerShellExtractor.Properties(command,"command");properties["literalName"]=literal;properties["resolvedName"]=name??"<dynamic>";
        properties["resolution"]=localFunction is not null?"local-function":model is not null?"controlled-catalog-candidate":native?"native-command-candidate":"unresolved";
        properties["invocationOperator"]=command.InvocationOperator.ToString();properties["splatting"]=command.CommandElements.OfType<VariableExpressionAst>().Any(x=>x.Splatted);
        output.Add(new(commandId,"Function",name??"<dynamic>",path,properties));
        var invocation=output.Observe(commandId,"command_invocation",name??"dynamic invocation",path,model is not null?.9:.5,model is not null?"complete":"partial",command.Extent.StartLineNumber);
        output.Link(document,commandId,"CALLS",invocation,model is not null?.9:.5);
        var bindingModel=model;
        if(localFunction is not null)
        {
            var parameters=(localFunction.Parameters??localFunction.Body.ParamBlock?.Parameters)?.ToArray()??[];
            bindingModel=new(localFunction.Name,parameters.Select((p,index)=>new PowerShellCommandCatalog.Parameter(p.Name.VariablePath.UserPath,index,
                Switch:p.StaticType==typeof(System.Management.Automation.SwitchParameter),Aliases:p.Attributes.OfType<AttributeAst>().Where(a=>a.TypeName.Name.Equals("Alias",StringComparison.OrdinalIgnoreCase)).SelectMany(a=>a.PositionalArguments.OfType<StringConstantExpressionAst>().Select(x=>x.Value)).ToArray())).ToArray());
        }
        var bound=Bind(command,state,bindingModel,pipeline,depth,localFunction is not null||native);
        Value argument(string key)=>bound.GetValueOrDefault(key)??Value.Empty;
        if(command.InvocationOperator==TokenKind.Dot)
        {
            if(command.CommandElements[0] is ScriptBlockExpressionAst inline)
            {Uncertain(command,"Dot-sourced inline block shares local state; external/session effects remain unknown.");return RunScript(inline.ScriptBlock,state,depth+1);}
            PowerShellManifest.Dependency(output,document,path,command,literal,"dot-source");
            Uncertain(command,"Dot-sourced file is recorded as a static dependency, never executed; its scope mutations are unresolved.");
            // Dot sourcing can overwrite any existing variable/alias. Preserve known flows and add unknown provenance.
            var unknown=Origin(command,"dot-source-scope",state.Scope,partial:true);
            foreach(var key in state.Variables.Keys.ToArray())state.Variables[key]=Merge(state.Variables[key],unknown);
            foreach(var key in PowerShellCommandCatalog.Aliases.Keys)state.Aliases[key]=null;
            return unknown;
        }
        if(localFunction is not null)
        {
            invoked.Add(localFunction);
            if(depth>=MaxDepth){Uncertain(command,"Local function call depth exceeded 6; recursion remains partial.");return At(Merge(bound.Values.ToArray()),command,"truncated-function-call",state,true);}
            var callee=new State(state.Scope+"/call:"+command.Extent.StartOffset+":"+(++invocations),state);
            BindParameters(localFunction.Parameters??localFunction.Body.ParamBlock?.Parameters,bound,callee,"function-parameter");Declare(localFunction.Body,callee);
            return Move(RunScript(localFunction.Body,callee,depth+1),commandId,command,"local-function-return");
        }
        if(name is null)
        {
            var target=Eval(command.CommandElements[0],state,depth);
            if(target.Block is not null&&depth<MaxDepth)
            {
                var blockState=new State(state.Scope+"/block:"+command.Extent.StartOffset+":"+(++invocations),state);
                BindParameters(target.Block.ParamBlock?.Parameters,bound,blockState,"scriptblock-parameter");Declare(target.Block,blockState);
                return Move(RunScript(target.Block,blockState,depth+1),commandId,command,"scriptblock-return");
            }
            Uncertain(command,"Dynamic invocation target is unresolved; command argument binding is a candidate.");
            Sink(Merge(target,Merge(bound.Values.ToArray())),commandId,command,"FM-PS-0007","Dynamic invocation target or argument","CWE-78","dynamic-target",true);
            return Move(Merge(target,Merge(bound.Values.ToArray())),commandId,command,"dynamic-command-output",true);
        }
        if(name.Equals("Import-Module",StringComparison.OrdinalIgnoreCase))
        {
            var value=argument("Name");if(value.Table is not null)value=TableRead(value,"ModuleName",state,command,"module-specification-read");
            PowerShellManifest.Dependency(output,document,path,command,value.Constant?.ToString(),"module-import");
            Uncertain(command,"Import-Module dependency is static only; imported commands/session mutations are not executed or resolved.");
        }
        if(name.Equals("Set-Alias",StringComparison.OrdinalIgnoreCase)||name.Equals("New-Alias",StringComparison.OrdinalIgnoreCase))
        {
            var aliasName=argument("Name").Constant?.ToString();var target=argument("Value").Constant?.ToString();
            if(aliasName is not null)state.Aliases[aliasName]=target;else{foreach(var key in PowerShellCommandCatalog.Aliases.Keys)state.Aliases[key]=null;Uncertain(command,"Dynamic alias mutation invalidates catalog aliases in this scope.");}
        }
        if(name.Equals("Invoke-Expression",StringComparison.OrdinalIgnoreCase))Sink(argument("Command"),commandId,command,"FM-PS-0001","Untrusted value reaches expression evaluation","CWE-95","expression-code",uncertain);
        if(name.Equals("Start-Process",StringComparison.OrdinalIgnoreCase))
            Sink(Merge(argument("FilePath"),argument("ArgumentList")),commandId,command,"FM-PS-0002","Untrusted process launch input","CWE-78","process-input",uncertain);
        if(name.Equals("Invoke-Command",StringComparison.OrdinalIgnoreCase))
        {
            Sink(Merge(argument("ComputerName"),argument("FilePath"),argument("ArgumentList"),argument("ScriptBlock")),commandId,command,"FM-PS-0003","Untrusted remoting input","CWE-78","remoting-input",true);
            var block=argument("ScriptBlock").Block;
            if(block is not null&&depth<MaxDepth)
            {
                var remote=new State(state.Scope+"/remote:"+command.Extent.StartOffset,state);remote.Variables["input"]=argument("InputObject");
                var args=new Dictionary<string,Value>{{"#0",argument("ArgumentList")}};
                BindParameters(block.ParamBlock?.Parameters,args,remote,"remoting-parameter");Declare(block,remote);return Move(RunScript(block,remote,depth+1),commandId,command,"remoting-output",true);
            }
            Uncertain(command,"Remoting environment and session command definitions are unresolved.");
        }
        if(name.Equals("Invoke-WebRequest",StringComparison.OrdinalIgnoreCase)||name.Equals("Invoke-RestMethod",StringComparison.OrdinalIgnoreCase))
        {
            Sink(argument("Uri"),commandId,command,"FM-PS-0004","Untrusted network destination","CWE-918","network-destination",uncertain);
            Sink(Merge(argument("Body"),argument("Headers")),commandId,command,"FM-PS-0008","Secret reaches network output","CWE-200","secret-egress",uncertain,secretOnly:true);
        }
        if(new[]{"Set-Content","Add-Content","Out-File"}.Contains(name,StringComparer.OrdinalIgnoreCase))
        {
            Sink(Merge(argument("Path"),argument("LiteralPath"),argument("FilePath")),commandId,command,"FM-PS-0005","Untrusted file write destination","CWE-73","file-path",uncertain);
            Sink(Merge(argument("Value"),argument("InputObject")),commandId,command,"FM-PS-0008","Secret reaches file output","CWE-200","secret-egress",uncertain,secretOnly:true);
        }
        if(new[]{"Set-ItemProperty","New-ItemProperty"}.Contains(name,StringComparer.OrdinalIgnoreCase))
            Sink(Merge(argument("Path"),argument("LiteralPath"),argument("Name"),argument("Value")),commandId,command,"FM-PS-0006","Untrusted registry or provider write","CWE-73","provider-write",uncertain);
        if(native)Sink(Merge(bound.Values.ToArray()),commandId,command,"FM-PS-0007","Untrusted native command argument","CWE-78","native-argument",true);
        if(name.Equals("ForEach-Object",StringComparison.OrdinalIgnoreCase)||name.Equals("Where-Object",StringComparison.OrdinalIgnoreCase))
        {
            var input=argument("InputObject");var block=argument(name.StartsWith("ForEach",StringComparison.OrdinalIgnoreCase)?"Process":"FilterScript").Block;
            if(block is not null&&depth<MaxDepth)
            {var nested=new State(state.Scope+"/pipeline:"+command.Extent.StartOffset,state);nested.Variables["_"]=input;nested.Variables["PSItem"]=input;Declare(block,nested);var result=RunScript(block,nested,depth+1);return Move(name.StartsWith("Where",StringComparison.OrdinalIgnoreCase)?input:result,commandId,command,"pipeline-scriptblock-output");}
            Uncertain(command,"Pipeline scriptblock cannot be statically resolved.");
        }
        if(model?.Source is not null)
        {
            var source=model.Source;
            if(name.Equals("Read-Host",StringComparison.OrdinalIgnoreCase)&&bound.ContainsKey("AsSecureString"))source="secret";
            var value=Origin(command,source,state.Scope);return Move(value,commandId,command,"command-source");
        }
        if(model?.PassThrough==true)return Move(Merge(bound.Values.ToArray()),commandId,command,"catalog-passthrough");
        if(model is null&&!native){Uncertain(command,"Command absent from controlled catalog: "+name);return Move(Merge(Origin(command,"unresolved-command-output",state.Scope,partial:true),Merge(bound.Values.ToArray())),commandId,command,"unknown-command-output",true);}
        if(new[]{"Join-Path","Resolve-Path"}.Contains(name,StringComparer.OrdinalIgnoreCase))return Move(Merge(bound.Values.ToArray()),commandId,command,"path-composition");
        return Value.Empty;
    }
    private Dictionary<string,Value> Bind(CommandAst command,State state,PowerShellCommandCatalog.Command? model,Value pipeline,int depth,bool freeNamed)
    {
        var result=new Dictionary<string,Value>(StringComparer.OrdinalIgnoreCase);int position=0;
        string Resolve(string input)
        {
            if(model is null)return input;
            var exact=model.Parameters.FirstOrDefault(x=>x.Name.Equals(input,StringComparison.OrdinalIgnoreCase)||(x.Aliases??[]).Contains(input,StringComparer.OrdinalIgnoreCase));
            if(exact is not null)return exact.Name;
            var prefix=model.Parameters.Where(x=>x.Name.StartsWith(input,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(prefix.Length==1)return prefix[0].Name;
            if(!new[]{"ErrorAction","ErrorVariable","WarningAction","Verbose","Debug","InformationAction","OutVariable"}.Contains(input,StringComparer.OrdinalIgnoreCase))Uncertain(command,"Unresolved or ambiguous parameter -"+input+" on "+model.Name);
            return "?"+input;
        }
        void Add(string name,Value value,Ast location,bool partial=false)
        {
            var id=PowerShellExtractor.Id(path,location,"ps-binding",state.Scope+":"+name);var properties=PowerShellExtractor.Properties(location,"parameter-binding");properties["parameterName"]=name;properties["command"]=model?.Name;
            output.Add(new(id,"Parameter",name,path,properties));var moved=Move(value,id,location,"parameter-binding:"+name,partial);
            result[name]=result.TryGetValue(name,out var old)?Merge(old,moved):moved;
        }
        for(int i=1;i<command.CommandElements.Count;i++)
        {
            var element=command.CommandElements[i];
            if(element is VariableExpressionAst{Splatted:true} splat)
            {
                var value=Eval(splat,state,depth);
                if(value.Table is not null)
                {
                    var tables=value.Table.Ids.Where(state.Tables.ContainsKey).Select(id=>state.Tables[id]).ToArray();
                    foreach(var key in tables.SelectMany(t=>t.Fields.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if(!Budget(element))break;
                        Add(Resolve(key),TableRead(value,key,state,element,"splat-entry:"+key),element,!value.Table.Exact);
                    }
                    var unknown=Merge([new(value.Table.OtherTraces),..tables.Select(t=>Materialize(t.Unknown,state,element))]);
                    if(HasValue(unknown))
                    {
                        Uncertain(element,"Unknown hashtable entries remain candidates for every catalog parameter.");
                        if(model is not null)foreach(var candidate in model.Parameters.Where(p=>!p.Switch))Add(candidate.Name,unknown,element,true);
                        else Add("#"+(position++),unknown,element,true);
                    }
                }
                else
                {
                    Uncertain(element,"Dynamic splat cannot be bound; its value is a candidate for every catalog parameter.");
                    if(model is not null)foreach(var candidate in model.Parameters.Where(p=>!p.Switch))Add(candidate.Name,value,element,true);
                    else Add("#"+(position++),value,element,true);
                }
                continue;
            }
            if(element is CommandParameterAst parameter)
            {
                var name=Resolve(parameter.ParameterName);var spec=model?.Parameters.FirstOrDefault(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
                Value value;
                if(parameter.Argument is not null)value=Eval(parameter.Argument,state,depth);
                else if(spec?.Switch==true)value=Origin(element,"constant",state.Scope,true);
                else if(i+1<command.CommandElements.Count&&command.CommandElements[i+1] is not CommandParameterAst)value=Eval(command.CommandElements[++i],state,depth);
                else value=Origin(element,"constant",state.Scope,true);
                Add(name,value,element,model is null&&!freeNamed);continue;
            }
            var positional=model?.Parameters.FirstOrDefault(p=>p.Position==position)?.Name??"#"+position;
            Add(positional,Eval(element,state,depth),element,model is not null&&positional.StartsWith('#'));position++;
        }
        if(pipeline.Traces.Count>0)
        {
            var target=model?.Parameters.FirstOrDefault(p=>p.Pipeline)?.Name??"#pipeline";
            if(!result.ContainsKey(target))Add(target,pipeline,command,model is null);
            else Uncertain(command,"Explicit parameter supersedes catalog pipeline binding; actual parameter-set dispatch is not executed.");
        }
        return result;
    }
    private void Sink(Value value,string commandId,CommandAst command,string rule,string title,string cwe,string sink,bool partial,bool secretOnly=false)
    {
        var moved=Move(value,commandId,command,"sink:"+sink,partial);
        foreach(var trace in moved.Traces.Where(t=>t.Kind!="constant"&&(!secretOnly||t.Kind=="secret")&&!(sink=="expression-code"&&t.Numeric)))
        {
            var key=commandId+":"+rule+":"+trace.Steps[0].NodeId;if(!findingKeys.Add(key))continue;
            output.AddFinding(new(Identity.For("finding",snapshot+":"+key),rule,"warning",title,path,command.Extent.StartLineNumber,
                $"Modeled {trace.Kind} value flows through ordered local definitions and parameter binding to {sink}. This is a source-to-sink witness under the stated catalog and scope assumptions, not proof of runtime exploitability.",trace.Partial?.6:.9,trace.Evidence,cwe)
            {
                Status=trace.Partial?"candidate-flow":"modeled-flow",ModelVersion=PowerShellCommandCatalog.Version,Completeness=trace.Partial||trace.Truncated?"partial":"complete",Route=trace.Steps,
                Truncated=trace.Truncated,TruncationReason=trace.Truncated?"Local route exceeded 64 steps or 16 alternative traces.":null,
                Assumptions=["Controlled catalog commands/aliases have their documented behavior unless a modeled local override is present.","Flow is local and context-bounded; no code, module, or analyzer executes.","The source's trust and the destination's operational policy require review."]
            });
        }
    }
}

