using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis.Operations;

namespace CsPsc
{
    internal static class Compiler
    {
        public static string Run(string source)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine("using static System.Console;");
            sb.AppendLine("using static CsPsc.Preamble.Intrinsics;");
            sb.AppendLine(source);
            var tree = CSharpSyntaxTree.ParseText(sb.ToString());

            var walker = new Walker(tree);
            walker.Visit();
            walker.Finish();

            return walker.ToString();
        }

        class Walker : CSharpSyntaxWalker
        {
            private enum BreakReason
            {
                Loop,
                Switch,
            }

            private const int StopByReturn = 1;
            private const int StopByBreak = 2;

            private const string Preamble = """
            using System.Runtime.InteropServices;

            namespace CsPsc.Preamble
            {
                public static class Intrinsics
                {
                    [DllImport("cspsc_intrinsics", EntryPoint = "println")]
                    public static extern void println(string s);

                    [DllImport("cspsc_intrinsics", EntryPoint = "print")]
                    public static extern void print(string s);
                }
            }
            """;

            private readonly SyntaxTree _tree;
            private readonly Stack<BreakReason> _breakReasons = new();

            private readonly StringBuilder _script = new(string.Join("\n", new[]{
                "currentpagedevice /PageSize get aload pop",
                "/__pageheight exch def /__pagewidth exch def",
                "/Courier findfont 9 scalefont setfont",
                "/__font_size 9 def",
                "/__break_flag false def",
                "/__global_y __pageheight 24 sub def",
                "/__newline { /__global_y __global_y __font_size sub store 24 __global_y moveto } def",
                "/__println { show __newline } def",
                "/__concat { 2 dict begin /s2 exch def /s1 exch def s1 length s2 length add string dup 0 s1 putinterval dup s1 length s2 putinterval end } def",
                "/__break { /__break_flag true store exit } def",
                "/__loop { loop __break_flag { /__break_flag false store exit } if } def",
                "24 __global_y moveto",
                ""
            }));
            private readonly Dictionary<string, string> _intrinsics = new() {
                { "println", "__println" }, { "print", "show" }, { "WriteLine", "__println" }, { "Write", "show" }
            };

            private bool _handled;
            private readonly SemanticModel _semanticModel;

            public Walker(SyntaxTree tree)
            {
                _tree = tree;
                var preambleTree = CSharpSyntaxTree.ParseText(Preamble);
                var compilation = CSharpCompilation.Create("CsPscCompilation")
                    .AddReferences(ReferenceAssemblies.NetStandard20)
                    .AddSyntaxTrees(preambleTree)
                    .AddSyntaxTrees(tree);
                _semanticModel = compilation.GetSemanticModel(tree);
            }

            public void Visit()
            {
                var diagnostics = _semanticModel.GetDiagnostics();
                if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var messages = diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString());
                    throw new InvalidOperationException(
                        "Compilation failed:\n" + string.Join("\n", messages));
                }

                var functionStatements = _tree.GetRoot()
                    .ChildNodes()
                    .OfType<GlobalStatementSyntax>()
                    .Where(gs => gs.Statement is LocalFunctionStatementSyntax);

                // Step1: hoisting global functions
                foreach (var func in functionStatements)
                {
                    Visit(func);
                }
                // Step2: visit all other nodes
                foreach (var node in _tree.GetRoot().ChildNodes().Except(functionStatements))
                {
                    Visit(node);
                }
            }

            public override void Visit(SyntaxNode? node)
            {
                _handled = false;
                if (node == null) return;
                base.Visit(node);
                if (!_handled)
                {
                    throw new NotImplementedException($"Node not supported: {node.Kind()}");
                }
            }

            public override void VisitGlobalStatement(GlobalStatementSyntax node)
            {
                _handled = true;
                base.VisitGlobalStatement(node);
            }

            public override void VisitCompilationUnit(CompilationUnitSyntax node)
            {
                _handled = true;
                base.VisitCompilationUnit(node);
            }

            public override void VisitAttributeList(AttributeListSyntax node)
            {
                _handled = true;
                if (node.Target?.Identifier.ValueText != "assembly")
                {
                    throw new NotImplementedException("Only assembly attributes are supported.");
                }
                foreach (var attr in node.Attributes)
                {
                    if (attr.Name.ToString() == "PSFont")
                    {
                        VisitPSFontAttribute(attr);
                        continue;
                    }
                    throw new NotImplementedException($"Unknown assembly attribute: {attr.Name}");
                }
            }

            public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
            {
                _handled = true;
                if (HasCapturedVariables(node))
                {
                    throw new NotSupportedException("Closure is not supported");
                }
                var isImported = false;
                foreach (var attrList in node.AttributeLists)
                {
                    if (!string.IsNullOrEmpty(attrList.Target?.Identifier.ValueText))
                    {
                        throw new NotImplementedException("No targeted attributes are supported for local functions.");
                    }

                    foreach (var attr in attrList.Attributes)
                    {
                        if (attr.Name.ToString() == "DllImport")
                        {
                            isImported = true;
                            continue;
                        }

                        throw new NotImplementedException($"Unknown local function attribute: {attr.Name}");
                    }
                }

                if (isImported && node.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
                {
                    _intrinsics[node.Identifier.ValueText] = node.Identifier.ValueText.ToLower();
                }
                else if (isImported || node.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
                {
                    throw new NotImplementedException("Only extern functions with DllImport are supported.");
                }
                else if (node.Body != null)
                {
                    var paramCount = node.ParameterList.Parameters.Count;
                    Emit($"/{node.Identifier.ValueText} {{ {paramCount} dict begin");
                    foreach (var param in node.ParameterList.Parameters.Reverse())
                    {
                        Emit($"/{param.Identifier.ValueText} exch def");
                    }

                    var returnType = _semanticModel.GetTypeInfo(node.ReturnType).Type
                        ?? throw new InvalidOperationException("Cannot determine the return type of the function.");
                    var hasReturnValue = returnType.SpecialType != SpecialType.System_Void;
                    Emit("mark");
                    Emit("{ ");
                    base.Visit(node.Body);
                    // ここは ..., rv, stop_code, stopped? になってる。先頭はifで消化されるので、stop_codeはstopped?の時に消化する
                    Emit(" } stopped { pop } if");
                    Emit(hasReturnValue ? "count 1 roll cleartomark count -1 roll" : "cleartomark");
                    Emit("end } def");
                }
                else
                {
                    throw new NotImplementedException("Local functions must have a body.");
                }
            }

            public override void VisitBlock(BlockSyntax node)
            {
                _handled = true;
                var localVarsCount = node.Statements
                    .OfType<LocalDeclarationStatementSyntax>()
                    .SelectMany(decl => decl.Declaration.Variables)
                    .Count();
                var localFunctions = node.Statements
                    .OfType<LocalFunctionStatementSyntax>()
                    .ToList();
                localVarsCount += localFunctions.Count;
                if (localVarsCount > 0)
                {
                    Emit($"{localVarsCount} dict begin");
                }
                // step1: hoisting local functions
                foreach (var func in localFunctions)
                {
                    Visit(func);
                }
                // step2: visit all other statements
                foreach (var statement in node.Statements.Except(localFunctions))
                {
                    Visit(statement);
                }
                if (localVarsCount > 0)
                {
                    Emit("end");
                }
            }

            public override void VisitBinaryExpression(BinaryExpressionSyntax node)
            {
                _handled = true;
                // ショートサーキットがあるやつ
                if (node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression))
                {
                    var op = node.IsKind(SyntaxKind.LogicalAndExpression) ? "not " : "";
                    var value = node.IsKind(SyntaxKind.LogicalAndExpression) ? "false" : "true";
                    base.Visit(node.Left);
                    Emit($"{op} {{ {value} }} {{ ");
                    base.Visit(node.Right);
                    Emit("} ifelse");
                }
                // ないやつ
                else
                {
                    var resultIsString = IsString(node.Left) || IsString(node.Right);
                    base.Visit(node.Left);
                    if (node.IsKind(SyntaxKind.ModuloExpression) && IsReal(node.Left))
                    {
                        Emit("cvi");
                    }
                    if (node.IsKind(SyntaxKind.AddExpression) && resultIsString)
                    {
                        EmitToStringSyntax(node.Left);
                    }
                    base.Visit(node.Right);
                    if (node.IsKind(SyntaxKind.ModuloExpression) && IsReal(node.Right))
                    {
                        Emit("cvi");
                    }
                    if (node.IsKind(SyntaxKind.AddExpression) && resultIsString)
                    {
                        EmitToStringSyntax(node.Right);
                    }
                    if (node.IsKind(SyntaxKind.DivideExpression))
                    {
                        Emit(IsReal(node.Left) || IsReal(node.Right) ? "div" : "idiv");
                    }
                    else if (node.IsKind(SyntaxKind.AddExpression) && resultIsString)
                    {
                        Emit("__concat");
                    }
                    else
                    {
                        Emit(GetOperatorPostScript(node.Kind()));
                    }
                }
            }

            public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
            {
                _handled = true;
                // 分割代入だけは特殊なので専用の処理
                if (_semanticModel.GetOperation(node) is IDeconstructionAssignmentOperation)
                {
                    // Evaluate right-hand side
                    base.Visit(node.Right);
                    if (node.Left is DeclarationExpressionSyntax declaration && declaration.Designation is ParenthesizedVariableDesignationSyntax pvd)
                    {
                        // Store to each variable
                        for (var i = 0; i < pvd.Variables.Count; ++i)
                        {
                            if (pvd.Variables[i] is DiscardDesignationSyntax)
                            {
                                continue;
                            }
                            Emit($"dup {i} get /{pvd.Variables[i]} exch def");
                        }
                    }
                    else if (node.Left is TupleExpressionSyntax tuple)
                    {
                        // Store to each variable
                        for (var i = 0; i < tuple.Arguments.Count; ++i)
                        {
                            var arg = tuple.Arguments[i];
                            var tupleOp = "store";
                            var tupleIdentifier = arg.Expression.ToString();
                            if (arg.Expression is DeclarationExpressionSyntax decl)
                            {
                                if (decl.Designation is DiscardDesignationSyntax)
                                {
                                    continue;
                                }
                                tupleOp = "def";
                                tupleIdentifier = decl.Designation.ToString();
                            }
                            Emit($"dup {i} get /{tupleIdentifier} exch {tupleOp}");
                        }
                    }
                    else
                    {
                        throw new NotImplementedException("Unsupported deconstruction assignment target.");
                    }
                    return;
                }

                // 通常の代入/通常の複合代入など
                string op;
                var store = node.Left is ElementAccessExpressionSyntax ? "put" : "store";
                if (node.IsKind(SyntaxKind.DivideAssignmentExpression))
                {
                    op = IsReal(node.Right) || IsReal(node.Left) ? "div" : "idiv";
                }
                else if (node.IsKind(SyntaxKind.AddAssignmentExpression))
                {
                    if (!IsString(node.Left) && IsString(node.Right))
                    {
                        throw new InvalidOperationException("Cannot add non-string to string.");
                    }
                    op = IsString(node.Left) ? "__concat" : "add";
                }
                else
                {
                    op = GetAssignmentOperatorPostScript(node.Kind());
                }

                if (node.Left is ElementAccessExpressionSyntax elementAccess)
                {
                    VisitElementIndexerSyntax(elementAccess);
                    if (!node.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    {
                        VisitElementIndexerSyntax(elementAccess);
                        Emit("get");

                        if (node.IsKind(SyntaxKind.ModuloAssignmentExpression) && IsReal(elementAccess))
                        {
                            Emit("cvi");
                        }
                    }
                }
                else
                {
                    Emit($"/{node.Left}");
                    if (!node.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    {
                        Emit(node.Left.ToString());
                        if (node.IsKind(SyntaxKind.ModuloAssignmentExpression) && IsReal(node.Left))
                        {
                            Emit("cvi");
                        }
                    }
                }

                base.Visit(node.Right);
                if (node.IsKind(SyntaxKind.ModuloAssignmentExpression) && IsReal(node.Right))
                {
                    Emit("cvi");
                }
                if (node.IsKind(SyntaxKind.AddAssignmentExpression) && IsString(node.Left) && !IsString(node.Right))
                {
                    EmitToStringSyntax(node.Right);
                }
                Emit(op);
                Emit($"dup {(node.Left is ElementAccessExpressionSyntax ? 4 : 3)} 1 roll");
                Emit(store);
            }

            public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
            {
                _handled = true;
                switch (node.Kind())
                {
                    case SyntaxKind.UnaryMinusExpression:
                        base.Visit(node.Operand);
                        Emit("neg");
                        return;
                    case SyntaxKind.UnaryPlusExpression:
                        base.Visit(node.Operand);
                        return;
                    case SyntaxKind.PreIncrementExpression:
                    case SyntaxKind.PreDecrementExpression:
                        var op = node.Kind() == SyntaxKind.PreIncrementExpression ? "add" : "sub";
                        if (node.Operand is ElementAccessExpressionSyntax elementAccess)
                        {
                            VisitElementIndexerSyntax(elementAccess);
                            VisitElementIndexerSyntax(elementAccess);
                            Emit($"get 1 {op} put");
                            VisitElementIndexerSyntax(elementAccess);
                            Emit("get");
                        }
                        else
                        {
                            Emit($"/{node.Operand} {node.Operand}");
                            Emit($"1 {op} store");
                            Emit(node.Operand.ToString());
                        }
                        return;
                    case SyntaxKind.BitwiseNotExpression:
                    case SyntaxKind.LogicalNotExpression:
                        base.Visit(node.Operand);
                        Emit("not");
                        return;
                    default:
                        throw new NotImplementedException($"Prefix unary operator not supported: {node.Kind()}");
                }
            }

            public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
            {
                _handled = true;
                switch (node.Kind())
                {
                    case SyntaxKind.PostIncrementExpression:
                    case SyntaxKind.PostDecrementExpression:
                        var op = node.Kind() == SyntaxKind.PostIncrementExpression ? "add" : "sub";
                        if (node.Operand is ElementAccessExpressionSyntax elementAccess)
                        {
                            VisitElementIndexerSyntax(elementAccess);
                            Emit("get");
                            VisitElementIndexerSyntax(elementAccess);
                            VisitElementIndexerSyntax(elementAccess);
                            Emit($"get 1 {op} put");
                        }
                        else
                        {
                            Emit($"{node.Operand} /{node.Operand}");
                            Emit($"{node.Operand} 1 {op} store");
                        }
                        return;
                    default:
                        throw new NotImplementedException($"Postfix unary operator not supported: {node.Kind()}");
                }
            }

            public override void VisitVariableDeclaration(VariableDeclarationSyntax node)
            {
                _handled = true;
                foreach (var variable in node.Variables)
                {
                    if (variable.Initializer == null)
                    {
                        Emit($"/{variable.Identifier.ValueText} 0 def");
                    }
                    else
                    {
                        Emit($"/{variable.Identifier.ValueText}");
                        base.Visit(variable.Initializer.Value);
                        Emit("def");
                    }
                }
            }

            public override void VisitForStatement(ForStatementSyntax node)
            {
                _handled = true;
                _breakReasons.Push(BreakReason.Loop);
                if (node.Declaration?.Variables.Count > 0)
                {
                    Emit($"{node.Declaration?.Variables.Count} dict begin");
                }
                base.Visit(node.Declaration);
                Emit("{");
                base.Visit(node.Condition);
                Emit("not { exit } if\n");
                Emit("{");
                base.Visit(node.Statement);
                Emit("exit } __loop");
                foreach (var incrementor in node.Incrementors)
                {
                    base.Visit(incrementor);
                    if (HasExpressionValue(incrementor))
                    {
                        Emit("pop");
                    }
                }
                Emit("} loop");
                if (node.Declaration?.Variables.Count > 0)
                {
                    Emit("end");
                }
                _breakReasons.Pop();
            }

            public override void VisitForEachStatement(ForEachStatementSyntax node)
            {
                _handled = true;
                _breakReasons.Push(BreakReason.Loop);
                base.Visit(node.Expression);
                Emit("{");
                Emit($"1 dict begin /{node.Identifier.ValueText} exch def");
                Emit("{");
                base.Visit(node.Statement);
                Emit("exit } __loop");
                Emit("end } forall");
                _breakReasons.Pop();
            }

            public override void VisitWhileStatement(WhileStatementSyntax node)
            {
                _handled = true;
                _breakReasons.Push(BreakReason.Loop);
                Emit("{");
                base.Visit(node.Condition);
                Emit("not { exit } if\n");
                Emit("{");
                base.Visit(node.Statement);
                Emit("exit } __loop");
                Emit("} loop");
                _breakReasons.Pop();
            }

            public override void VisitDoStatement(DoStatementSyntax node)
            {
                _handled = true;
                _breakReasons.Push(BreakReason.Loop);
                Emit("{");
                Emit("{");
                base.Visit(node.Statement);
                Emit("exit } __loop");
                base.Visit(node.Condition);
                Emit("not { exit } if\n");
                Emit("} loop");
                _breakReasons.Pop();
            }

            public override void VisitLiteralExpression(LiteralExpressionSyntax node)
            {
                _handled = true;
                switch (node.Kind())
                {
                    case SyntaxKind.NumericLiteralExpression:
                        Emit(node.Token.ValueText);
                        break;
                    case SyntaxKind.StringLiteralExpression:
                        Emit($"({EscapePostScriptString(node.Token.ValueText)})");
                        break;
                    case SyntaxKind.CharacterLiteralExpression:
                        Emit($"{(int)node.Token.ValueText[0]}");
                        break;
                    case SyntaxKind.TrueLiteralExpression:
                        Emit("true");
                        break;
                    case SyntaxKind.FalseLiteralExpression:
                        Emit("false");
                        break;
                    default:
                        throw new NotImplementedException($"Literal kind not supported: {node.Kind()}");
                }
                base.VisitLiteralExpression(node);
            }

            public override void VisitIfStatement(IfStatementSyntax node)
            {
                _handled = true;
                base.Visit(node.Condition);
                Emit("{");
                base.Visit(node.Statement);
                if (node.Else != null)
                {
                    Emit("} {");
                    base.Visit(node.Else.Statement);
                    Emit("} ifelse");
                }
                else
                {
                    Emit("} if");
                }
            }

            public override void VisitSwitchStatement(SwitchStatementSyntax node)
            {
                _handled = true;
                _breakReasons.Push(BreakReason.Switch);
                Emit("mark {");
                base.Visit(node.Expression);
                var literalSections = node.Sections.Where(s => s.Labels.Any(l => !l.IsKind(SyntaxKind.DefaultSwitchLabel))).ToList();
                for (var i = 0; i < literalSections.Count; ++i)
                {
                    var labels = literalSections[i].Labels.Where(l => !l.IsKind(SyntaxKind.DefaultSwitchLabel)).ToList();
                    if (labels.Count == 0) continue;
                    Emit("dup");
                    base.Visit(labels[0]);
                    Emit("eq");
                    for (var i2 = 1; i2 < labels.Count; ++i2)
                    {
                        Emit("1 index");
                        base.Visit(labels[i2]);
                        Emit("eq or");
                    }
                    Emit("{ pop");
                    foreach (var statement in literalSections[i].Statements)
                    {
                        base.Visit(statement);
                    }
                    Emit("} ");
                    if (i < literalSections.Count - 1)
                    {
                        Emit("{");
                    }
                }
                if (literalSections.Count == 0)
                {
                    Emit("false { } ");
                }
                Emit("{ pop");
                var defaultSection = node.Sections.FirstOrDefault(s => s.Labels.Any(l => l.IsKind(SyntaxKind.DefaultSwitchLabel)));
                if (defaultSection != null)
                {
                    foreach (var statement in defaultSection.Statements)
                    {
                        base.Visit(statement);
                    }
                }
                Emit("} ifelse");
                for (var i = 1; i < literalSections.Count; ++i)
                {
                    Emit("} ifelse");
                }
                Emit("} stopped { ");
                Emit($"dup {StopByBreak} eq {{ cleartomark }} {{ count 2 roll cleartomark count -2 roll stop }} ifelse");
                Emit("} { cleartomark } ifelse");
                _breakReasons.Pop();
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                _handled = true;
                if (node.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    base.Visit(memberAccess.Expression);
                    if (memberAccess.Name.Identifier.ValueText == "ToString")
                    {
                        EmitToStringSyntax(memberAccess.Expression);
                        return;
                    }
                    throw new NotImplementedException("Only ToString member access is supported.");
                }
                if (node.Expression is not IdentifierNameSyntax identifier)
                {
                    throw new NotImplementedException("Only simple identifier function calls are supported.");
                }
                foreach (var arg in node.ArgumentList.Arguments)
                {
                    base.Visit(arg.Expression);
                }
                _intrinsics.TryGetValue(identifier.Identifier.ValueText, out var importName);
                Emit(importName ?? identifier.Identifier.ValueText);
            }

            public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                _handled = true;
                base.Visit(node.Expression);

                var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
                if (symbol is IFieldSymbol { ContainingType.IsTupleType: true } field)
                {
                    var tupleType = field.ContainingType;
                    var tupleElement = tupleType.TupleElements.Select(t => t.Name).ToList().IndexOf(field.Name);
                    Emit($"{tupleElement} get");
                    return;
                }

                if (node.Name.Identifier.ValueText == "Length")
                {
                    Emit("length");
                    return;
                }
                throw new NotImplementedException("Only Length member access is supported.");
            }

            public override void VisitIdentifierName(IdentifierNameSyntax node)
            {
                _handled = true;
                Emit(node.Identifier.ValueText);
            }

            public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
            {
                _handled = true;
                base.Visit(node.Condition);
                Emit("{");
                base.Visit(node.WhenTrue);
                Emit("} {");
                base.Visit(node.WhenFalse);
                Emit("} ifelse");
            }

            public override void VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
            {
                _handled = true;
                if (node.Type.RankSpecifiers.Any(s => s.Sizes.Count != 1))
                {
                    throw new NotImplementedException("Multi-dimensional arrays are not supported. Use jagged arrays instead.");
                }
                base.Visit(node.Type.RankSpecifiers[0]);
                Emit("array");
            }

            public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
            {
                _handled = true;
                VisitElementIndexerSyntax(node);
                Emit("get");
            }

            public override void VisitContinueStatement(ContinueStatementSyntax node)
            {
                _handled = true;
                Emit("exit");
            }

            public override void VisitBreakStatement(BreakStatementSyntax node)
            {
                _handled = true;
                switch (_breakReasons.Peek())
                {
                    case BreakReason.Loop:
                        Emit("__break");
                        break;
                    case BreakReason.Switch:
                        Emit($"{StopByBreak} stop");
                        break;
                    default:
                        throw new InvalidOperationException("Invalid break context.");
                }
            }

            public override void VisitReturnStatement(ReturnStatementSyntax node)
            {
                _handled = true;
                base.VisitReturnStatement(node);
                Emit($"{StopByReturn} stop");
            }

            public override void VisitUsingDirective(UsingDirectiveSyntax node)
            {
                _handled = true;
                // Do nothing for using directives
            }

            public override void VisitTupleExpression(TupleExpressionSyntax node)
            {
                _handled = true;
                Emit("[");
                foreach (var element in node.Arguments)
                {
                    base.Visit(element.Expression);
                }
                Emit("]");
            }

            public override void VisitExpressionStatement(ExpressionStatementSyntax node)
            {
                _handled = true;
                base.VisitExpressionStatement(node);
                if (HasExpressionValue(node.Expression))
                {
                    Emit("pop");
                }
            }

            public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
            {
                _handled = true;
                base.VisitLocalDeclarationStatement(node);
            }

            public override void VisitParenthesizedExpression(ParenthesizedExpressionSyntax node)
            {
                _handled = true;
                base.VisitParenthesizedExpression(node);
            }

            public override void VisitPredefinedType(PredefinedTypeSyntax node)
            {
                _handled = true;
                // Do nothing for predefined types
            }

            public override void VisitCastExpression(CastExpressionSyntax node)
            {
                _handled = true;
                if (node.Type is not PredefinedTypeSyntax predefinedType)
                {
                    throw new NotSupportedException("Only predefined types are supported in cast expressions.");
                }

                base.Visit(node.Expression);
                switch (predefinedType.Keyword.Kind())
                {
                    case SyntaxKind.IntKeyword:
                        Emit("cvi");
                        break;
                    case SyntaxKind.DoubleKeyword:
                    case SyntaxKind.FloatKeyword:
                        Emit("cvr");
                        break;
                    case SyntaxKind.CharKeyword:
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Cast to '{predefinedType.Keyword.ValueText}' is not supported.");
                }
            }

            public override void VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
            {
                _handled = true;
                Emit("()");
                foreach (var content in node.Contents)
                {
                    if (content is InterpolatedStringTextSyntax textContent)
                    {
                        Emit($"({EscapePostScriptString(textContent.TextToken.ValueText)})");
                    }
                    else if (content is InterpolationSyntax interpolation)
                    {
                        base.Visit(interpolation.Expression);
                        EmitToStringSyntax(interpolation.Expression);
                    }
                    else
                    {
                        throw new NotImplementedException("Unknown interpolated string content.");
                    }
                    Emit("__concat");
                }
            }

            private void VisitPSFontAttribute(AttributeSyntax node)
            {
                var args = node.ArgumentList?.Arguments;
                if (args?.Count != 2) return;
                var fontName = args?[0]?.ToString().Trim('"');
                var fontSize = args?[1]?.ToString();
                Emit($"/{fontName} findfont {fontSize} scalefont setfont");
                Emit($"/__font_size {fontSize} store");
            }

            private void VisitElementIndexerSyntax(ElementAccessExpressionSyntax node)
            {
                base.Visit(node.Expression);
                foreach (var arg in node.ArgumentList.Arguments)
                {
                    base.Visit(arg.Expression);
                }
            }

            private void EmitToStringSyntax(SyntaxNode node)
            {
                Emit(MakeToString());
                return;

                string MakeToString()
                {
                    if (IsString(node)) return "";
                    if (IsChar(node)) return "1 string dup 0 4 -1 roll put";
                    if (IsBoolean(node)) return "{ (True) } { (False) } ifelse";
                    return "20 string cvs";
                }
            }

            public override string ToString() => _script.ToString();

            public void Finish()
            {
                Emit("showpage");
            }

            private static string GetOperatorPostScript(SyntaxKind kind)
            {
                return kind switch
                {
                    SyntaxKind.AddExpression => "add",
                    SyntaxKind.SubtractExpression => "sub",
                    SyntaxKind.MultiplyExpression => "mul",
                    SyntaxKind.DivideExpression => "div",
                    SyntaxKind.ModuloExpression => "mod",
                    SyntaxKind.BitwiseAndExpression => "and",
                    SyntaxKind.LogicalAndExpression => "and",
                    SyntaxKind.BitwiseOrExpression => "or",
                    SyntaxKind.LogicalOrExpression => "or",
                    SyntaxKind.ExclusiveOrExpression => "xor",
                    SyntaxKind.LeftShiftExpression => "bitshift",
                    SyntaxKind.RightShiftExpression => "neg bitshift",
                    SyntaxKind.EqualsExpression => "eq",
                    SyntaxKind.NotEqualsExpression => "ne",
                    SyntaxKind.GreaterThanExpression => "gt",
                    SyntaxKind.LessThanExpression => "lt",
                    SyntaxKind.GreaterThanOrEqualExpression => "ge",
                    SyntaxKind.LessThanOrEqualExpression => "le",
                    _ => throw new NotImplementedException($"Operator not supported: {kind}"),
                };
            }

            private static string GetAssignmentOperatorPostScript(SyntaxKind kind)
            {
                return kind switch
                {
                    SyntaxKind.SimpleAssignmentExpression => "",
                    SyntaxKind.AddAssignmentExpression => "add",
                    SyntaxKind.SubtractAssignmentExpression => "sub",
                    SyntaxKind.MultiplyAssignmentExpression => "mul",
                    SyntaxKind.DivideAssignmentExpression => "div",
                    SyntaxKind.ModuloAssignmentExpression => "mod",
                    SyntaxKind.AndAssignmentExpression => "and",
                    SyntaxKind.OrAssignmentExpression => "or",
                    SyntaxKind.ExclusiveOrAssignmentExpression => "xor",
                    SyntaxKind.LeftShiftAssignmentExpression => "bitshift",
                    SyntaxKind.RightShiftAssignmentExpression => "neg bitshift",
                    _ => throw new NotImplementedException($"Assignment operator not supported: {kind}"),
                };
            }

            private void Emit(string code)
            {
                _script.Append(code);
                if (code.Contains("__println") || code.EndsWith("def") || code.EndsWith("put") || code.EndsWith("store") || code.EndsWith("if") || code.EndsWith("ifelse") || code.EndsWith("loop"))
                {
                    _script.Append('\n');
                }
                else
                {
                    _script.Append(' ');
                }
            }

            private bool IsReal(SyntaxNode node)
            {
                var typeInfo = _semanticModel.GetTypeInfo(node);
                var type = typeInfo.Type;
                return type?.SpecialType is SpecialType.System_Double or SpecialType.System_Single;
            }

            private bool IsChar(SyntaxNode node)
            {
                var typeInfo = _semanticModel.GetTypeInfo(node);
                var type = typeInfo.Type;
                return type?.SpecialType is SpecialType.System_Char;
            }

            private bool IsString(SyntaxNode node)
            {
                var typeInfo = _semanticModel.GetTypeInfo(node);
                var type = typeInfo.Type;
                return type?.SpecialType is SpecialType.System_String;
            }

            private bool IsBoolean(SyntaxNode node)
            {
                var typeInfo = _semanticModel.GetTypeInfo(node);
                var type = typeInfo.Type;
                return type?.SpecialType is SpecialType.System_Boolean;
            }

            private bool HasCapturedVariables(LocalFunctionStatementSyntax node)
            {
                if (node.Body == null)
                {
                    return false;
                }
                var flow = _semanticModel.AnalyzeDataFlow(node.Body);
                var captured = flow?.Captured
                    .Where(s =>
                        s.Kind is SymbolKind.Local or SymbolKind.Parameter &&
                        !flow.VariablesDeclared.Contains(s));
                return captured?.Any() == true;
            }

            private bool HasExpressionValue(SyntaxNode node)
            {
                var typeInfo = _semanticModel.GetTypeInfo(node);
                var type = typeInfo.Type ?? throw new InvalidOperationException("Cannot determine the type of the expression.");
                // 事前にDiagnosisをチェックしているのでError型になってここに到達することはないはず
                return type.SpecialType != SpecialType.System_Void;
            }

            private string EscapePostScriptString(string str)
            {
                return str
                    .Replace("\\", "\\\\")
                    .Replace("(", "\\(")
                    .Replace(")", "\\)")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
            }
        }
    }
}
