using System.Collections.Immutable;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Taint = System.Collections.Immutable.ImmutableHashSet<Decisya.ArchitectureTests.Elem>;

namespace Decisya.ArchitectureTests;

/// <summary>
/// The per-method simulation of <see cref="PersonalDataLogRule"/> (issue #230, G2 "Check design"): an abstract
/// interpretation of the IL stack and of the locals, arguments, allocations and fields. Parameters start as symbolic
/// elements, so one run yields both the findings inside the method and its <see cref="Summary"/> (what flows to its
/// return value, into its arguments, and to a sink), which callers instantiate at each direct call (S5).
/// </summary>
/// <remarks>
/// Locals, arguments and allocations are flow-insensitive cells (union over the method, iterated to a fixed point).
/// The operand stack is merged at every branch target. Fields of compiler-generated types, of Decisya types and
/// static fields are a flow-insensitive store shared by every method (concrete sources only).
/// </remarks>
internal sealed class MethodRun
{
    private readonly PersonalDataWorld _w;
    private readonly MethodDefinition _m;
    private readonly string _site;
    private readonly Dictionary<string, Taint> _cells = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Taint> _initial = [];
    private readonly HashSet<string> _consoleCells = new(StringComparer.Ordinal);
    private readonly Dictionary<Instruction, int> _index = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<SinkUse> _uses = [];
    private readonly List<(int Offset, int Line, string File)> _points = [];
    private Taint _return = Taint.Empty;
    private bool _changed;
    private TypeReference? _constrained;

    public MethodRun(PersonalDataWorld world, MethodDefinition method)
    {
        _w = world;
        _m = method;
        _site = SiteOf(method);

        var instructions = method.Body.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            _index[instructions[i]] = i;
        }

        foreach (var point in method.DebugInformation.SequencePoints.Where(p => !p.IsHidden).OrderBy(p => p.Offset))
        {
            _points.Add((point.Offset, point.StartLine, PersonalDataWorld.RelativePath(point.Document?.Url)));
        }

        var count = method.Parameters.Count + (method.HasThis ? 1 : 0);
        for (var k = 0; k < count; k++)
        {
            var initial = Taint.Empty.Add(Elem.OfParam(k));
            if (_w.SourceOfType(ArgType(k)) is { } typeSource)
            {
                initial = initial.Add(Elem.Of(typeSource));
            }

            _initial[k] = initial;
            _cells[$"A{k}"] = initial;
        }
    }

    /// <summary>The credited site of a method: <c>Namespace.Type::Method</c>; a closure or state machine counts as its declaring method.</summary>
    internal static string SiteOf(MethodDefinition method)
    {
        var credited = CompilerGeneratedTypeWalk.CreditedType(method.DeclaringType);
        return CompilerGeneratedTypeWalk.ReflectionStyleFullName(credited) + "::" + OwnerMethodName(method);
    }

    private static string OwnerMethodName(MethodDefinition method)
    {
        if (!CompilerGeneratedTypeWalk.IsCompilerGenerated(method.DeclaringType))
        {
            return method.Name;
        }

        foreach (var name in new[] { method.Name, method.DeclaringType.Name })
        {
            // <Outer>b__0_0, <Outer>d__3 and the nested <<Outer>g__Local|0>d: the name between the leading '<' and the next '>'.
            var start = 0;
            while (start < name.Length && name[start] == '<')
            {
                start++;
            }

            var end = name.IndexOf('>', start);
            if (start > 0 && end > start)
            {
                return name[start..end];
            }
        }

        return method.Name;
    }

    public Summary Run()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            _changed = false;
            Simulate();
            if (!_changed)
            {
                break;
            }
        }

        var summary = new Summary { Return = _return };
        foreach (var (index, initial) in _initial)
        {
            var extra = CellGet($"A{index}").Except(initial);
            if (!extra.IsEmpty)
            {
                summary.ArgOut[index] = extra;
            }
        }

        foreach (var use in _uses)
        {
            summary.Uses.Add(use);
        }

        return summary;
    }

    // ------------------------------------------------------------------------------------------ values and cells

    private sealed record Val(Taint T, TypeReference? Type, string? Const = null, string? Cell = null, bool Console = false);

    private Taint CellGet(string? cell)
    {
        if (cell is null)
        {
            return Taint.Empty;
        }

        if (cell.StartsWith("G:", StringComparison.Ordinal))
        {
            return _w.GlobalGet(cell[2..]);
        }

        return _cells.TryGetValue(cell, out var taint) ? taint : Taint.Empty;
    }

    private void CellAdd(string? cell, Taint add)
    {
        if (cell is null || add.IsEmpty)
        {
            return;
        }

        if (cell.StartsWith("G:", StringComparison.Ordinal))
        {
            _w.GlobalAdd(cell[2..], add);
            return;
        }

        var current = CellGet(cell);
        var next = current.Union(add);
        if (next.Count != current.Count)
        {
            _cells[cell] = next;
            _changed = true;
        }
    }

    private Taint Eff(Val value) => value.Cell is null ? value.T : value.T.Union(CellGet(value.Cell));

    private static Val Pop(List<Val> stack)
    {
        if (stack.Count == 0)
        {
            return new Val(Taint.Empty, null);
        }

        var value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    private Taint WithTypeSource(Taint taint, TypeReference? type)
    {
        if (_w.SourceOfType(type) is { } source)
        {
            Seen(source);
            return taint.Add(Elem.Of(source));
        }

        return taint;
    }

    private void Seen(TaintSource source) => _w.Stats.SourcesSeen.Add($"{_site} <- {source.Label}");

    /// <summary>Reading member <paramref name="member"/> of a value: the taint held in that field, plus the value's own direct taint.</summary>
    internal static Taint ReadMember(Taint taint, string member)
    {
        var builder = Taint.Empty.ToBuilder();
        foreach (var element in taint)
        {
            if (element.Held.Length > 0)
            {
                // Held is a dotted path: "Field" or "Field.Inner.Deeper".
                if (string.Equals(element.Held, member, StringComparison.Ordinal))
                {
                    builder.Add(element with { Held = string.Empty });
                }
                else if (element.Held.StartsWith(member + ".", StringComparison.Ordinal))
                {
                    builder.Add(element with { Held = element.Held[(member.Length + 1)..] });
                }
            }
            else if (element.Param >= 0 && element.From.Length == 0)
            {
                builder.Add(element with { From = member });
            }
            else
            {
                builder.Add(element);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>A summary element instantiated at a call site: symbolic parameters become the arguments' taint.</summary>
    private Taint Subst(Elem element, Val[] all)
    {
        if (element.Param < 0)
        {
            return Taint.Empty.Add(element);
        }

        if (element.Param >= all.Length)
        {
            return Taint.Empty;
        }

        var argument = Eff(all[element.Param]);
        var selected = element.From.Length == 0 ? argument : ReadMember(argument, element.From);
        var builder = Taint.Empty.ToBuilder();
        foreach (var inner in selected)
        {
            builder.Add(inner with { Held = Join(element.Held, inner.Held) });
        }

        return builder.ToImmutable();
    }

    /// <summary>The path of a value held at <paramref name="outer"/> that was itself held at <paramref name="inner"/>.</summary>
    private static string Join(string outer, string inner)
    {
        var joined = outer.Length == 0 ? inner : inner.Length == 0 ? outer : outer + "." + inner;

        // Capped at three segments (the processor's MaxRenderDepth), so a loop that stores a value into its own field
        // reaches a fixed point instead of growing a path forever.
        var segments = joined.Split('.');
        return segments.Length <= MaxPathSegments ? joined : string.Join('.', segments.Take(MaxPathSegments));
    }

    private const int MaxPathSegments = 2;

    private TypeReference? ArgType(int index)
    {
        if (_m.HasThis)
        {
            return index == 0 ? _m.DeclaringType : _m.Parameters[index - 1].ParameterType;
        }

        return _m.Parameters[index].ParameterType;
    }

    // ------------------------------------------------------------------------------------------ the simulation

    private void Simulate()
    {
        var instructions = _m.Body.Instructions;
        var states = new Dictionary<int, Val[]>();
        var work = new Stack<int>();

        void Enter(int target, List<Val> stack)
        {
            if (!states.TryGetValue(target, out var existing))
            {
                states[target] = [.. stack];
                work.Push(target);
                return;
            }

            if (existing.Length != stack.Count)
            {
                return;
            }

            var changed = false;
            var merged = new Val[existing.Length];
            for (var k = 0; k < existing.Length; k++)
            {
                var a = existing[k];
                var b = stack[k];
                var sameCell = string.Equals(a.Cell, b.Cell, StringComparison.Ordinal);
                var taint = sameCell ? a.T.Union(b.T) : Eff(a).Union(Eff(b));
                var type = string.Equals(a.Type?.FullName, b.Type?.FullName, StringComparison.Ordinal) ? a.Type : null;
                var constant = string.Equals(a.Const, b.Const, StringComparison.Ordinal) ? a.Const : null;
                var cell = sameCell ? a.Cell : null;
                merged[k] = new Val(taint, type, constant, cell, a.Console && b.Console);
                changed |= taint.Count != a.T.Count
                    || !string.Equals(type?.FullName, a.Type?.FullName, StringComparison.Ordinal)
                    || !string.Equals(constant, a.Const, StringComparison.Ordinal)
                    || !string.Equals(cell, a.Cell, StringComparison.Ordinal)
                    || merged[k].Console != a.Console;
            }

            if (changed)
            {
                states[target] = merged;
                work.Push(target);
            }
        }

        Enter(0, []);
        foreach (var handler in _m.Body.ExceptionHandlers)
        {
            var exception = new List<Val> { new(Taint.Empty, handler.CatchType) };
            switch (handler.HandlerType)
            {
                case ExceptionHandlerType.Catch:
                    Enter(_index[handler.HandlerStart], exception);
                    break;
                case ExceptionHandlerType.Filter:
                    Enter(_index[handler.FilterStart], exception);
                    Enter(_index[handler.HandlerStart], [.. exception]);
                    break;
                default:
                    Enter(_index[handler.HandlerStart], []);
                    break;
            }
        }

        var visits = 0;
        while (work.Count > 0 && visits++ < 50_000)
        {
            if (visits % 1000 == 0)
            {
                _w.CheckBudget(_site);
            }

            var current = work.Pop();
            var stack = new List<Val>(states[current]);
            foreach (var successor in Execute(instructions[current], current, stack))
            {
                Enter(successor, stack);
            }
        }
    }

    private List<int> Execute(Instruction ins, int position, List<Val> st)
    {
        Step(ins, st);

        var successors = new List<int>(2);
        var instructions = _m.Body.Instructions;
        switch (ins.OpCode.FlowControl)
        {
            case FlowControl.Branch:
                if (ins.OpCode.Code is Code.Leave or Code.Leave_S)
                {
                    st.Clear();
                }

                if (ins.Operand is Instruction target)
                {
                    successors.Add(_index[target]);
                }

                break;
            case FlowControl.Cond_Branch:
                if (position + 1 < instructions.Count)
                {
                    successors.Add(position + 1);
                }

                if (ins.Operand is Instruction single)
                {
                    successors.Add(_index[single]);
                }
                else if (ins.Operand is Instruction[] many)
                {
                    successors.AddRange(many.Select(t => _index[t]));
                }

                break;
            case FlowControl.Return:
            case FlowControl.Throw:
                break;
            default:
                if (position + 1 < instructions.Count)
                {
                    successors.Add(position + 1);
                }

                break;
        }

        return successors;
    }

    private void Step(Instruction ins, List<Val> st)
    {
        var code = ins.OpCode.Code;
        switch (code)
        {
            case Code.Nop:
            case Code.Break:
            case Code.Volatile:
            case Code.Unaligned:
            case Code.Tail:
            case Code.Readonly:
            case Code.No:
                break;
            case Code.Constrained:
                _constrained = (TypeReference)ins.Operand;
                break;

            case Code.Ldarg_0:
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
            case Code.Ldarg:
            case Code.Ldarg_S:
            case Code.Ldarga:
            case Code.Ldarga_S:
            {
                var index = ArgIndex(ins);
                var cell = $"A{index}";
                st.Add(new Val(Taint.Empty, index < _initial.Count ? ArgType(index) : null, null, cell, _consoleCells.Contains(cell)));
                break;
            }

            case Code.Starg:
            case Code.Starg_S:
            {
                var value = Pop(st);
                CellAdd($"A{ArgIndex(ins)}", Eff(value));
                break;
            }

            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            case Code.Ldloc:
            case Code.Ldloc_S:
            case Code.Ldloca:
            case Code.Ldloca_S:
            {
                var index = LocalIndex(ins);
                var cell = $"L{index}";
                st.Add(new Val(Taint.Empty, _m.Body.Variables[index].VariableType, null, cell, _consoleCells.Contains(cell)));
                break;
            }

            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
            case Code.Stloc:
            case Code.Stloc_S:
            {
                var value = Pop(st);
                var cell = $"L{LocalIndex(ins)}";
                CellAdd(cell, Eff(value));
                if (value.Console)
                {
                    _consoleCells.Add(cell);
                }

                break;
            }

            case Code.Ldnull:
            case Code.Ldc_I4:
            case Code.Ldc_I4_S:
            case Code.Ldc_I4_M1:
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_2:
            case Code.Ldc_I4_3:
            case Code.Ldc_I4_4:
            case Code.Ldc_I4_5:
            case Code.Ldc_I4_6:
            case Code.Ldc_I4_7:
            case Code.Ldc_I4_8:
            case Code.Ldc_I8:
            case Code.Ldc_R4:
            case Code.Ldc_R8:
            case Code.Ldtoken:
            case Code.Ldftn:
            case Code.Sizeof:
            case Code.Arglist:
                st.Add(new Val(Taint.Empty, null));
                break;

            case Code.Ldstr:
                st.Add(new Val(Taint.Empty, _m.Module.TypeSystem.String, (string)ins.Operand));
                break;

            case Code.Dup:
            {
                var top = st.Count > 0 ? st[^1] : new Val(Taint.Empty, null);
                st.Add(top);
                break;
            }

            case Code.Pop:
                Pop(st);
                break;

            case Code.Call:
            case Code.Callvirt:
                Call(ins, st, isNewobj: false);
                break;
            case Code.Newobj:
                Call(ins, st, isNewobj: true);
                break;
            case Code.Calli:
            {
                var site = (CallSite)ins.Operand;
                var union = Taint.Empty;
                for (var k = 0; k < site.Parameters.Count + 1 + (site.HasThis ? 1 : 0); k++)
                {
                    union = union.Union(Eff(Pop(st)));
                }

                if (!string.Equals(site.ReturnType.FullName, "System.Void", StringComparison.Ordinal))
                {
                    st.Add(new Val(union, site.ReturnType));
                }

                break;
            }

            case Code.Ldfld:
            case Code.Ldflda:
            {
                var field = (FieldReference)ins.Operand;
                var receiver = Pop(st);
                st.Add(LoadField(field, Eff(receiver), code == Code.Ldflda));
                break;
            }

            case Code.Ldsfld:
            case Code.Ldsflda:
                st.Add(LoadField((FieldReference)ins.Operand, Taint.Empty, code == Code.Ldsflda));
                break;

            case Code.Stfld:
            {
                var field = (FieldReference)ins.Operand;
                var value = Pop(st);
                var receiver = Pop(st);
                StoreField(field, Eff(value), receiver.Cell);
                break;
            }

            case Code.Stsfld:
            {
                var field = (FieldReference)ins.Operand;
                var value = Pop(st);
                StoreField(field, Eff(value), null);
                break;
            }

            case Code.Ldelem_Any:
            case Code.Ldelem_I:
            case Code.Ldelem_I1:
            case Code.Ldelem_I2:
            case Code.Ldelem_I4:
            case Code.Ldelem_I8:
            case Code.Ldelem_R4:
            case Code.Ldelem_R8:
            case Code.Ldelem_Ref:
            case Code.Ldelem_U1:
            case Code.Ldelem_U2:
            case Code.Ldelem_U4:
            case Code.Ldelema:
            {
                Pop(st);
                var array = Pop(st);
                var elementType = array.Type is ArrayType arrayType ? arrayType.ElementType : null;
                st.Add(new Val(Eff(array), elementType, null, code == Code.Ldelema ? array.Cell : null));
                break;
            }

            case Code.Stelem_Any:
            case Code.Stelem_I:
            case Code.Stelem_I1:
            case Code.Stelem_I2:
            case Code.Stelem_I4:
            case Code.Stelem_I8:
            case Code.Stelem_R4:
            case Code.Stelem_R8:
            case Code.Stelem_Ref:
            {
                var value = Pop(st);
                Pop(st);
                var array = Pop(st);
                CellAdd(array.Cell, StoreTaint(value));
                break;
            }

            case Code.Stind_I:
            case Code.Stind_I1:
            case Code.Stind_I2:
            case Code.Stind_I4:
            case Code.Stind_I8:
            case Code.Stind_R4:
            case Code.Stind_R8:
            case Code.Stind_Ref:
            case Code.Stobj:
            {
                var value = Pop(st);
                var address = Pop(st);
                CellAdd(address.Cell, StoreTaint(value));
                break;
            }

            case Code.Ldind_I:
            case Code.Ldind_I1:
            case Code.Ldind_I2:
            case Code.Ldind_I4:
            case Code.Ldind_I8:
            case Code.Ldind_R4:
            case Code.Ldind_R8:
            case Code.Ldind_Ref:
            case Code.Ldind_U1:
            case Code.Ldind_U2:
            case Code.Ldind_U4:
            {
                var address = Pop(st);
                st.Add(new Val(Eff(address), null));
                break;
            }

            case Code.Ldobj:
            {
                var address = Pop(st);
                st.Add(new Val(Eff(address), (TypeReference)ins.Operand));
                break;
            }

            case Code.Box:
            case Code.Unbox_Any:
            case Code.Castclass:
            case Code.Isinst:
            case Code.Unbox:
            {
                var value = Pop(st);
                var type = ins.Operand as TypeReference;
                var keep = type is null or GenericParameter ? value.Type : type;
                var boxed = code == Code.Box;
                st.Add(new Val(boxed ? Eff(value) : value.T, keep, value.Const, boxed ? null : value.Cell, value.Console));
                break;
            }

            case Code.Newarr:
            {
                Pop(st);
                st.Add(new Val(Taint.Empty, new ArrayType((TypeReference)ins.Operand), null, $"N{ins.Offset}"));
                break;
            }

            case Code.Ldlen:
            case Code.Ceq:
            case Code.Cgt:
            case Code.Cgt_Un:
            case Code.Clt:
            case Code.Clt_Un:
                Pop(st);
                if (code is not Code.Ldlen)
                {
                    Pop(st);
                }

                st.Add(new Val(Taint.Empty, null));
                break;

            case Code.Ret:
                if (!string.Equals(_m.ReturnType.FullName, "System.Void", StringComparison.Ordinal))
                {
                    var returned = Eff(Pop(st));
                    var next = _return.Union(returned);
                    if (next.Count != _return.Count)
                    {
                        _return = next;
                        _changed = true;
                    }
                }

                break;

            default:
                Generic(ins, st);
                break;
        }
    }

    private void Generic(Instruction ins, List<Val> st)
    {
        var pops = PopCount(ins.OpCode.StackBehaviourPop);
        var pushes = PushCount(ins.OpCode.StackBehaviourPush);
        var union = Taint.Empty;
        for (var k = 0; k < pops; k++)
        {
            union = union.Union(Eff(Pop(st)));
        }

        for (var k = 0; k < pushes; k++)
        {
            st.Add(new Val(union, null));
        }
    }

    private static int PopCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
            => 3,
        _ => 0,
    };

    private static int PushCount(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
            or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => 0,
    };

    private int ArgIndex(Instruction ins) => ins.OpCode.Code switch
    {
        Code.Ldarg_0 => 0,
        Code.Ldarg_1 => 1,
        Code.Ldarg_2 => 2,
        Code.Ldarg_3 => 3,
        _ => ins.Operand is ParameterDefinition parameter ? (_m.HasThis ? parameter.Index + 1 : parameter.Index) : 0,
    };

    private static int LocalIndex(Instruction ins) => ins.OpCode.Code switch
    {
        Code.Ldloc_0 or Code.Stloc_0 => 0,
        Code.Ldloc_1 or Code.Stloc_1 => 1,
        Code.Ldloc_2 or Code.Stloc_2 => 2,
        Code.Ldloc_3 or Code.Stloc_3 => 3,
        _ => ((VariableDefinition)ins.Operand).Index,
    };

    // ------------------------------------------------------------------------------------------ fields

    private static string FieldKey(FieldReference field) => PersonalDataWorld.Reflect(field.DeclaringType) + "::" + field.Name;

    private static bool IsSharedField(FieldReference field) =>
        PersonalDataWorld.IsDecisyaName(field.DeclaringType) || field.DeclaringType.Name.StartsWith('<');

    private static bool IsGeneratedField(FieldReference field) => field.DeclaringType.Name.StartsWith('<');

    private Val LoadField(FieldReference field, Taint receiver, bool address)
    {
        // A hoisted local or closure field is a variable of its family, not a member of an object: only the shared store.
        var taint = IsGeneratedField(field) ? Taint.Empty : ReadMember(receiver, PersonalDataWorld.Norm(field.Name));
        string? cell = null;
        var shared = IsSharedField(field) || PersonalDataWorld.IsStaticField(field);
        if (shared)
        {
            taint = taint.Union(_w.GlobalGet(FieldKey(field)));
            if (address)
            {
                cell = "G:" + FieldKey(field);
            }
        }

        if (_w.SourceOfField(field) is { } source)
        {
            Seen(source);
            taint = taint.Add(Elem.Of(source));
        }

        if (_w.IsNonPersonalKind(field.FieldType))
        {
            taint = Taint.Empty;
        }

        return new Val(WithTypeSource(taint, field.FieldType), field.FieldType, null, cell);
    }

    private void StoreField(FieldReference field, Taint value, string? receiverCell)
    {
        var name = PersonalDataWorld.Norm(field.Name);
        if (receiverCell is not null && !IsGeneratedField(field))
        {
            CellAdd(receiverCell, value.Select(e => e with { Held = Join(name, e.Held) }).ToImmutableHashSet());
        }

        if (IsSharedField(field) || PersonalDataWorld.IsStaticField(field))
        {
            _w.GlobalAdd(FieldKey(field), value);
        }
    }

    // ------------------------------------------------------------------------------------------ whole-object judgement

    private bool Excused(TypeReference? type, Elem element)
    {
        if (_w.DecisyaDefinition(type) is not { } definition)
        {
            return false;
        }

        // The first segment of the dotted path is the member of this object that holds the taint.
        var first = element.Held.Split('.')[0];
        return _w.TypeIsSensitive(definition)
            || (first.Length > 0 && _w.PathIsSensitive(definition, first));
    }

    /// <summary>
    /// What a Decisya object contributes when it is stored into a container or a boxed array: the taint its [Sensitive]
    /// type or members mask is dropped, and the personal members it has without [Sensitive] are added.
    /// </summary>
    private Taint StoreTaint(Val value)
    {
        var taint = Eff(value);
        if (_w.DecisyaDefinition(value.Type) is not { } definition)
        {
            return taint;
        }

        var builder = Taint.Empty.ToBuilder();
        foreach (var element in taint)
        {
            if (!Excused(value.Type, element))
            {
                builder.Add(element);
            }
        }

        foreach (var member in _w.UnsanitizedMembers(definition))
        {
            builder.Add(Elem.Of(member));
        }

        return builder.ToImmutable();
    }

    private void Judge(Val value, string sink, Instruction ins)
    {
        foreach (var element in Eff(value))
        {
            HandleElement(element, value.Type, sink, ins);
        }

        if (_w.DecisyaDefinition(value.Type) is { } definition)
        {
            foreach (var member in _w.UnsanitizedMembers(definition))
            {
                Report(member, sink, ins);
            }
        }
    }

    private void HandleElement(Elem element, TypeReference? argumentType, string sink, Instruction ins)
    {
        if (element.Src is null)
        {
            if (_uses.Add(new SinkUse(element, argumentType, sink)))
            {
                _changed = true;
            }

            return;
        }

        if (!Excused(argumentType, element))
        {
            Report(element.Src, sink, ins);
        }
    }

    private void Report(TaintSource source, string sink, Instruction ins)
    {
        var (file, line) = Location(ins);
        _w.AddFinding(new RawFinding(_site, file, line, source, sink));
    }

    private (string File, int Line) Location(Instruction ins)
    {
        if (_points.Count == 0)
        {
            return ("unknown", 0);
        }

        var best = _points[0];
        foreach (var point in _points)
        {
            if (point.Offset <= ins.Offset)
            {
                best = point;
            }
            else
            {
                break;
            }
        }

        return (best.File, best.Line);
    }

    // ------------------------------------------------------------------------------------------ calls

    private sealed record SinkInfo(string Label, IReadOnlyList<int> Indexes, bool LoggerMessage);

    private void Call(Instruction ins, List<Val> st, bool isNewobj)
    {
        var reference = IlScan.MethodOperand(ins.Operand)!;
        var generic = ins.Operand as GenericInstanceMethod;
        var constrained = _constrained;
        _constrained = null;

        var definition = _w.ResolveMethod(reference);
        var args = new Val[reference.Parameters.Count];
        for (var k = args.Length - 1; k >= 0; k--)
        {
            args[k] = Pop(st);
        }

        Val? receiver = null;
        string? allocation = null;
        if (isNewobj)
        {
            allocation = $"N{ins.Offset}";
            receiver = new Val(Taint.Empty, reference.DeclaringType, null, allocation);
        }
        else if (reference.HasThis)
        {
            receiver = Pop(st);
            if (constrained is not null)
            {
                receiver = receiver with { Type = constrained };
            }
        }

        var all = receiver is null ? args : [receiver, .. args];

        if (ClassifySink(reference, definition, all, isNewobj) is { } sink)
        {
            if (sink.LoggerMessage)
            {
                _w.Stats.LoggerMessageCallSites.Add($"{_site} -> {PersonalDataWorld.Reflect(reference.DeclaringType)}::{reference.Name}");
            }

            foreach (var index in sink.Indexes.Where(i => i < all.Length))
            {
                Judge(all[index], sink.Label, ins);
            }
        }

        var source = SourceOfInvocation(reference, definition, all);
        if (source is not null)
        {
            Seen(source);
        }

        var result = Taint.Empty;
        if (_w.IsMasker(reference))
        {
            source = null;
        }
        else if (definition is not null && _w.InScope(definition) && definition.HasBody && Followable(definition, isNewobj))
        {
            var summary = _w.GetSummary(definition);
            foreach (var use in summary.Uses)
            {
                foreach (var element in Subst(use.E, all))
                {
                    HandleElement(element, use.ArgType, use.Sink, ins);
                }
            }

            foreach (var element in summary.Return)
            {
                result = result.Union(Subst(element, all));
            }

            foreach (var (index, outs) in summary.ArgOut)
            {
                if (index < all.Length && all[index].Cell is { } cell)
                {
                    foreach (var element in outs)
                    {
                        CellAdd(cell, Subst(element, all));
                    }
                }
            }
        }
        else
        {
            result = Opaque(reference, definition, all, receiver, isNewobj, allocation);
        }

        if (source is not null)
        {
            var sourceTaint = Taint.Empty.Add(Elem.Of(source));
            result = result.Union(sourceTaint);
            for (var k = 0; k < args.Length; k++)
            {
                if (reference.Parameters[k].ParameterType.IsByReference)
                {
                    CellAdd(args[k].Cell, sourceTaint);
                }
            }
        }

        if (isNewobj)
        {
            st.Add(new Val(Taint.Empty, reference.DeclaringType, null, allocation));
            return;
        }

        var returnType = ReturnTypeOf(reference, generic);
        if (string.Equals(reference.ReturnType.FullName, "System.Void", StringComparison.Ordinal))
        {
            return;
        }

        if (_w.IsNonPersonalKind(returnType) || reference.Name is "get_Length" or "get_Count" || _w.IsMasker(reference))
        {
            result = Taint.Empty;
        }

        var console = string.Equals(PersonalDataWorld.Reflect(reference.DeclaringType), "System.Console", StringComparison.Ordinal)
            && reference.Name is "get_Out" or "get_Error";

        // A by-ref return, and a fluent call on a builder (StringBuilder.Append returns the same object), stay the receiver's cell.
        var fluent = receiver is not null
            && !string.Equals(PersonalDataWorld.Reflect(reference.DeclaringType), "System.String", StringComparison.Ordinal)
            && string.Equals(PersonalDataWorld.Reflect(reference.ReturnType), PersonalDataWorld.Reflect(reference.DeclaringType), StringComparison.Ordinal);
        var cellOfResult = (reference.ReturnType.IsByReference || fluent) && all.Length > 0 ? all[0].Cell : null;
        st.Add(new Val(WithTypeSource(result, returnType), returnType, null, cellOfResult, console));
    }

    private static bool Followable(MethodDefinition definition, bool isNewobj) =>
        isNewobj || !definition.IsVirtual || definition.IsFinal || definition.DeclaringType.IsSealed;

    private static TypeReference? ReturnTypeOf(MethodReference reference, GenericInstanceMethod? generic)
    {
        var type = reference.ReturnType;
        if (type is GenericParameter parameter)
        {
            if (parameter.Type == GenericParameterType.Method && generic is not null && parameter.Position < generic.GenericArguments.Count)
            {
                return generic.GenericArguments[parameter.Position];
            }

            if (parameter.Type == GenericParameterType.Type && reference.DeclaringType is GenericInstanceType owner && parameter.Position < owner.GenericArguments.Count)
            {
                return owner.GenericArguments[parameter.Position];
            }

            return null;
        }

        return type;
    }

    /// <summary>A call whose body is not followed: the result carries the union of what goes in; containers and builders absorb their arguments.</summary>
    private Taint Opaque(MethodReference reference, MethodDefinition? definition, Val[] all, Val? receiver, bool isNewobj, string? allocation)
    {
        var declaring = PersonalDataWorld.Reflect(reference.DeclaringType);

        // Async plumbing moves no data (G4 narrowing N-02): Start, AwaitOnCompleted and AwaitUnsafeOnCompleted only schedule
        // the continuation. Treated as a data flow, "ref this" (the state machine) and the awaiter cell absorb the builder's
        // store, so a record that came back through SetResult leaks into every awaited value and, through a factory such
        // as Failed(code), into its other members. SetResult, SetException and get_Task keep their normal handling.
        if (receiver is not null && !isNewobj && IsAsyncSchedulingCall(declaring, reference.Name))
        {
            return Taint.Empty;
        }

        var container = receiver is not null && !isNewobj
            && declaring.StartsWith("System.Collections", StringComparison.Ordinal);

        var inputs = new List<Taint>(all.Length);
        for (var k = 0; k < all.Length; k++)
        {
            var isReceiver = receiver is not null && k == 0;

            // An out argument is only written by the callee: what its variable held before is not an input, so it must not
            // flow into the receiver, the allocation or the other arguments (the Try-reader shape, G4 narrowing N-01).
            var isOut = !isReceiver && IsOutParameter(reference, definition, k - (receiver is null ? 0 : 1));
            inputs.Add(isOut ? Taint.Empty : ArgumentTaint(reference, all[k], k, isReceiver, container && !isReceiver));
        }

        var union = inputs.Aggregate(Taint.Empty, (acc, item) => acc.Union(item));

        // The arguments only (never the receiver itself) go into the receiver's cell and the by-ref arguments' cells.
        var argumentsOnly = Taint.Empty;
        for (var k = receiver is null ? 0 : 1; k < all.Length; k++)
        {
            argumentsOnly = argumentsOnly.Union(inputs[k]);
        }

        if (isNewobj)
        {
            CellAdd(allocation, argumentsOnly);
        }
        else if (receiver?.Cell is { } receiverCell && !string.Equals(declaring, "System.String", StringComparison.Ordinal))
        {
            CellAdd(receiverCell, argumentsOnly);
        }

        for (var k = 0; k < reference.Parameters.Count; k++)
        {
            var index = k + (receiver is null ? 0 : 1);
            if (reference.Parameters[k].ParameterType.IsByReference && all[index].Cell is { } cell)
            {
                var others = Taint.Empty;
                for (var j = 0; j < inputs.Count; j++)
                {
                    if (j != index)
                    {
                        others = others.Union(inputs[j]);
                    }
                }

                CellAdd(cell, others);
            }
        }

        if (receiver is not null && !isNewobj && reference.Parameters.Count == 0
            && reference.Name.StartsWith("get_", StringComparison.Ordinal)
            && PersonalDataWorld.IsDecisyaName(reference.DeclaringType))
        {
            return ReadMember(Eff(receiver), _w.MemberPathOfGetter(reference, definition));
        }

        return union;
    }

    private static bool IsAsyncSchedulingCall(string declaring, string name) =>
        name is "Start" or "AwaitOnCompleted" or "AwaitUnsafeOnCompleted"
        && declaring.StartsWith("System.Runtime.CompilerServices.Async", StringComparison.Ordinal)
        && declaring.Contains("MethodBuilder", StringComparison.Ordinal);

    /// <summary>
    /// Whether parameter <paramref name="parameter"/> (zero-based, without <c>this</c>) is an <c>out</c> parameter: the
    /// definition's flag when the method resolves, else the <c>Try...(..., out x)</c> convention on the last by-ref parameter.
    /// </summary>
    private static bool IsOutParameter(MethodReference reference, MethodDefinition? definition, int parameter)
    {
        if (parameter < 0 || parameter >= reference.Parameters.Count || !reference.Parameters[parameter].ParameterType.IsByReference)
        {
            return false;
        }

        if (definition is not null && parameter < definition.Parameters.Count)
        {
            return definition.Parameters[parameter].IsOut;
        }

        return reference.Name.StartsWith("Try", StringComparison.Ordinal) && parameter == reference.Parameters.Count - 1;
    }

    private Taint ArgumentTaint(MethodReference reference, Val value, int index, bool isReceiver, bool container)
    {
        if (_w.DecisyaDefinition(value.Type) is not null && IsTextPosition(reference, index, isReceiver))
        {
            return TextOf(value);
        }

        return container ? StoreTaint(value) : Eff(value);
    }

    private static bool IsTextPosition(MethodReference reference, int index, bool isReceiver)
    {
        var name = reference.Name;
        if (isReceiver)
        {
            return name == "ToString" && reference.Parameters.Count == 0;
        }

        var parameter = index - (reference.HasThis ? 1 : 0);
        if (parameter < 0 || parameter >= reference.Parameters.Count)
        {
            return false;
        }

        if (name.StartsWith("AppendFormatted", StringComparison.Ordinal))
        {
            return parameter == 0;
        }

        var declaring = PersonalDataWorld.Reflect(reference.DeclaringType);
        var isObject = string.Equals(reference.Parameters[parameter].ParameterType.FullName, "System.Object", StringComparison.Ordinal);
        return isObject
            && ((declaring == "System.String" && name is "Concat" or "Format")
                || (declaring == "System.Text.StringBuilder" && name == "Append"));
    }

    /// <summary>The text a Decisya value turns into: its own ToString (followed), else just the type name.</summary>
    private Taint TextOf(Val value)
    {
        if (_w.DecisyaDefinition(value.Type) is not { } definition)
        {
            return Eff(value);
        }

        MethodDefinition? toString = null;
        for (var current = definition; current is not null && toString is null; current = _w.ResolveType(current.BaseType))
        {
            toString = current.Methods.FirstOrDefault(m => m.Name == "ToString" && !m.HasParameters && m.HasBody);
        }

        if (toString is null || !_w.InScope(toString))
        {
            return Taint.Empty;
        }

        var summary = _w.GetSummary(toString);
        var result = Taint.Empty;
        foreach (var element in summary.Return)
        {
            result = result.Union(Subst(element, [value]));
        }

        return result;
    }

    private static List<int> From(int start, int count) => [.. Enumerable.Range(start, Math.Max(0, count - start))];

    private SinkInfo? ClassifySink(MethodReference reference, MethodDefinition? definition, Val[] all, bool isNewobj)
    {
        var declaring = PersonalDataWorld.Reflect(reference.DeclaringType);
        var name = reference.Name;

        if (isNewobj)
        {
            return _w.IsExceptionType(reference.DeclaringType)
                ? new SinkInfo(reference.DeclaringType.Name + " constructor (exception text)", From(1, all.Length), false)
                : null;
        }

        switch (declaring)
        {
            case "Microsoft.Extensions.Logging.ILogger" when name is "Log" or "BeginScope":
                return new SinkInfo("ILogger." + name, From(1, all.Length), false);
            case "Microsoft.Extensions.Logging.LoggerExtensions":
                return new SinkInfo("ILogger." + name, From(1, all.Length), false);
            case "System.Console" when name is "Write" or "WriteLine":
                return new SinkInfo("Console." + name, From(0, all.Length), false);
            case "System.IO.TextWriter" when name.StartsWith("Write", StringComparison.Ordinal) && all.Length > 0 && all[0].Console:
                return new SinkInfo("Console." + name, From(1, all.Length), false);
            case "System.Diagnostics.Debug" when name.StartsWith("Write", StringComparison.Ordinal) || name is "Print" or "Fail" or "Assert":
                return new SinkInfo("Debug." + name, From(0, all.Length), false);
            case "System.Diagnostics.Trace" when name.StartsWith("Write", StringComparison.Ordinal) || name.StartsWith("Trace", StringComparison.Ordinal):
                return new SinkInfo("Trace." + name, From(0, all.Length), false);
        }

        if (name == "Invoke"
            && (declaring.StartsWith("System.Action`", StringComparison.Ordinal) || declaring.StartsWith("System.Func`", StringComparison.Ordinal))
            && reference.DeclaringType is GenericInstanceType delegateType
            && delegateType.GenericArguments.Count > 0
            && PersonalDataWorld.Reflect(delegateType.GenericArguments[0]).StartsWith("Microsoft.Extensions.Logging.ILogger", StringComparison.Ordinal))
        {
            return new SinkInfo("LoggerMessage.Define delegate", From(2, all.Length), false);
        }

        if (definition is not null && PersonalDataWorld.HasAttribute(definition, PersonalDataWorld.LoggerMessageAttributeName))
        {
            var shift = reference.HasThis ? 1 : 0;
            var indexes = new List<int>();
            for (var k = 0; k < reference.Parameters.Count; k++)
            {
                var parameterType = PersonalDataWorld.Reflect(reference.Parameters[k].ParameterType);
                if (!parameterType.StartsWith("Microsoft.Extensions.Logging.ILogger", StringComparison.Ordinal))
                {
                    indexes.Add(k + shift);
                }
            }

            return new SinkInfo($"{reference.DeclaringType.Name}.{name} ([LoggerMessage])", indexes, true);
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------ sources at a call

    private TaintSource? SourceOfInvocation(MethodReference reference, MethodDefinition? definition, Val[] all)
    {
        if (TryBuiltInClaim(reference, all, out var claim))
        {
            return claim;
        }

        if (TryReader(reference, definition, all, out var reader))
        {
            return reader;
        }

        return _w.SourceOfCall(reference);
    }

    private bool TryReader(MethodReference reference, MethodDefinition? definition, Val[] all, out TaintSource? source)
    {
        source = null;
        if (!_w.TryGetReader(reference, out var entry))
        {
            return false;
        }

        var label = PersonalDataWorld.Reflect(reference.DeclaringType) + "::" + (reference.Name.StartsWith("get_", StringComparison.Ordinal) ? reference.Name[4..] : reference.Name);
        if (entry.NameParameter is null)
        {
            source = new TaintSource(entry.FallbackCategory, label);
            return true;
        }

        var position = definition is null ? -1 : definition.Parameters.ToList().FindIndex(p => string.Equals(p.Name, entry.NameParameter, StringComparison.Ordinal));
        var actual = position < 0 ? -1 : position + (reference.HasThis ? 1 : 0);
        if (actual >= 0 && actual < all.Length && all[actual].Const is { } constant)
        {
            // A constant name is matched against every category's claim types; no match is clean.
            var category = _w.CategoryOfClaimType(constant);
            source = category is null ? null : new TaintSource(category, label);
            return true;
        }

        source = new TaintSource(entry.FallbackCategory, label);
        return true;
    }

    /// <summary>The <c>ClaimsPrincipal</c> and <c>ClaimsIdentity</c> readers stay built into the rule (G2 D2).</summary>
    private bool TryBuiltInClaim(MethodReference reference, Val[] all, out TaintSource? source)
    {
        source = null;
        var declaring = PersonalDataWorld.Reflect(reference.DeclaringType);
        var principal = declaring is "System.Security.Claims.ClaimsPrincipal" or "System.Security.Claims.ClaimsIdentity";
        var extension = declaring == "System.Security.Claims.PrincipalExtensions" && reference.Name == "FindFirstValue";
        if (!principal && !extension)
        {
            return false;
        }

        var label = declaring + "::" + (reference.Name.StartsWith("get_", StringComparison.Ordinal) ? reference.Name[4..] : reference.Name);
        if (reference.Name == "get_Claims")
        {
            source = new TaintSource(PersonalDataWorld.UserIdentifierId, label);
            return true;
        }

        if (reference.Name is not ("FindFirst" or "FindAll" or "FindFirstValue" or "TryFindFirst"))
        {
            return false;
        }

        // The name argument is argument 1 for the instance readers and for the static extension alike.
        const int index = 1;
        if (index < all.Length
            && string.Equals(reference.Parameters[index - (reference.HasThis ? 1 : 0)].ParameterType.FullName, "System.String", StringComparison.Ordinal)
            && all[index].Const is { } constant)
        {
            var category = _w.CategoryOfClaimType(constant);
            source = category is null ? null : new TaintSource(category, label);
            return true;
        }

        source = new TaintSource(PersonalDataWorld.UserIdentifierId, label);
        return true;
    }
}
