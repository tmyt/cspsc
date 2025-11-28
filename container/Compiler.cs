using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using Basic.Reference.Assemblies;

namespace CsPsc
{
    internal static class Compiler
    {
        public static string Run(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);

            var walker = new Walker(tree);
            walker.Visit();
            walker.Finish();

            return walker.ToString();
        }

        class Walker : CSharpSyntaxWalker
        {
            private readonly SyntaxTree _tree;

            private readonly StringBuilder _script = new(string.Join("\n", new[]{
                "currentpagedevice /PageSize get aload pop",
                "/__pageheight exch def /__pagewidth exch def",
                "/Courier findfont 9 scalefont setfont",
                "/__font_size 9 def",
                "/__global_y __pageheight 24 sub def",
                "/__newline { /__global_y __global_y __font_size sub store 24 __global_y moveto } def",
                "/__println { show __newline } def",
                "/__concat { 2 dict begin /s2 exch def /s1 exch def s1 length s2 length add string dup 0 s1 putinterval dup s1 length s2 putinterval end } def",
                "24 __global_y moveto",
                ""
            }));
            private readonly Dictionary<string, string> _imports = new() { { "println", "__println" }, { "print", "show" } };

            private bool _handled;
            private readonly SemanticModel _semanticModel;

            public Walker(SyntaxTree tree)
            {
                _tree = tree;
                var compilation = CSharpCompilation.Create(null)
                    .AddReferences(ReferenceAssemblies.NetStandard20)
                    .AddSyntaxTrees(tree);
                _semanticModel = compilation.GetSemanticModel(tree);
            }

            public void Visit()
            {
                Visit(_tree.GetRoot());
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
                    _imports[node.Identifier.ValueText] = node.Identifier.ValueText.ToLower();
                }
                else if (isImported || node.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword)))
                {
                    throw new NotImplementedException("Only extern functions with DllImport are supported.");
                }
                else if (node.Body != null)
                {
                    _imports[node.Identifier.ValueText] = node.Identifier.ValueText;
                    var paramCount = node.ParameterList.Parameters.Count;
                    Emit($"/{node.Identifier.ValueText} {{ {paramCount} dict begin");
                    foreach (var param in node.ParameterList.Parameters.Reverse())
                    {
                        Emit($"/{param.Identifier.ValueText} exch def");
                    }

                    var hasReturnValue = node.Body.DescendantNodes()
                        .Where(n => n.IsKind(SyntaxKind.ReturnStatement))
                        .OfType<ReturnStatementSyntax>()
                        .Any(n => n.Expression != null);
                    Emit("mark");
                    Emit("{ ");
                    base.Visit(node.Body);
                    Emit("exit } loop");
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
                var localVarsCount = node.ChildNodes()
                    .OfType<LocalDeclarationStatementSyntax>()
                    .SelectMany(decl => decl.Declaration.Variables)
                    .Count();
                if (localVarsCount > 0)
                {
                    Emit($"{localVarsCount} dict begin");
                }
                base.VisitBlock(node);
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
                        if (!node.IsKind(SyntaxKind.SimpleAssignmentExpression))
                        {
                            Emit("get");
                        }

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
                if (!IsExpressionStatement(node))
                {
                    // When assignment is used as an expression, duplicate the value and reorder stack
                    // so that the assigned value remains on stack after store/put.
                    // For simple variable: stack is [/name value], dup gives [/name value value],
                    //   then "3 1 roll" gives [value /name value], store consumes 2, leaving [value].
                    // For element access: stack is [array index value], dup gives [array index value value],
                    //   then "4 1 roll" gives [value array index value], put consumes 3, leaving [value].
                    var roll = node.Left is ElementAccessExpressionSyntax ? "4 1 roll" : "3 1 roll";
                    Emit($"dup {roll}");
                }
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
                            if (!IsExpressionStatement(node))
                            {
                                VisitElementIndexerSyntax(elementAccess);
                                Emit("get");
                            }
                        }
                        else
                        {
                            Emit($"/{node.Operand} {node.Operand}");
                            Emit($"1 {op} store");
                            if (!IsExpressionStatement(node))
                            {
                                Emit(node.Operand.ToString());
                            }
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
                            if (!IsExpressionStatement(node))
                            {
                                VisitElementIndexerSyntax(elementAccess);
                                Emit("get");
                            }
                            VisitElementIndexerSyntax(elementAccess);
                            VisitElementIndexerSyntax(elementAccess);
                            Emit($"get 1 {op} put");
                        }
                        else
                        {
                            if (!IsExpressionStatement(node))
                            {
                                Emit($"{node.Operand} /{node.Operand}");
                            }
                            else
                            {
                                Emit($"/{node.Operand}");
                            }
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
                if (node.Declaration?.Variables.Count > 0)
                {
                    Emit($"{node.Declaration?.Variables.Count} dict begin");
                }
                base.Visit(node.Declaration);
                Emit("{");
                base.Visit(node.Condition);
                Emit("not { exit } if\n");
                base.Visit(node.Statement);
                foreach (var incrementor in node.Incrementors)
                {
                    base.Visit(incrementor);
                }
                Emit("} loop");
                if (node.Declaration?.Variables.Count > 0)
                {
                    Emit("end");
                }
            }

            public override void VisitWhileStatement(WhileStatementSyntax node)
            {
                _handled = true;
                Emit("{");
                base.Visit(node.Condition);
                Emit("not { exit } if\n");
                base.Visit(node.Statement);
                Emit("} loop");
            }

            public override void VisitDoStatement(DoStatementSyntax node)
            {
                _handled = true;
                Emit("{");
                base.Visit(node.Statement);
                base.Visit(node.Condition);
                Emit("not { exit } if\n");
                Emit("} loop");
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
                        Emit($"({node.Token.ValueText})");
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
                base.Visit(node.Expression);
                var defaultSection = node.Sections.FirstOrDefault(s => s.Labels.Any(l => l.IsKind(SyntaxKind.DefaultSwitchLabel)));
                var literalSections = node.Sections.Count(s => s.Labels.Any(l => !l.IsKind(SyntaxKind.DefaultSwitchLabel)));
                for (var i = 0; i < node.Sections.Count; ++i)
                {
                    var labels = node.Sections[i].Labels.Where(l => !l.IsKind(SyntaxKind.DefaultSwitchLabel)).ToList();
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
                    Emit("{");
                    foreach (var statement in node.Sections[i].Statements)
                    {
                        base.Visit(statement);
                    }
                    Emit("} ");
                    if (defaultSection != null || i < node.Sections.Count - 1)
                    {
                        Emit("{");
                    }
                }
                if (defaultSection != null)
                {
                    if (literalSections == 0)
                    {
                        Emit("false { } ");
                    }
                    foreach (var statement in defaultSection.Statements)
                    {
                        base.Visit(statement);
                    }
                    Emit("} ifelse");
                }
                else if (node.Sections.Any())
                {
                    Emit("if");
                }
                for (var i = 1; i < literalSections; ++i)
                {
                    Emit("} ifelse");
                }
                Emit("pop");
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
                if (_imports.TryGetValue(identifier.Identifier.ValueText, out var psName))
                {
                    foreach (var arg in node.ArgumentList.Arguments)
                    {
                        base.Visit(arg.Expression);
                    }
                    Emit(psName);
                    return;
                }
                if (identifier.Identifier.ValueText == "WriteLine")
                {
                    base.Visit(node.ArgumentList.Arguments[0]);
                    Emit("__println");
                    return;
                }
                throw new NotImplementedException($"No imported function found: {identifier.Identifier.ValueText}");
            }

            public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                _handled = true;
                base.Visit(node.Expression);
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
                if (node.Type.RankSpecifiers.Count > 1 || node.Type.RankSpecifiers.Any(s => s.Sizes.Count != 1))
                {
                    throw new NotImplementedException("Only single-dimensional arrays are supported.");
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
                throw new NotImplementedException("Continue statement is not supported yet.");
            }

            public override void VisitReturnStatement(ReturnStatementSyntax node)
            {
                _handled = true;
                base.VisitReturnStatement(node);
                var nestedLoops = 0;
                SyntaxNode? currentNode = node;
                while (currentNode?.Parent is not LocalFunctionStatementSyntax)
                {
                    if (currentNode == null) break;
                    if (currentNode is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax)
                    {
                        nestedLoops++;
                    }

                    currentNode = currentNode?.Parent;
                }
                Emit(string.Join(" ", Enumerable.Range(0, nestedLoops + 1).Select(_ => "exit")));
            }

            public override void VisitExpressionStatement(ExpressionStatementSyntax node)
            {
                _handled = true;
                base.VisitExpressionStatement(node);
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

            public override void VisitForEachStatement(ForEachStatementSyntax node)
            {
                _handled = true;
                base.Visit(node.Expression);
                Emit("{");
                Emit($"1 dict begin /{node.Identifier.ValueText} exch def");
                var localVarsCount = node.Statement.ChildNodes()
                    .OfType<LocalDeclarationStatementSyntax>()
                    .SelectMany(decl => decl.Declaration.Variables)
                    .Count();
                if (localVarsCount > 0)
                {
                    Emit($"{localVarsCount} dict begin");
                }
                base.Visit(node.Statement);
                if (localVarsCount > 0)
                {
                    Emit("end");
                }
                Emit("end } forall");
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
                        Emit($"({textContent.TextToken.ValueText})");
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
                if (args == null || args?.Count != 2) return;
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

            private static bool IsExpressionStatement(SyntaxNode node) => node.Parent != null && (node.Parent.IsKind(SyntaxKind.ExpressionStatement) || node.Parent.IsKind(SyntaxKind.ForStatement));

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
        }
    }
}
