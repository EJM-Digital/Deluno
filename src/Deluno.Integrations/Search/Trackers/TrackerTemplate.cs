using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The Go-template subset tracker definitions are written in (#606).
///
/// <para>Values are strings, null or lists of strings. The names a
/// definition reads: <c>.Keywords</c>, <c>.Query.IMDBID</c>, <c>.Config.sort</c>,
/// <c>.Result.title</c>, and <c>.True</c>, which is "True", and <c>.False</c>,
/// which is null. A checkbox that is off reads as <c>.False</c>, so
/// <c>eq .Config.x .False</c> asks whether it is off.</para>
///
/// <para>Actions: output, <c>if</c>/<c>else if</c>/<c>else</c>/<c>end</c> and
/// <c>range</c>. Functions: and, or, not, eq, ne, join, re_replace, len,
/// print. The empty string, null and an empty list are false.</para>
/// </summary>
public static class TrackerTemplate
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Node>> Parsed = new(StringComparer.Ordinal);

    /// <summary>
    /// Evaluates <paramref name="template"/>. <paramref name="encode"/>, when
    /// given, is applied to every value an action writes and never to the
    /// template's own text, which is how a search path URL-encodes the
    /// keywords without encoding its slashes.
    /// </summary>
    public static string Render(string? template, IReadOnlyDictionary<string, object?> variables, Func<string, string>? encode = null)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        if (!template.Contains("{{", StringComparison.Ordinal))
        {
            return template;
        }

        var nodes = Parsed.GetOrAdd(template, Parse);
        var output = new StringBuilder(template.Length);
        var scope = new Scope(variables, Dot: null, encode);
        foreach (var node in nodes)
        {
            node.Write(output, scope);
        }

        return output.ToString();
    }

    /// <summary>Reads a template without running it; throws FormatException if Deluno cannot run it.</summary>
    public static void Validate(string? template)
    {
        if (!string.IsNullOrEmpty(template) && template.Contains("{{", StringComparison.Ordinal))
        {
            Parsed.GetOrAdd(template, Parse);
        }
    }

    /// <summary>Whether a value counts as true, as Go's <c>if</c> judges it.</summary>
    public static bool IsTrue(object? value) => value switch
    {
        null => false,
        string text => text.Length > 0,
        bool flag => flag,
        ICollection collection => collection.Count > 0,
        _ => true
    };

    public static string? AsString(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "True" : null,
        IEnumerable<string> list => string.Join(" ", list),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private sealed record Scope(IReadOnlyDictionary<string, object?> Variables, object? Dot, Func<string, string>? Encode)
    {
        public object? Lookup(string name)
        {
            if (name == ".")
            {
                return Dot;
            }

            return Variables.TryGetValue(name, out var value) ? value : null;
        }
    }

    // ---- nodes -------------------------------------------------------------

    private abstract class Node
    {
        public abstract void Write(StringBuilder output, Scope scope);
    }

    private sealed class TextNode(string text) : Node
    {
        public override void Write(StringBuilder output, Scope scope) => output.Append(text);
    }

    private sealed class OutputNode(Expr expression) : Node
    {
        public override void Write(StringBuilder output, Scope scope)
        {
            var value = AsString(expression.Evaluate(scope));
            if (value is null)
            {
                return;
            }

            output.Append(scope.Encode is null ? value : scope.Encode(value));
        }
    }

    private sealed class IfNode(IReadOnlyList<(Expr Condition, IReadOnlyList<Node> Body)> branches, IReadOnlyList<Node> otherwise) : Node
    {
        public override void Write(StringBuilder output, Scope scope)
        {
            foreach (var (condition, body) in branches)
            {
                if (IsTrue(condition.Evaluate(scope)))
                {
                    foreach (var node in body)
                    {
                        node.Write(output, scope);
                    }

                    return;
                }
            }

            foreach (var node in otherwise)
            {
                node.Write(output, scope);
            }
        }
    }

    private sealed class RangeNode(Expr source, IReadOnlyList<Node> body, IReadOnlyList<Node> otherwise) : Node
    {
        public override void Write(StringBuilder output, Scope scope)
        {
            var items = source.Evaluate(scope) switch
            {
                null => [],
                string text => text.Length == 0 ? [] : [text],
                IEnumerable enumerable => enumerable.Cast<object?>().ToArray(),
                var single => [single]
            };

            if (items.Length == 0)
            {
                foreach (var node in otherwise)
                {
                    node.Write(output, scope);
                }

                return;
            }

            foreach (var item in items)
            {
                var inner = scope with { Dot = item };
                foreach (var node in body)
                {
                    node.Write(output, inner);
                }
            }
        }
    }

    // ---- expressions -------------------------------------------------------

    private abstract class Expr
    {
        public abstract object? Evaluate(Scope scope);
    }

    private sealed class LiteralExpr(object? value) : Expr
    {
        public override object? Evaluate(Scope scope) => value;
    }

    private sealed class VariableExpr(string name) : Expr
    {
        public override object? Evaluate(Scope scope) => scope.Lookup(name);
    }

    private sealed class CallExpr(string function, IReadOnlyList<Expr> arguments) : Expr
    {
        public override object? Evaluate(Scope scope)
        {
            switch (function)
            {
                // Go's and/or return an operand, not a bool, and stop early.
                case "and":
                {
                    object? last = null;
                    foreach (var argument in arguments)
                    {
                        last = argument.Evaluate(scope);
                        if (!IsTrue(last))
                        {
                            return last;
                        }
                    }

                    return last;
                }
                case "or":
                {
                    object? last = null;
                    foreach (var argument in arguments)
                    {
                        last = argument.Evaluate(scope);
                        if (IsTrue(last))
                        {
                            return last;
                        }
                    }

                    return last;
                }
            }

            var values = arguments.Select(argument => argument.Evaluate(scope)).ToArray();
            return function switch
            {
                "not" => IsTrue(values.FirstOrDefault()) ? null : "True",
                // eq a b c: true if a equals any of the rest.
                "eq" => values.Length > 1 && values.Skip(1).Any(other => Same(values[0], other)) ? "True" : null,
                "ne" => values.Length > 1 && !Same(values[0], values[1]) ? "True" : null,
                "join" => values.Length > 1 ? string.Join(AsString(values[1]) ?? string.Empty, AsList(values[0])) : string.Join(",", AsList(values.FirstOrDefault())),
                "re_replace" => values.Length > 2
                    ? TrackerRegex.Replace(AsString(values[0]) ?? string.Empty, AsString(values[1]) ?? string.Empty, AsString(values[2]) ?? string.Empty)
                    : AsString(values.FirstOrDefault()),
                "len" => AsList(values.FirstOrDefault()).Count.ToString(CultureInfo.InvariantCulture),
                "print" => string.Concat(values.Select(AsString)),
                "index" => values.Length > 1 && int.TryParse(AsString(values[1]), out var at) && AsList(values[0]) is { } list && at >= 0 && at < list.Count ? list[at] : null,
                _ => throw new FormatException($"The template function '{function}' is not supported.")
            };
        }

        private static bool Same(object? left, object? right)
        {
            var a = AsString(left);
            var b = AsString(right);
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b) || string.Equals(a, b, StringComparison.Ordinal);
        }

        private static IReadOnlyList<string> AsList(object? value) => value switch
        {
            null => [],
            string text => [text],
            IEnumerable enumerable => enumerable.Cast<object?>().Select(AsString).OfType<string>().ToArray(),
            _ => [AsString(value) ?? string.Empty]
        };
    }

    // ---- parsing -----------------------------------------------------------

    private static readonly HashSet<string> Functions = ["and", "or", "not", "eq", "ne", "join", "re_replace", "len", "print", "index"];

    private static IReadOnlyList<Node> Parse(string template)
    {
        var segments = Split(template);
        var position = 0;
        var nodes = ParseBlock(segments, ref position, out var terminator);
        if (terminator is not null)
        {
            throw new FormatException($"Unexpected '{{{{ {terminator} }}}}' in a tracker template.");
        }

        return nodes;
    }

    private readonly record struct Segment(bool IsAction, string Text);

    private static List<Segment> Split(string template)
    {
        var segments = new List<Segment>();
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                segments.Add(new Segment(false, template[index..]));
                break;
            }

            var close = FindClose(template, open + 2);
            if (close < 0)
            {
                segments.Add(new Segment(false, template[index..]));
                break;
            }

            var text = template[index..open];
            var action = template[(open + 2)..close];
            if (action.StartsWith('-'))
            {
                text = text.TrimEnd();
                action = action[1..];
            }

            var trimNext = action.EndsWith('-');
            if (trimNext)
            {
                action = action[..^1];
            }

            if (text.Length > 0)
            {
                segments.Add(new Segment(false, text));
            }

            segments.Add(new Segment(true, action.Trim()));
            index = close + 2;
            if (trimNext)
            {
                while (index < template.Length && char.IsWhiteSpace(template[index]))
                {
                    index++;
                }
            }
        }

        return segments;
    }

    /// <summary>The closing braces, skipping any inside a quoted string.</summary>
    private static int FindClose(string template, int from)
    {
        var quote = '\0';
        for (var i = from; i < template.Length - 1; i++)
        {
            var c = template[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '`')
            {
                quote = c;
            }
            else if (c == '}' && template[i + 1] == '}')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Nodes until an else, else if or end, which is handed back as the terminator.</summary>
    private static List<Node> ParseBlock(List<Segment> segments, ref int position, out string? terminator)
    {
        var nodes = new List<Node>();
        while (position < segments.Count)
        {
            var segment = segments[position++];
            if (!segment.IsAction)
            {
                nodes.Add(new TextNode(segment.Text));
                continue;
            }

            var action = segment.Text;
            if (action.Length == 0 || action.StartsWith("/*", StringComparison.Ordinal))
            {
                continue;
            }

            if (action == "end" || action == "else" || action.StartsWith("else ", StringComparison.Ordinal))
            {
                terminator = action;
                return nodes;
            }

            if (action.StartsWith("if ", StringComparison.Ordinal))
            {
                nodes.Add(ParseIf(segments, ref position, action[3..]));
            }
            else if (action.StartsWith("range ", StringComparison.Ordinal))
            {
                var source = ParseExpression(action[6..]);
                var body = ParseBlock(segments, ref position, out var end);
                IReadOnlyList<Node> otherwise = [];
                if (end == "else")
                {
                    otherwise = ParseBlock(segments, ref position, out end);
                }

                if (end != "end")
                {
                    throw new FormatException("A range in a tracker template has no end.");
                }

                nodes.Add(new RangeNode(source, body, otherwise));
            }
            else
            {
                nodes.Add(new OutputNode(ParseExpression(action)));
            }
        }

        terminator = null;
        return nodes;
    }

    private static IfNode ParseIf(List<Segment> segments, ref int position, string condition)
    {
        var branches = new List<(Expr, IReadOnlyList<Node>)>();
        IReadOnlyList<Node> otherwise = [];
        var current = ParseExpression(condition);
        while (true)
        {
            var body = ParseBlock(segments, ref position, out var terminator);
            branches.Add((current, body));
            if (terminator == "end")
            {
                break;
            }

            if (terminator is not null && terminator.StartsWith("else if ", StringComparison.Ordinal))
            {
                current = ParseExpression(terminator[8..]);
                continue;
            }

            if (terminator == "else")
            {
                otherwise = ParseBlock(segments, ref position, out terminator);
                if (terminator != "end")
                {
                    throw new FormatException("An if in a tracker template has no end.");
                }

                break;
            }

            throw new FormatException("An if in a tracker template has no end.");
        }

        return new IfNode(branches, otherwise);
    }

    private static Expr ParseExpression(string text)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var expression = ParsePipeline(tokens, ref position);

        // A stray closing bracket is a typo Deluno forgives rather than
        // failing a whole definition over.
        while (position < tokens.Count && tokens[position] == ")")
        {
            position++;
        }

        if (position != tokens.Count)
        {
            throw new FormatException($"Could not read the template action '{text}'.");
        }

        return expression;
    }

    private static Expr ParsePipeline(List<string> tokens, ref int position)
    {
        var expression = ParseCommand(tokens, ref position, piped: null);
        while (position < tokens.Count && tokens[position] == "|")
        {
            position++;
            expression = ParseCommand(tokens, ref position, piped: expression);
        }

        return expression;
    }

    private static Expr ParseCommand(List<string> tokens, ref int position, Expr? piped)
    {
        if (position < tokens.Count && Functions.Contains(tokens[position]))
        {
            var function = tokens[position++];
            var arguments = new List<Expr>();
            while (position < tokens.Count && tokens[position] is not ")" and not "|")
            {
                arguments.Add(ParseOperand(tokens, ref position));
            }

            if (piped is not null)
            {
                arguments.Add(piped);
            }

            return new CallExpr(function, arguments);
        }

        var operand = ParseOperand(tokens, ref position);
        if (piped is not null)
        {
            throw new FormatException("Only a function can take a piped value in a tracker template.");
        }

        return operand;
    }

    private static Expr ParseOperand(List<string> tokens, ref int position)
    {
        if (position >= tokens.Count)
        {
            throw new FormatException("A template action ended early.");
        }

        var token = tokens[position++];
        if (token == "(")
        {
            var inner = ParsePipeline(tokens, ref position);
            if (position >= tokens.Count || tokens[position] != ")")
            {
                throw new FormatException("A template action has an unclosed bracket.");
            }

            position++;
            return inner;
        }

        if (token.StartsWith('"'))
        {
            return new LiteralExpr(Unquote(token));
        }

        if (token.StartsWith('`'))
        {
            return new LiteralExpr(token[1..^1]);
        }

        if (token.StartsWith('.'))
        {
            return new VariableExpr(token);
        }

        return token switch
        {
            "true" => new LiteralExpr("True"),
            "false" or "nil" => new LiteralExpr(null),
            _ when Functions.Contains(token) => new CallExpr(token, []),
            _ => new LiteralExpr(token)
        };
    }

    private static string Unquote(string token)
    {
        var body = token[1..^1];
        if (!body.Contains('\\'))
        {
            return body;
        }

        var result = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i == body.Length - 1)
            {
                result.Append(body[i]);
                continue;
            }

            var next = body[++i];
            result.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
        }

        return result.ToString();
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '(' or ')' or '|')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            if (c is '"' or '`')
            {
                var start = i++;
                while (i < text.Length && text[i] != c)
                {
                    if (c == '"' && text[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                i = Math.Min(i + 1, text.Length);
                tokens.Add(text[start..i]);
                continue;
            }

            var from = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '(' and not ')' and not '|' and not '"')
            {
                i++;
            }

            tokens.Add(text[from..i]);
        }

        return tokens;
    }
}

/// <summary>
/// A definition's regular expressions are ordinary .NET regular expressions;
/// only the replacement's Go-style <c>${1}</c> and .NET's <c>$1</c> mean the
/// same thing and need translating.
/// </summary>
internal static class TrackerRegex
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static Regex Get(string pattern)
        => Cache.GetOrAdd(pattern, static value => new Regex(value, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));

    public static string Replace(string input, string pattern, string replacement)
        => Get(pattern).Replace(input, replacement);
}
