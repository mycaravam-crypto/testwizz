using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestWizz.Core.Context;
using TestWizz.Core.Planning;

namespace TestWizz.DotNet;

/// <summary>
/// Derives test scenarios from the C# source excerpt in a context document: guard clauses and thrown exceptions,
/// comparison boundaries, early returns, switch cases, the happy path and cancellation. Works on syntax only, so it
/// needs nothing but <c>testwizz.context/v1</c>. Expectations come from the current code and are therefore marked for
/// review unless the XML documentation states the same thing.
/// </summary>
public sealed partial class CSharpScenarioExtractor : IScenarioExtractor
{
    public const string CodeOnlyReason = "Inferred from the current implementation; no documentation or requirement confirms it.";
    private const string WrapperHeader = "class __TestWizzWrapper__ {";

    public string Version => "csharp-extractor/1";

    [GeneratedRegex("<exception\\s+cref=\"(?:[A-Z]:)?(?<type>[^\"]+)\"\\s*>(?<text>.*?)</exception>", RegexOptions.Singleline)]
    private static partial Regex DocException();

    [GeneratedRegex("<(?<tag>summary|returns|remarks)>(?<text>.*?)</\\k<tag>>", RegexOptions.Singleline)]
    private static partial Regex DocSection();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    public ExtractionResult Extract(ContextDocument context)
    {
        if (context.Target.Kind != "method")
        {
            throw new ContextException(
                "plan.needs_method",
                $"Planning works on one method; '{context.Target.Id}' is a {context.Target.Kind}.",
                [.. context.Members.Select(m => m.Id)]);
        }

        return new Run(context).Execute();
    }

    private sealed class Run(ContextDocument context)
    {
        private readonly List<Evidence> _evidence = [];
        private readonly List<Draft> _drafts = [];
        private readonly List<PlanFinding> _findings = [];
        private readonly Dictionary<SyntaxNode, string> _ids = [];
        private readonly HashSet<string> _dependencyNames = context.Dependencies
            .Where(d => d.Role == "constructor")
            .Select(d => d.ParameterName.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        public ExtractionResult Execute()
        {
            var tree = CSharpSyntaxTree.ParseText($"{WrapperHeader}\n{context.Source.Excerpt}\n}}", new CSharpParseOptions(LanguageVersion.Preview));
            var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (context.Source.Truncated)
            {
                _findings.Add(new("excerpt-truncated", "The source excerpt was truncated; scenarios may be incomplete. Increase --max-chars."));
            }

            if (method is null)
            {
                _findings.Add(new("parse-failed", "The method could not be parsed from the source excerpt."));
                return new ExtractionResult([], [], _findings);
            }

            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                _findings.Add(new("parse-errors", "The source excerpt has syntax errors; scenarios may be incomplete."));
            }

            var docExceptions = CollectDocumentation();
            var cancellation = CollectCancellation(method);
            CollectBody(method);
            var relatedTests = context.RelatedTests
                .Where(t => t.MatchedOn.StartsWith("member:", StringComparison.Ordinal))
                .Select(t => (Test: t, Id: Add("related-test", null, $"{t.Name} ({t.File}:{t.Line})")))
                .ToList();

            DeriveHappyPath(method);
            DeriveFromStatements(method, docExceptions);
            DeriveCancellation(cancellation);

            if (_drafts.Count <= 1)
            {
                _findings.Add(new("no-branches", "No guards, branches or exceptions were found; only the happy path is planned."));
            }

            var scenarios = _drafts.Select((d, i) => new Scenario(
                $"S{i + 1}",
                d.Category,
                d.Title,
                d.Inputs,
                d.Expected,
                [.. d.Evidence.Distinct(StringComparer.Ordinal).OrderBy(e => int.Parse(e[1..], CultureInfo.InvariantCulture))],
                "deterministic",
                d.ReviewReason is not null,
                d.ReviewReason,
                [.. relatedTests.Where(r => LooksCovered(r.Test.Name, d)).Select(r => r.Test.Name)])).ToList();

            return new ExtractionResult(_evidence, scenarios, _findings);
        }

        // ---- evidence -------------------------------------------------------------------------------------------

        private string Add(string kind, int? line, string text, SyntaxNode? node = null)
        {
            if (node is not null && _ids.TryGetValue(node, out var existing))
            {
                return existing;
            }

            var id = $"E{_evidence.Count + 1}";
            _evidence.Add(new Evidence(id, kind, line, text));
            if (node is not null)
            {
                _ids[node] = id;
            }

            return id;
        }

        private Dictionary<string, string> CollectDocumentation()
        {
            var exceptions = new Dictionary<string, string>(StringComparer.Ordinal);
            var doc = context.Source.Documentation;
            if (doc is null)
            {
                return exceptions;
            }

            foreach (Match match in DocSection().Matches(doc))
            {
                Add("doc", null, $"{match.Groups["tag"].Value}: {Collapse(match.Groups["text"].Value)}");
            }

            foreach (Match match in DocException().Matches(doc))
            {
                var type = ShortName(match.Groups["type"].Value);
                exceptions[type] = Add("doc", null, $"exception {type}: {Collapse(match.Groups["text"].Value)}");
            }

            return exceptions;
        }

        private string? CollectCancellation(MethodDeclarationSyntax method)
        {
            var token = method.ParameterList.Parameters.FirstOrDefault(p => p.Type?.ToString() is "CancellationToken" or "System.Threading.CancellationToken");
            return token is null ? null : Add("cancellation", Line(token), $"{token.Type} {token.Identifier.Text}", token);
        }

        private void CollectBody(MethodDeclarationSyntax method)
        {
            foreach (var node in method.DescendantNodes())
            {
                switch (node)
                {
                    case IfStatementSyntax ifStatement:
                        Add(Throws(ifStatement.Statement) is not null ? "guard" : "branch", Line(ifStatement), $"if ({Collapse(ifStatement.Condition.ToString())})", ifStatement);
                        foreach (var comparison in Comparisons(ifStatement.Condition))
                        {
                            Add("comparison", Line(comparison), Collapse(comparison.ToString()), comparison);
                        }

                        break;
                    case ThrowStatementSyntax or ThrowExpressionSyntax:
                        Add("throw", Line(node), Collapse(Statement(node).ToString()), node);
                        break;
                    case InvocationExpressionSyntax invocation when ThrowHelper(invocation) is not null:
                        Add("throw", Line(invocation), Collapse(Statement(invocation).ToString()), invocation);
                        break;
                    case SwitchSectionSyntax section:
                        Add("switch-case", Line(section), Collapse(string.Join(" ", section.Labels)), section);
                        break;
                    case SwitchExpressionArmSyntax arm:
                        Add("switch-case", Line(arm), Collapse(arm.ToString()), arm);
                        break;
                    case ReturnStatementSyntax:
                        Add("return", Line(node), Collapse(node.ToString()), node);
                        break;
                    case MemberAccessExpressionSyntax access when IsDependency(access.Expression) && access.Parent is not MemberAccessExpressionSyntax:
                        var call = access.Parent is InvocationExpressionSyntax inv ? inv : (SyntaxNode)access;
                        Add("dependency", Line(call), Collapse(call.ToString()), access);
                        break;
                }
            }

            if (method.ExpressionBody is { } body)
            {
                Add("return", Line(body), Collapse(body.ToString()), body);
            }
        }

        // ---- scenarios ------------------------------------------------------------------------------------------

        private void DeriveHappyPath(MethodDeclarationSyntax method)
        {
            var calls = _ids.Where(p => _evidence.Single(e => e.Id == p.Value).Kind == "dependency").OrderBy(p => p.Key.SpanStart).ToList();
            var finalReturn = method.ExpressionBody?.Expression
                ?? method.Body?.Statements.OfType<ReturnStatementSyntax>().LastOrDefault()?.Expression;
            var evidence = new List<string>();
            string expected;
            if (finalReturn is not null)
            {
                evidence.Add(method.ExpressionBody is { } arrow ? _ids[arrow] : _ids[finalReturn.Parent!]);
                expected = $"Returns {Collapse(finalReturn.ToString())}";
            }
            else
            {
                expected = "Completes without throwing";
            }

            if (calls.Count > 0)
            {
                expected += "; uses " + string.Join(", ", calls.Select(c => Collapse((c.Key.Parent is InvocationExpressionSyntax i ? i : c.Key).ToString())));
                evidence.AddRange(calls.Select(c => c.Value));
            }

            evidence.AddRange(_evidence.Where(e => e.Kind == "doc" && e.Text.StartsWith("summary", StringComparison.Ordinal)).Select(e => e.Id));
            var guards = method.DescendantNodes().OfType<IfStatementSyntax>().Count() + method.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(i => ThrowHelper(i) is not null);
            _drafts.Add(new Draft(
                ScenarioCategory.HappyPath,
                "Valid input",
                guards > 0 ? "Inputs for which none of the guard conditions hold" : "Representative valid inputs",
                expected,
                evidence,
                CodeOnlyReason));
        }

        private void DeriveFromStatements(MethodDeclarationSyntax method, Dictionary<string, string> docExceptions)
        {
            foreach (var node in method.DescendantNodes())
            {
                switch (node)
                {
                    case IfStatementSyntax ifStatement:
                        DeriveIf(ifStatement, docExceptions);
                        break;
                    case ThrowExpressionSyntax throwExpression when throwExpression.Parent is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce:
                        var value = Collapse(coalesce.Left.ToString());
                        AddException(ThrownType(throwExpression.Expression), $"{value} is null", $"{value} = null", [_ids[throwExpression]], docExceptions);
                        break;
                    case InvocationExpressionSyntax invocation when ThrowHelper(invocation) is { } helper:
                        var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.ToString() ?? "argument";
                        AddException(helper.Type, $"{argument} is {helper.Condition}", helper.Condition == "null" ? $"{argument} = null" : $"{argument} is {helper.Condition}", [_ids[invocation]], docExceptions);
                        break;
                    case ThrowStatementSyntax throwStatement when throwStatement.Ancestors().TakeWhile(a => a != method).All(a => a is BlockSyntax):
                        AddException(ThrownType(throwStatement.Expression), "always", "Any input", [_ids[throwStatement]], docExceptions);
                        break;
                    case SwitchSectionSyntax section:
                        DeriveSwitchSection(section, docExceptions);
                        break;
                    case SwitchExpressionArmSyntax arm:
                        var governing = (arm.Parent as SwitchExpressionSyntax)?.GoverningExpression.ToString() ?? "value";
                        var armThrow = arm.Expression as ThrowExpressionSyntax;
                        if (armThrow is not null)
                        {
                            AddException(ThrownType(armThrow.Expression), $"{governing} matches {arm.Pattern}", $"{governing} matching {arm.Pattern}", [_ids[arm]], docExceptions);
                        }
                        else
                        {
                            _drafts.Add(new Draft(ScenarioCategory.HappyPath, $"{governing} matches {Collapse(arm.Pattern.ToString())}", $"{governing} matching {Collapse(arm.Pattern.ToString())}", $"Evaluates to {Collapse(arm.Expression.ToString())}", [_ids[arm]], CodeOnlyReason));
                        }

                        break;
                }
            }
        }

        private void DeriveIf(IfStatementSyntax ifStatement, Dictionary<string, string> docExceptions)
        {
            var condition = Collapse(ifStatement.Condition.ToString());
            var scope = ifStatement.Ancestors().OfType<ForEachStatementSyntax>().FirstOrDefault() is { } loop
                ? $" for an element {loop.Identifier.Text} of {loop.Expression}"
                : "";
            var guardId = _ids[ifStatement];
            var thrown = Throws(ifStatement.Statement);
            string outcome;
            if (thrown is not null)
            {
                AddException(ThrownType(thrown.Expression), condition + scope, $"{condition}{scope}", [guardId, _ids[thrown]], docExceptions);
                outcome = $"throws {ShortName(ThrownType(thrown.Expression))}";
            }
            else if (FirstReturn(ifStatement.Statement) is { } ret)
            {
                var value = ret.Expression is null ? "" : Collapse(ret.Expression.ToString());
                var negative = value is "false" or "null" or "default" or "" || value.StartsWith("Array.Empty", StringComparison.Ordinal) || value.EndsWith(".Empty", StringComparison.Ordinal);
                _drafts.Add(new Draft(
                    negative ? ScenarioCategory.Negative : ScenarioCategory.HappyPath,
                    value.Length == 0 ? $"Returns early when {condition}" : $"Returns {value} when {condition}",
                    $"{condition}{scope}",
                    value.Length == 0 ? "Returns without further work" : $"Returns {value}",
                    [guardId, _ids[ret]],
                    CodeOnlyReason));
                outcome = value.Length == 0 ? "returns early" : $"returns {value}";
            }
            else
            {
                outcome = "takes the branch";
            }

            foreach (var comparison in Comparisons(ifStatement.Condition))
            {
                if (Boundary(comparison) is { } boundary)
                {
                    _drafts.Add(new Draft(
                        ScenarioCategory.Boundary,
                        $"Boundary of {Collapse(comparison.ToString())}",
                        $"{boundary.Subject} = {boundary.Taken} (condition true) and {boundary.Subject} = {boundary.NotTaken} (condition false){scope}",
                        $"{boundary.Taken}: {outcome}; {boundary.NotTaken}: passes this check",
                        [_ids[comparison], guardId],
                        CodeOnlyReason));
                }
            }
        }

        private void DeriveSwitchSection(SwitchSectionSyntax section, Dictionary<string, string> docExceptions)
        {
            var governing = (section.Parent as SwitchStatementSyntax)?.Expression.ToString() ?? "value";
            var labels = Collapse(string.Join(" ", section.Labels.Select(l => l switch
            {
                CaseSwitchLabelSyntax c => c.Value.ToString(),
                CasePatternSwitchLabelSyntax p => p.Pattern.ToString(),
                _ => "default",
            })));
            var thrown = section.Statements.OfType<ThrowStatementSyntax>().FirstOrDefault();
            if (thrown is not null)
            {
                AddException(ThrownType(thrown.Expression), $"{governing} is {labels}", $"{governing} = {labels}", [_ids[section], _ids[thrown]], docExceptions);
                return;
            }

            var ret = section.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault();
            _drafts.Add(new Draft(
                ScenarioCategory.HappyPath,
                $"{governing} is {labels}",
                $"{governing} = {labels}",
                ret?.Expression is { } value ? $"Returns {Collapse(value.ToString())}" : "Executes this case",
                ret is null ? [_ids[section]] : [_ids[section], _ids[ret]],
                CodeOnlyReason));
        }

        private void DeriveCancellation(string? cancellationId)
        {
            if (cancellationId is null)
            {
                return;
            }

            var token = _evidence.Single(e => e.Id == cancellationId).Text.Split(' ')[^1];
            var forwarded = _evidence.Where(e => e.Kind == "dependency" && Regex.IsMatch(e.Text, $"\\b{Regex.Escape(token)}\\b")).ToList();
            _drafts.Add(new Draft(
                ScenarioCategory.Negative,
                "Cancellation",
                $"{token} already cancelled",
                forwarded.Count > 0
                    ? $"Passes {token} to {string.Join(", ", forwarded.Select(f => f.Text))}; the cancellation surfaces as OperationCanceledException"
                    : $"{token} is accepted but not passed to any dependency call found in the method",
                [cancellationId, .. forwarded.Select(f => f.Id)],
                CodeOnlyReason));
        }

        private void AddException(string type, string condition, string inputs, List<string> evidence, Dictionary<string, string> docExceptions)
        {
            var shortType = ShortName(type);
            var documented = docExceptions.TryGetValue(shortType, out var docId);
            if (documented)
            {
                evidence.Add(docId!);
            }

            _drafts.Add(new Draft(
                ScenarioCategory.Exception,
                condition == "always" ? $"Always throws {shortType}" : $"Throws {shortType} when {condition}",
                inputs,
                $"Throws {shortType}",
                evidence,
                documented ? null : CodeOnlyReason));
        }

        // ---- helpers --------------------------------------------------------------------------------------------

        private int Line(SyntaxNode node) => context.Source.StartLine + node.GetLocation().GetLineSpan().StartLinePosition.Line - 1;

        private bool IsDependency(ExpressionSyntax receiver)
        {
            var name = receiver switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.Text,
                _ => null,
            };
            return name is not null && _dependencyNames.Contains(name.TrimStart('_').ToLowerInvariant());
        }

        private static IEnumerable<BinaryExpressionSyntax> Comparisons(ExpressionSyntax condition) =>
            condition.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>().Where(b => b.Kind() is
                SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
                SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or
                SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
            .Where(b => !IsNull(b.Left) && !IsNull(b.Right) && (IsConstantLike(b.Left) || IsConstantLike(b.Right)));

        private static bool IsNull(ExpressionSyntax e) => e.IsKind(SyntaxKind.NullLiteralExpression);

        private static bool IsConstantLike(ExpressionSyntax e) => e switch
        {
            LiteralExpressionSyntax => true,
            PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } => true,
            IdentifierNameSyntax id => id.Identifier.Text.Length > 0 && char.IsUpper(id.Identifier.Text[0]),
            MemberAccessExpressionSyntax m => char.IsUpper(m.Name.Identifier.Text[0]) && m.Expression is IdentifierNameSyntax { Identifier.Text: [var c, ..] } && char.IsUpper(c),

            // Static factory with constant arguments, e.g. TimeSpan.FromHours(24).
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax factory } call =>
                IsConstantLike(factory) && call.ArgumentList.Arguments.All(a => IsConstantLike(a.Expression)),
            _ => false,
        };

        private static (string Subject, string Taken, string NotTaken)? Boundary(BinaryExpressionSyntax comparison)
        {
            var kind = comparison.Kind();
            var (subject, constant) = (comparison.Left, comparison.Right);
            if (IsConstantLike(comparison.Left) && !IsConstantLike(comparison.Right))
            {
                (subject, constant) = (comparison.Right, comparison.Left);
                kind = kind switch
                {
                    SyntaxKind.LessThanExpression => SyntaxKind.GreaterThanExpression,
                    SyntaxKind.LessThanOrEqualExpression => SyntaxKind.GreaterThanOrEqualExpression,
                    SyntaxKind.GreaterThanExpression => SyntaxKind.LessThanExpression,
                    SyntaxKind.GreaterThanOrEqualExpression => SyntaxKind.LessThanOrEqualExpression,
                    _ => kind,
                };
            }

            var c = constant.ToString();
            string Plus() => Offset(constant, +1) ?? $"just above {c}";
            string Minus() => Offset(constant, -1) ?? $"just below {c}";
            var (taken, notTaken) = kind switch
            {
                SyntaxKind.LessThanExpression => (Minus(), c),
                SyntaxKind.LessThanOrEqualExpression => (c, Plus()),
                SyntaxKind.GreaterThanExpression => (Plus(), c),
                SyntaxKind.GreaterThanOrEqualExpression => (c, Minus()),
                SyntaxKind.EqualsExpression => (c, Plus()),
                SyntaxKind.NotEqualsExpression => (Plus(), c),
                _ => (c, c),
            };
            return (Collapse(subject.ToString()), taken, notTaken);
        }

        /// <summary>Adds an integer step to a numeric literal, keeping its suffix (<c>100m</c> → <c>101m</c>); null for non-literals.</summary>
        private static string? Offset(ExpressionSyntax constant, int delta)
        {
            var text = constant.ToString().Replace("_", "", StringComparison.Ordinal);
            var suffix = new string([.. text.Reverse().TakeWhile(char.IsLetter).Reverse()]);
            var number = text[..^suffix.Length];
            if (suffix.Length > 0 && suffix.ToLowerInvariant() is not ("m" or "l" or "u" or "ul" or "lu" or "d" or "f"))
            {
                return null;
            }

            return decimal.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                ? (value + delta).ToString(CultureInfo.InvariantCulture) + suffix
                : null;
        }

        private static ThrowStatementSyntax? Throws(StatementSyntax statement) => statement switch
        {
            ThrowStatementSyntax t => t,
            BlockSyntax { Statements: [ThrowStatementSyntax t] } => t,
            BlockSyntax b => b.Statements.LastOrDefault() as ThrowStatementSyntax,
            _ => null,
        };

        private static ReturnStatementSyntax? FirstReturn(StatementSyntax statement) => statement switch
        {
            ReturnStatementSyntax r => r,
            BlockSyntax b => b.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault(),
            _ => null,
        };

        private static (string Type, string Condition)? ThrowHelper(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access
                || !access.Name.Identifier.Text.StartsWith("ThrowIf", StringComparison.Ordinal)
                || !access.Expression.ToString().EndsWith("Exception", StringComparison.Ordinal))
            {
                return null;
            }

            var words = WordBoundary().Split(access.Name.Identifier.Text["ThrowIf".Length..]).Select(w => w.ToLowerInvariant());
            return (access.Expression.ToString(), string.Join(' ', words));
        }

        private static string ThrownType(ExpressionSyntax? expression) => expression switch
        {
            ObjectCreationExpressionSyntax creation => creation.Type.ToString(),
            null => "the caught exception",
            _ => Collapse(expression.ToString()),
        };

        private static SyntaxNode Statement(SyntaxNode node) => node.FirstAncestorOrSelf<StatementSyntax>() ?? node;

        private static string ShortName(string type) => type[(type.LastIndexOf('.') + 1)..];

        private static string Collapse(string text) =>
            string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

        private static bool LooksCovered(string testName, Draft draft)
        {
            var name = testName[(testName.LastIndexOf('.') + 1)..].ToLowerInvariant();
            var negativeWords = new[] { "throw", "invalid", "null", "empty", "negative", "fail", "error", "exception" };
            return draft.Category switch
            {
                ScenarioCategory.HappyPath => !negativeWords.Any(name.Contains),
                ScenarioCategory.Exception => name.Contains("throw", StringComparison.Ordinal)
                    || name.Contains(draft.Expected.Split(' ')[^1].Replace("Exception", "", StringComparison.Ordinal).ToLowerInvariant(), StringComparison.Ordinal),
                _ => false,
            };
        }
    }

    private sealed record Draft(ScenarioCategory Category, string Title, string Inputs, string Expected, List<string> Evidence, string? ReviewReason);
}
