using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Njsast.Ast;

namespace Njsast.Reader
{
    // A recursive descent parser operates by defining functions for all
    // syntactic elements, and recursively calling those, each function
    // advancing the input stream and returning an AST node. Precedence
    // of constructs (for example, the fact that `!x[1]` means `!(x[1])`
    // instead of `(!x)[1]` is handled by the fact that the parser
    // function that parses unary prefix operators is called first, and
    // in turn calls the function that parses `[]` subscripts — that
    // way, it'll receive the node for `x[1]` already parsed, and wraps
    // *that* in the unary operator node.
    //
    // Acorn uses an [operator precedence parser][opp] to handle binary
    // operator precedence, because it is much more compact than using
    // the technique outlined above, which uses different, nesting
    // functions to specify precedence, for all of the ten binary
    // precedence levels that JavaScript defines.
    //
    // [opp]: http://en.wikipedia.org/wiki/Operator-precedence_parser

    public sealed partial class Parser
    {
        sealed class Property
        {
            public bool Init;
            public bool Get;
            public bool Set;

            public bool this[PropertyKind kind]
            {
                get
                {
                    switch (kind)
                    {
                        case PropertyKind.Initialise:
                            return Init;
                        case PropertyKind.Get:
                            return Get;
                        case PropertyKind.Set:
                            return Set;
                        default:
                            throw new InvalidOperationException();
                    }
                }
                set
                {
                    switch (kind)
                    {
                        case PropertyKind.Initialise:
                            Init = value;
                            break;
                        case PropertyKind.Get:
                            Get = value;
                            break;
                        case PropertyKind.Set:
                            Set = value;
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
        }

        // Check if property name clashes with already added.
        // Object/class getters and setters are not allowed to clash —
        // either with each other or with an init property — and in
        // strict mode, init properties are also not allowed to be repeated.
        void CheckPropertyClash([NotNull] AstObjectProperty prop, IDictionary<string, Property> propHash)
        {
            if (Options.EcmaVersion >= 6 &&
                (prop is AstObjectGetter || prop is AstObjectSetter || prop is AstObjectKeyVal))
                return;
            var key = prop.Key;
            string name;
            if (key is string)
                name = (string) key;
            else
            {
                return;
            }

            var kind = PropertyKind.Initialise;
            if (Options.EcmaVersion >= 6)
            {
                if (name == "__proto__" && kind == PropertyKind.Initialise)
                {
                    if (propHash.ContainsKey("proto"))
                        RaiseRecoverable(prop.Start, "Redefinition of __proto__ property");
                    propHash.Add("proto", new Property());
                }

                return;
            }

            if (propHash.TryGetValue(name, out var other))
            {
                bool redefinition;
                if (kind == PropertyKind.Initialise)
                {
                    redefinition = _strict && other.Init || other.Get || other.Set;
                }
                else
                {
                    redefinition = other.Init || other[kind];
                }

                if (redefinition)
                    RaiseRecoverable(prop.Start, "Redefinition of property");
            }
            else
            {
                other = propHash[name] = new Property();
            }

            other[kind] = true;
        }

        // ### Expression parsing

        // These nest, from the most general expression type at the top to
        // 'atomic', nondivisible expression types at the bottom. Most of
        // the functions will simply let the function(s) below them parse,
        // and, *if* the syntactic construct they handle is present, wrap
        // the AST node that the inner parser gave them in another node.

        // Parse a full expression. The optional arguments are used to
        // forbid the `in` operator (in for loops initalization expressions)
        // and provide reference for storing '=' operator inside shorthand
        // property assignment in contexts where both object expression
        // and object pattern might appear (so it's possible to raise
        // delayed syntax error at correct position).
        public AstNode ParseExpression(bool noIn = false, [CanBeNull] DestructuringErrors refDestructuringErrors = null)
        {
            var startLocation = Start;
            var expr = ParseMaybeAssign(noIn, refDestructuringErrors);
            if (Type == TokenType.Comma)
            {
                var expressions = new StructList<AstNode>();
                expressions.Add(expr);
                while (Eat(TokenType.Comma))
                {
                    expressions.Add(ParseMaybeAssign(noIn, refDestructuringErrors));
                }

                return new AstSequence(this, startLocation, _lastTokEnd, ref expressions);
            }

            return expr;
        }

        // Parse an assignment expression. This includes applications of
        // operators like `+=`.
        AstNode ParseMaybeAssign(bool noIn = false, DestructuringErrors refDestructuringErrors = null,
            [CanBeNull] Func<Parser, AstNode, int, Position, AstNode> afterLeftParse = null)
        {
            if (_inGenerator && IsContextual("yield"))
                return ParseYield();

            var ownDestructuringErrors = false;
            Position oldParenAssign = default;
            Position oldTrailingComma = default;
            if (refDestructuringErrors != null)
            {
                oldParenAssign = refDestructuringErrors.ParenthesizedAssign;
                oldTrailingComma = refDestructuringErrors.TrailingComma;
                refDestructuringErrors.ParenthesizedAssign = refDestructuringErrors.TrailingComma = default;
            }
            else
            {
                refDestructuringErrors = new DestructuringErrors();
                ownDestructuringErrors = true;
            }

            var startLoc = Start;
            if (Type == TokenType.ParenL || Type == TokenType.Name)
                _potentialArrowAt = Start;
            var left = ParseMaybeConditional(noIn, refDestructuringErrors);
            if (afterLeftParse != null)
                left = afterLeftParse(this, left, Start.Index, startLoc);
            if (Type == TokenType.Eq || Type == TokenType.Assign)
            {
                CheckPatternErrors(refDestructuringErrors, true);
                if (!ownDestructuringErrors) refDestructuringErrors.Reset();
                var @operator = StringToOperator((string) Value);
                var leftNode = Type == TokenType.Eq ? ToAssignable(left) : left;
                refDestructuringErrors.ShorthandAssign = default; // reset because shorthand default was used correctly
                CheckLVal(leftNode, false, null);
                Next();
                var right = ParseMaybeAssign(noIn);
                return new AstAssign(this, startLoc, _lastTokEnd, leftNode, right, @operator);
            }

            if (ownDestructuringErrors) CheckExpressionErrors(refDestructuringErrors, true);
            if (oldParenAssign.Line > 0) refDestructuringErrors.ParenthesizedAssign = oldParenAssign;
            if (oldTrailingComma.Line > 0) refDestructuringErrors.TrailingComma = oldTrailingComma;
            return left;
        }

        static Operator StringToOperator([NotNull] string s)
        {
            switch (s)
            {
                case "+": return Operator.Addition;
                case "-": return Operator.Subtraction;
                case "*": return Operator.Multiplication;
                case "/": return Operator.Division;
                case "%": return Operator.Modulus;
                case "**": return Operator.Power;
                case "<<": return Operator.LeftShift;
                case ">>": return Operator.RightShift;
                case ">>>": return Operator.RightShiftUnsigned;
                case "&": return Operator.BitwiseAnd;
                case "|": return Operator.BitwiseOr;
                case "^": return Operator.BitwiseXOr;

                case "==": return Operator.Equals;
                case "===": return Operator.StrictEquals;
                case "!=": return Operator.NotEquals;
                case "!==": return Operator.StrictNotEquals;
                case "<": return Operator.LessThan;
                case "<=": return Operator.LessEquals;
                case ">": return Operator.GreaterThan;
                case ">=": return Operator.GreaterEquals;
                case "&&": return Operator.LogicalAnd;
                case "||": return Operator.LogicalOr;

                case "=": return Operator.Assignment;
                case "+=": return Operator.AdditionAssignment;
                case "-=": return Operator.SubtractionAssignment;
                case "*=": return Operator.MultiplicationAssignment;
                case "/=": return Operator.DivisionAssignment;
                case "%=": return Operator.ModulusAssignment;
                case "**=": return Operator.PowerAssignment;
                case "<<=": return Operator.LeftShiftAssignment;
                case ">>=": return Operator.RightShiftAssignment;
                case ">>>=": return Operator.RightShiftUnsignedAssignment;
                case "&=": return Operator.BitwiseAndAssignment;
                case "|=": return Operator.BitwiseOrAssignment;
                case "^=": return Operator.BitwiseXOrAssignment;

                case "++": return Operator.Increment;
                case "--": return Operator.Decrement;
                case "~": return Operator.BitwiseNot;
                case "!": return Operator.LogicalNot;
                case "delete": return Operator.Delete;
                case "in": return Operator.In;
                case "instanceof": return Operator.InstanceOf;
                case "void": return Operator.Void;
                case "typeof": return Operator.TypeOf;

                default:
                    throw new ArgumentException();
            }
        }

        // Parse a ternary conditional (`?:`) operator.
        AstNode ParseMaybeConditional(bool noIn, DestructuringErrors refDestructuringErrors)
        {
            var startLoc = Start;
            var expr = ParseExpressionOperators(noIn, refDestructuringErrors);
            if (CheckExpressionErrors(refDestructuringErrors))
                return expr;
            if (Eat(TokenType.Question))
            {
                var consequent = ParseMaybeAssign();
                Expect(TokenType.Colon);
                var alternate = ParseMaybeAssign(noIn);
                return new AstConditional(this, startLoc, _lastTokEnd, expr, consequent, alternate);
            }

            return expr;
        }

        // Start the precedence parser.
        AstNode ParseExpressionOperators(bool noIn, DestructuringErrors refDestructuringErrors)
        {
            var startLoc = Start;
            var expr = ParseMaybeUnary(refDestructuringErrors, false);
            if (CheckExpressionErrors(refDestructuringErrors))
                return expr;
            return expr.Start.Index == startLoc.Index && expr is AstArrow
                ? expr
                : ParseExpressionOperator(expr, startLoc, -1, noIn);
        }

        // Parse binary operators with the operator precedence parsing
        // algorithm. `left` is the left-hand side of the operator.
        // `minPrec` provides context that allows the function to stop and
        // defer further parser to one of its callers when it encounters an
        // operator that has a lower precedence than the set it is parsing.
        AstNode ParseExpressionOperator(AstNode left, Position leftStartLoc, int minPrec, bool noIn)
        {
            var prec = TokenInformation.Types[Type].BinaryOperation;
            if (prec >= 0 && (!noIn || Type != TokenType.In))
            {
                if (prec > minPrec)
                {
                    var op = StringToOperator((string) Value);
                    Next();
                    var startLoc = Start;
                    var right = ParseExpressionOperator(ParseMaybeUnary(null, false), startLoc, prec, noIn);
                    var node = BuildBinary(leftStartLoc, left, right, op);
                    return ParseExpressionOperator(node, leftStartLoc, minPrec, noIn);
                }
            }

            return left;
        }

        [NotNull]
        AstNode BuildBinary(Position startLoc, AstNode left, AstNode right, Operator op)
        {
            return new AstBinary(this, startLoc, _lastTokEnd, left, right, op);
        }

        // Parse unary operators, both prefix and postfix.
        AstNode ParseMaybeUnary(DestructuringErrors refDestructuringErrors, bool sawUnary)
        {
            var startLoc = Start;
            AstNode expr;
            if (_inAsync && IsContextual("await"))
            {
                expr = ParseAwait();
                sawUnary = true;
            }
            else if (TokenInformation.Types[Type].Prefix)
            {
                var update = Type == TokenType.IncDec;
                var @operator = StringToOperator((string) Value);
                Next();
                var argument = ParseMaybeUnary(null, true);
                CheckExpressionErrors(refDestructuringErrors, true);
                if (update) CheckLVal(argument, false, null);
                else if (_strict && @operator == Operator.Delete &&
                         argument is AstSymbol)
                    RaiseRecoverable(startLoc, "Deleting local variable in strict mode");
                else sawUnary = true;
                expr = new AstUnaryPrefix(this, startLoc, _lastTokEnd, @operator, argument);
            }
            else
            {
                expr = ParseExpressionSubscripts(refDestructuringErrors);
                if (CheckExpressionErrors(refDestructuringErrors))
                    return expr;
                while (TokenInformation.Types[Type].Postfix && !CanInsertSemicolon())
                {
                    var @operator = StringToOperator((string) Value);
                    CheckLVal(expr, false, null);
                    Next();
                    expr = new AstUnaryPostfix(this, startLoc, _lastTokEnd, ToPostfix(@operator), expr);
                }
            }

            if (!sawUnary && Eat(TokenType.Starstar))
                return BuildBinary(startLoc, expr, ParseMaybeUnary(null, false), Operator.Power);
            return expr;
        }

        static Operator ToPostfix(Operator @operator)
        {
            switch (@operator)
            {
                case Operator.Increment:
                    return Operator.IncrementPostfix;
                case Operator.Decrement:
                    return Operator.DecrementPostfix;
                default:
                    throw new NotImplementedException();
            }
        }

        // Parse call, dot, and `[]`-subscript expressions.
        AstNode ParseExpressionSubscripts([CanBeNull] DestructuringErrors refDestructuringErrors = null)
        {
            var startLoc = Start;
            var expr = ParseExpressionAtom(refDestructuringErrors);
            var skipArrowSubscripts = expr is AstArrow &&
                                      _input.Substring(_lastTokStart.Index, _lastTokEnd.Index - _lastTokStart.Index) !=
                                      ")";
            if (CheckExpressionErrors(refDestructuringErrors) || skipArrowSubscripts)
                return expr;
            var result = ParseSubscripts(expr, startLoc);
            if (refDestructuringErrors != null && result is AstPropAccess)
            {
                if (refDestructuringErrors.ParenthesizedAssign.Index >= result.Start.Index)
                    refDestructuringErrors.ParenthesizedAssign = default;
                if (refDestructuringErrors.ParenthesizedBind.Index >= result.Start.Index)
                    refDestructuringErrors.ParenthesizedBind = default;
            }

            return result;
        }

        [NotNull]
        AstNode ParseSubscripts([NotNull] AstNode @base, Position startLoc, bool noCalls = false)
        {
            var maybeAsyncArrow = Options.EcmaVersion >= 8 && @base is AstSymbol identifierNode &&
                                  identifierNode.Name == "async" &&
                                  _lastTokEnd.Index == @base.End.Index && !CanInsertSemicolon();
            for (;;)
            {
                bool computed;
                if ((computed = Eat(TokenType.BracketL)) || Eat(TokenType.Dot))
                {
                    var property = computed ? ParseExpression() : ParseIdent(true);
                    if (computed) Expect(TokenType.BracketR);
                    if (computed)
                    {
                        @base = new AstSub(this, startLoc, _lastTokEnd, @base, property);
                    }
                    else
                    {
                        @base = new AstDot(this, startLoc, _lastTokEnd, @base, property);
                    }
                }
                else if (!noCalls && Eat(TokenType.ParenL))
                {
                    var refDestructuringErrors = new DestructuringErrors();
                    var oldYieldPos = _yieldPos;
                    var oldAwaitPos = _awaitPos;
                    _yieldPos = default;
                    _awaitPos = default;
                    var expressionList = new StructList<AstNode>();
                    ParseExpressionList(ref expressionList, TokenType.ParenR, Options.EcmaVersion >= 8, false,
                        refDestructuringErrors);
                    if (maybeAsyncArrow && !CanInsertSemicolon() && Eat(TokenType.Arrow))
                    {
                        CheckPatternErrors(refDestructuringErrors, false);
                        CheckYieldAwaitInDefaultParams();
                        _yieldPos = oldYieldPos;
                        _awaitPos = oldAwaitPos;
                        return ParseArrowExpression(startLoc, ref expressionList, true);
                    }

                    CheckExpressionErrors(refDestructuringErrors, true);
                    _yieldPos = oldYieldPos.Line != 0 ? oldYieldPos : _yieldPos;
                    _awaitPos = oldAwaitPos.Line != 0 ? oldAwaitPos : _awaitPos;
                    @base = new AstCall(this, startLoc, _lastTokEnd, @base, ref expressionList);
                }
                else if (Type == TokenType.BackQuote)
                {
                    var quasi = ParseTemplate(true);
                    @base = new AstPrefixedTemplateString(this, startLoc, _lastTokEnd, @base, quasi);
                }
                else
                {
                    return @base;
                }
            }
        }

        // Parse an atomic expression — either a single token that is an
        // expression, an expression started by a keyword like `function` or
        // `new`, or an expression wrapped in punctuation like `()`, `[]`,
        // or `{}`.
        AstNode ParseExpressionAtom([CanBeNull] DestructuringErrors refDestructuringErrors = null)
        {
            var canBeArrow = _potentialArrowAt.Index == Start.Index;
            var startLoc = Start;
            switch (Type)
            {
                case TokenType.Super:
                    if (!_inFunction)
                        Raise(startLoc, "'super' outside of function or class");
                    Next();

                    // The `super` keyword can appear at below:
                    // SuperProperty:
                    //     super [ Expression ]
                    //     super . IdentifierName
                    // SuperCall:
                    //     super Arguments
                    if (Type != TokenType.Dot && Type != TokenType.BracketL && Type != TokenType.ParenL)
                    {
                        Raise(Start, "Unexpected token");
                    }

                    return new AstSuper(this, startLoc, _lastTokEnd);
                case TokenType.This:
                    Next();
                    return new AstThis(this, startLoc, _lastTokEnd);
                case TokenType.Name:
                    var id = ParseIdent(Type != TokenType.Name);
                    if (Options.EcmaVersion >= 8 && id.Name == "async" && !CanInsertSemicolon() &&
                        Eat(TokenType.Function))
                        return ParseFunction(startLoc, false, false, false, true);
                    if (canBeArrow && !CanInsertSemicolon())
                    {
                        if (Eat(TokenType.Arrow))
                        {
                            var arg = new StructList<AstNode>();
                            arg.Add(id);
                            return ParseArrowExpression(startLoc, ref arg);
                        }

                        if (Options.EcmaVersion >= 8 && id.Name == "async" && Type == TokenType.Name)
                        {
                            id = ParseIdent();
                            if (CanInsertSemicolon() || !Eat(TokenType.Arrow))
                            {
                                Raise(Start, "Unexpected token");
                            }

                            var arg = new StructList<AstNode>();
                            arg.Add(id);
                            return ParseArrowExpression(startLoc, ref arg, true);
                        }
                    }

                    return id;
                case TokenType.Regexp:
                    var r = (RegExp) Value;
                    Next();
                    return new AstRegExp(this, startLoc, _lastTokEnd, r);
                case TokenType.Num:
                    if (Value is int intValue)
                        return ParseLiteral(intValue);
                    return ParseLiteral((double) Value);
                case TokenType.String:
                    var s = (string) Value;
                    Next();
                    return new AstString(this, startLoc, _lastTokEnd, s);
                case TokenType.Null:
                    Next();
                    return new AstNull(this, startLoc, _lastTokEnd);
                case TokenType.True:
                    Next();
                    return new AstTrue(this, startLoc, _lastTokEnd);
                case TokenType.False:
                    Next();
                    return new AstFalse(this, startLoc, _lastTokEnd);
                case TokenType.ParenL:
                    var expr = ParseParenAndDistinguishExpression(canBeArrow);
                    if (refDestructuringErrors != null)
                    {
                        if (refDestructuringErrors.ParenthesizedAssign.Line == 0 && !IsSimpleAssignTarget(expr))
                            refDestructuringErrors.ParenthesizedAssign = startLoc;
                        if (refDestructuringErrors.ParenthesizedBind.Line == 0)
                            refDestructuringErrors.ParenthesizedBind = startLoc;
                    }

                    return expr;
                case TokenType.BracketL:
                    Next();
                    var elements = new StructList<AstNode>();
                    ParseExpressionList(ref elements, TokenType.BracketR, true, true, refDestructuringErrors);
                    return new AstArray(this, startLoc, _lastTokEnd, ref elements);
                case TokenType.BraceL:
                    return ParseObj(false, refDestructuringErrors);
                case TokenType.Function:
                    Next();
                    return ParseFunction(startLoc, false, false);
                case TokenType.Class:
                    return ParseClass(startLoc, false, false);
                case TokenType.New:
                    return ParseNew();
                case TokenType.BackQuote:
                    return ParseTemplate();
            }

            Raise(startLoc, "Unexpected token");
            return null;
        }

        [NotNull]
        AstNode ParseLiteral(double value)
        {
            var startLoc = Start;
            var raw = _input.Substring(Start.Index, End.Index - Start.Index);
            Next();
            return new AstNumber(this, startLoc, _lastTokEnd, value, raw);
        }

        AstNode ParseParenExpression()
        {
            Expect(TokenType.ParenL);
            var val = ParseExpression();
            Expect(TokenType.ParenR);
            return val;
        }

        AstNode ParseParenAndDistinguishExpression(bool canBeArrow)
        {
            var startLoc = Start;
            AstNode node;
            var allowTrailingComma = Options.EcmaVersion >= 8;
            if (Options.EcmaVersion >= 6)
            {
                Next();

                var innerStartLoc = Start;
                var exprList = new StructList<AstNode>();
                var first = true;
                var lastIsComma = false;
                var refDestructuringErrors = new DestructuringErrors();
                var oldYieldPos = _yieldPos;
                var oldAwaitPos = _awaitPos;
                Position spreadStart = default;
                _yieldPos = default;
                _awaitPos = default;
                while (Type != TokenType.ParenR)
                {
                    if (first)
                        first = false;
                    else Expect(TokenType.Comma);
                    if (allowTrailingComma && AfterTrailingComma(TokenType.ParenR, true))
                    {
                        lastIsComma = true;
                        break;
                    }

                    if (Type == TokenType.Ellipsis)
                    {
                        spreadStart = Start;
                        exprList.Add(ParseRestBinding());
                        if (Type == TokenType.Comma) Raise(Start, "Comma is not permitted after the rest element");
                        break;
                    }

                    exprList.Add(ParseMaybeAssign(false, refDestructuringErrors,
                        (parser, item, position, location) => item));
                }

                var innerEndLoc = Start;
                Expect(TokenType.ParenR);

                if (canBeArrow && !CanInsertSemicolon() && Eat(TokenType.Arrow))
                {
                    CheckPatternErrors(refDestructuringErrors, false);
                    CheckYieldAwaitInDefaultParams();
                    _yieldPos = oldYieldPos;
                    _awaitPos = oldAwaitPos;
                    return ParseArrowExpression(startLoc, ref exprList);
                }

                if (exprList.Count == 0 || lastIsComma)
                {
                    Raise(_lastTokStart, "Unexpected token");
                }

                if (spreadStart.Line > 0)
                {
                    Raise(spreadStart, "Unexpected token");
                }

                CheckExpressionErrors(refDestructuringErrors, true);
                _yieldPos = oldYieldPos.Line != 0 ? oldYieldPos : _yieldPos;
                _awaitPos = oldAwaitPos.Line != 0 ? oldAwaitPos : _awaitPos;

                if (exprList.Count > 1)
                {
                    node = new AstSequence(this, innerStartLoc, innerEndLoc, ref exprList);
                }
                else
                {
                    node = exprList[0];
                }
            }
            else
            {
                node = ParseParenExpression();
            }

            return node;
        }

        // New's precedence is slightly tricky. It must allow its argument to
        // be a `[]` or dot subscript expression, but not a call — at least,
        // not without wrapping it in parentheses. Thus, it uses the noCalls
        // argument to parseSubscripts to prevent it from consuming the
        // argument list
        [NotNull]
        AstNode ParseNew()
        {
            var nodeStart = Start;
            var meta = ParseIdent(true);
            if (Options.EcmaVersion >= 6 && Eat(TokenType.Dot))
            {
                var identifierNode = ParseIdent(true);
                if (identifierNode.Name != "target")
                    RaiseRecoverable(identifierNode.Start, "The only valid meta property for new is new.target");
                if (!_inFunction)
                    RaiseRecoverable(nodeStart, "new.target can only be used in functions");
                return new AstNewTarget(this, nodeStart, _lastTokEnd);
            }

            var startLoc = Start;
            var callee = ParseSubscripts(ParseExpressionAtom(), startLoc, true);
            var arguments = new StructList<AstNode>();
            if (Eat(TokenType.ParenL))
                ParseExpressionList(ref arguments, TokenType.ParenR, Options.EcmaVersion >= 8, false);
            return new AstNew(this, nodeStart, _lastTokEnd, callee, ref arguments);
        }

        static readonly Regex TemplateRawRegex = new Regex("\r\n?");

        // Parse template expression.
        [NotNull]
        AstTemplateSegment ParseTemplateElement(ref bool isTagged)
        {
            var startLoc = Start;
            string valueStr;
            string rawStr;
            if (Type == TokenType.InvalidTemplate)
            {
                if (!isTagged)
                {
                    RaiseRecoverable(Start, "Bad escape sequence in untagged template literal");
                }

                rawStr = (string) Value;
                valueStr = null;
            }
            else
            {
                rawStr = TemplateRawRegex.Replace(_input.Substring(Start.Index, End.Index - Start.Index), "\n");
                valueStr = (string) Value;
            }

            Next();
            return new AstTemplateSegment(this, startLoc, _lastTokEnd, valueStr, rawStr);
        }

        [NotNull]
        AstTemplateString ParseTemplate(bool isTagged = false)
        {
            var startLoc = Start;
            Next();
            var expressions = new StructList<AstNode>();
            var isTail = Type == TokenType.BackQuote;
            var curElt = ParseTemplateElement(ref isTagged);
            expressions.Add(curElt);
            while (!isTail)
            {
                Expect(TokenType.DollarBraceL);
                expressions.Add(ParseExpression());
                Expect(TokenType.BraceR);
                isTail = Type == TokenType.BackQuote;
                expressions.Add(ParseTemplateElement(ref isTagged));
            }

            Next();
            return new AstTemplateString(this, startLoc, _lastTokEnd, ref expressions);
        }

        bool IsAsyncProp(bool computed, [NotNull] AstNode key)
        {
            return !computed && key is AstSymbol identifierNode && identifierNode.Name == "async" &&
                   (Type == TokenType.Name || Type == TokenType.Num || Type == TokenType.String ||
                    Type == TokenType.BracketL || TokenInformation.Types[Type].Keyword != null) &&
                   !LineBreak.IsMatch(_input.Substring(_lastTokEnd.Index, Start.Index - _lastTokEnd.Index));
        }

        // Parse an object literal or binding pattern.
        [NotNull]
        AstNode ParseObj(bool isPattern, [CanBeNull] DestructuringErrors refDestructuringErrors = null)
        {
            var startLoc = Start;
            var first = true;
            var propHash = new Dictionary<string, Property>();
            var properties = new StructList<AstNode>();
            Next();
            while (!Eat(TokenType.BraceR))
            {
                if (!first)
                {
                    Expect(TokenType.Comma);
                    if (AfterTrailingComma(TokenType.BraceR)) break;
                }
                else first = false;

                var prop = ParseProperty(isPattern, refDestructuringErrors);
                if (!isPattern) CheckPropertyClash(prop, propHash);
                properties.Add(prop);
            }

            if (isPattern)
            {
                return new AstDestructuring(this, startLoc, _lastTokEnd, ref properties, false);
            }

            var objProps = new StructList<AstObjectProperty>();
            objProps.Reserve(properties.Count);
            for (var i = 0; i < properties.Count; i++)
            {
                objProps.Add((AstObjectProperty) properties[(uint) i]);
            }

            return new AstObject(this, startLoc, _lastTokEnd, ref objProps);
        }

        [NotNull]
        AstObjectProperty ParseProperty(bool isPattern, [CanBeNull] DestructuringErrors refDestructuringErrors)
        {
            var isGenerator = false;
            bool isAsync;
            Position startLoc = default;
            var nodeStart = Start;
            if (Options.EcmaVersion >= 6)
            {
                if (isPattern || refDestructuringErrors != null)
                {
                    startLoc = Start;
                }

                if (!isPattern)
                    isGenerator = Eat(TokenType.Star);
            }

            var (computed, key) = ParsePropertyName();
            if (!isPattern && Options.EcmaVersion >= 8 && !isGenerator && IsAsyncProp(computed, key))
            {
                isAsync = true;
                (computed, key) = ParsePropertyName();
            }
            else
            {
                isAsync = false;
            }

            AstNode value;
            PropertyKind kind;
            bool method;
            bool shorthand;
            (value, kind, method, shorthand, computed, key) = ParsePropertyValue(computed, key, isPattern, isGenerator,
                isAsync, startLoc, refDestructuringErrors);
            if (kind == PropertyKind.Get)
            {
                return new AstObjectGetter(this, nodeStart, _lastTokEnd, key, value, false);
            }

            if (kind == PropertyKind.Set)
            {
                return new AstObjectSetter(this, nodeStart, _lastTokEnd, key, value, false);
            }

            if (kind == PropertyKind.Initialise)
            {
                return new AstObjectKeyVal(this, nodeStart, _lastTokEnd, key, value);
            }

            throw new NotImplementedException("parseProperty");
        }

        (AstNode value, PropertyKind kind, bool method, bool shorthand, bool computed, AstNode key) ParsePropertyValue(
            bool computed, AstNode key, bool isPattern, bool isGenerator, bool isAsync, Position startLoc,
            [CanBeNull] DestructuringErrors refDestructuringErrors)
        {
            if ((isGenerator || isAsync) && Type == TokenType.Colon)
            {
                Raise(Start, "Unexpected token");
            }

            if (Eat(TokenType.Colon))
            {
                var value = isPattern ? ParseMaybeDefault(Start) : ParseMaybeAssign(false, refDestructuringErrors);
                return (value, PropertyKind.Initialise, false, false, computed, key);
            }

            if (Options.EcmaVersion >= 6 && Type == TokenType.ParenL)
            {
                if (isPattern)
                {
                    Raise(Start, "Unexpected token");
                }

                var value = ParseMethod(isGenerator, isAsync);
                return (value, PropertyKind.Initialise, true, false, computed, key);
            }

            if (!isPattern &&
                Options.EcmaVersion >= 5 && !computed && key is AstSymbol identifierNode &&
                (identifierNode.Name == "get" || identifierNode.Name == "set") &&
                Type != TokenType.Comma && Type != TokenType.BraceR)
            {
                if (isGenerator || isAsync)
                {
                    Raise(Start, "Unexpected token");
                }

                var kind = identifierNode.Name == "get" ? PropertyKind.Get : PropertyKind.Set;
                (computed, key) = ParsePropertyName();
                key = MakeSymbolMethod(key);
                var value = ParseMethod(false);
                var paramCount = kind == PropertyKind.Get ? 0 : 1;
                if (value.ArgNames.Count != paramCount)
                {
                    var start = value.Start;
                    if (kind == PropertyKind.Get)
                        RaiseRecoverable(start, "getter should have no params");
                    else
                        RaiseRecoverable(start, "setter should have exactly one param");
                }
                else
                {
                    if (kind == PropertyKind.Set && value.ArgNames[0] is AstExpansion)
                        RaiseRecoverable(value.ArgNames[0].Start, "Setter cannot use rest params");
                }

                var valueAcc = new AstAccessor(this, value.Start, value.End, ref value.ArgNames, value.IsGenerator,
                    value.Async, ref value.Body);
                return (valueAcc, kind, false, false, computed, key);
            }

            if (Options.EcmaVersion >= 6 && !computed && key is AstSymbol identifierNode2)
            {
                CheckUnreserved(key.Start, key.End, identifierNode2.Name);
                AstNode value;
                if (isPattern)
                {
                    value = ParseMaybeDefault(startLoc, key);
                }
                else if (Type == TokenType.Eq && refDestructuringErrors != null)
                {
                    if (refDestructuringErrors.ShorthandAssign.Line == 0)
                        refDestructuringErrors.ShorthandAssign = Start;
                    value = ParseMaybeDefault(startLoc, key);
                }
                else
                {
                    value = key;
                }

                return (value, PropertyKind.Initialise, false, true, false, key);
            }

            Raise(Start, "Unexpected token");
            throw new InvalidOperationException();
        }

        AstNode MakeSymbolMethod(AstNode key)
        {
            if (key is AstNumber)
            {
                return new AstSymbolMethod(this, key.Start, key.End, ((AstNumber) key).Literal);
            }

            if (key is AstString)
            {
                return new AstSymbolMethod(this, key.Start, key.End, ((AstString) key).Value);
            }

            if (key is AstSymbol)
            {
                return new AstSymbolMethod(this, key.Start, key.End, ((AstSymbol) key).Name);
            }

            throw new InvalidOperationException(key.ToString());
        }

        (bool computed, AstNode key) ParsePropertyName()
        {
            if (Options.EcmaVersion >= 6)
            {
                if (Eat(TokenType.BracketL))
                {
                    var key = ParseMaybeAssign();
                    Expect(TokenType.BracketR);
                    return (true, key);
                }
            }

            return (false,
                Type == TokenType.Num || Type == TokenType.String ? ParseExpressionAtom() : ParseIdent(true));
        }

        // Parse object or class method.
        [NotNull]
        AstFunction ParseMethod(bool isGenerator, bool isAsync = false)
        {
            var startLoc = Start;
            var oldInGen = _inGenerator;
            var oldInAsync = _inAsync;
            var oldYieldPos = _yieldPos;
            var oldAwaitPos = _awaitPos;
            var oldInFunc = _inFunction;

            if (Options.EcmaVersion < 6 && isGenerator)
                throw new InvalidOperationException();
            if (Options.EcmaVersion < 8 && isAsync)
                throw new InvalidOperationException();

            _inGenerator = isGenerator;
            _inAsync = isAsync;
            _yieldPos = default;
            _awaitPos = default;
            _inFunction = true;
            EnterFunctionScope();
            try
            {
                Expect(TokenType.ParenL);
                var parameters = new StructList<AstNode>();
                ParseBindingList(ref parameters, TokenType.ParenR, false, Options.EcmaVersion >= 8);
                MakeSymbolFunArg(ref parameters);
                CheckYieldAwaitInDefaultParams();
                var body = new StructList<AstNode>();
                var expression = ParseFunctionBody(parameters, startLoc, null, false, ref body);
                return new AstFunction(this, startLoc, _lastTokEnd, null, ref parameters, isGenerator, isAsync,
                    ref body);
            }
            finally
            {
                _inGenerator = oldInGen;
                _inAsync = oldInAsync;
                _yieldPos = oldYieldPos;
                _awaitPos = oldAwaitPos;
                _inFunction = oldInFunc;
            }
        }

        void MakeSymbolFunArg(ref StructList<AstNode> parameters)
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                var par = parameters[(uint) i];
                if (par is AstSymbolFunarg) continue;
                if (par is AstSymbol)
                {
                    parameters[(uint) i] = new AstSymbolFunarg((AstSymbol) par);
                }
            }
        }

        // Parse arrow function expression with given parameters.
        [NotNull]
        AstNode ParseArrowExpression(Position startLoc, ref StructList<AstNode> parameters, bool isAsync = false)
        {
            var oldInGen = _inGenerator;
            var oldInAsync = _inAsync;
            var oldYieldPos = _yieldPos;
            var oldAwaitPos = _awaitPos;
            var oldInFunc = _inFunction;

            EnterFunctionScope();
            if (Options.EcmaVersion < 8 && isAsync)
                throw new InvalidOperationException();

            _inGenerator = false;
            _inAsync = isAsync;
            _yieldPos = default;
            _awaitPos = default;
            _inFunction = true;
            try
            {
                ToAssignableList(ref parameters, true);
                var body = new StructList<AstNode>();
                var expression = ParseFunctionBody(parameters, startLoc, null, true, ref body);
                return new AstArrow(this, startLoc, _lastTokEnd, null, ref parameters, _inGenerator, isAsync, ref body);
            }
            finally
            {
                _inGenerator = oldInGen;
                _inAsync = oldInAsync;
                _yieldPos = oldYieldPos;
                _awaitPos = oldAwaitPos;
                _inFunction = oldInFunc;
            }
        }

        // Parse function body and check parameters.
        bool ParseFunctionBody(in StructList<AstNode> parameters, Position startLoc, [CanBeNull] AstNode id,
            bool isArrowFunction, ref StructList<AstNode> body)
        {
            var isExpression = isArrowFunction && Type != TokenType.BraceL;
            var oldStrict = _strict;
            var useStrict = false;

            bool expression;
            if (isExpression)
            {
                var simpleBody = ParseMaybeAssign();
                body.Add(new AstSimpleStatement(this, simpleBody.Start, simpleBody.End, simpleBody));
                expression = true;
                CheckParams(parameters, false);
            }
            else
            {
                var nonSimple = Options.EcmaVersion >= 7 && !IsSimpleParamList(parameters);
                if (!oldStrict || nonSimple)
                {
                    useStrict = StrictDirective(End.Index);
                    // If this is a strict mode function, verify that argument names
                    // are not repeated, and it does not try to bind the words `eval`
                    // or `arguments`.
                    if (useStrict && nonSimple)
                        RaiseRecoverable(startLoc,
                            "Illegal 'use strict' directive in function with non-simple parameter list");
                }

                // Start a new scope with regard to labels and the `inFunction`
                // flag (restore them to their old value afterwards).
                var oldLabels = new StructList<AstLabel>();
                oldLabels.TransferFrom(ref _labels);
                if (useStrict) _strict = true;

                // Add the params to varDeclaredNames to ensure that an error is thrown
                // if a let/const declaration in the function clashes with one of the params.
                CheckParams(parameters, !oldStrict && !useStrict && !isArrowFunction && IsSimpleParamList(parameters));
                var block = ParseBlock(false);
                body.TransferFrom(ref block.Body);
                expression = false;
                AdaptDirectivePrologue(ref body, ref _strict);
                _labels.TransferFrom(ref oldLabels);
            }

            ExitFunctionScope();

            if (_strict && id != null)
            {
                // Ensure the function name isn't a forbidden identifier in strict mode, e.g. 'eval'
                CheckLVal(id, true, null);
            }

            _strict = oldStrict;

            return expression;
        }

        static bool IsSimpleParamList(in StructList<AstNode> @params)
        {
            foreach (var param in @params)
            {
                if (!(param is AstSymbol))
                {
                    return false;
                }
            }

            return true;
        }

        // Checks function params for various disallowed patterns such as using "eval"
        // or "arguments" and duplicate parameters.
        void CheckParams(in StructList<AstNode> parameters, bool allowDuplicates)
        {
            var nameHash = allowDuplicates ? null : new HashSet<string>();
            foreach (var param in parameters)
                CheckLVal(param, true, VariableKind.Var, nameHash);
        }

        // Parses a comma-separated list of expressions, and returns them as
        // an array. `close` is the token type that ends the list, and
        // `allowEmpty` can be turned on to allow subsequent commas with
        // nothing in between them to be parsed as `null` (which is needed
        // for array literals).
        void ParseExpressionList(ref StructList<AstNode> elements, TokenType close, bool allowTrailingComma,
            bool allowEmpty, [CanBeNull] DestructuringErrors refDestructuringErrors = null)
        {
            var first = true;
            while (!Eat(close))
            {
                if (!first)
                {
                    Expect(TokenType.Comma);
                    if (allowTrailingComma && AfterTrailingComma(close)) break;
                }
                else first = false;

                AstNode element;
                if (allowEmpty && Type == TokenType.Comma)
                    element = null;
                else if (Type == TokenType.Ellipsis)
                {
                    element = ParseSpread(refDestructuringErrors);
                    if (refDestructuringErrors != null && Type == TokenType.Comma &&
                        refDestructuringErrors.TrailingComma.Line == 0)
                        refDestructuringErrors.TrailingComma = Start;
                }
                else
                {
                    element = ParseMaybeAssign(false, refDestructuringErrors);
                }

                elements.Add(element);
            }
        }

        void CheckUnreserved(Position start, Position end, string name)
        {
            if (_inGenerator && name == "yield")
                RaiseRecoverable(start, "Can not use 'yield' as identifier inside a generator");
            if (_inAsync && name == "await")
                RaiseRecoverable(start, "Can not use 'await' as identifier inside an async function");
            if (_keywords.IsMatch(name))
                Raise(start, $"Unexpected keyword '{name}'");
            if (Options.EcmaVersion < 6 &&
                _input.Substring(start.Index, end - start).IndexOf("\\", StringComparison.Ordinal) != -1)
                return;
            var re = _strict ? _reservedWordsStrict : _reservedWords;
            if (re.IsMatch(name))
                RaiseRecoverable(start, $"The keyword '{name}' is reserved");
        }

        // Parse the next token as an identifier. If `liberal` is true (used
        // when parsing properties), it will also convert keywords into
        // identifiers.
        [NotNull]
        AstSymbol ParseIdent(bool liberal = false)
        {
            var startLocation = Start;
            if (liberal && "never".Equals(Options.AllowReserved)) liberal = false;

            string name = null;
            if (Type == TokenType.Name)
            {
                name = (string) Value;
            }
            else if (TokenInformation.Types[Type].Keyword != null)
            {
                name = TokenInformation.Types[Type].Keyword;

                // To fix https://github.com/ternjs/acorn/issues/575
                // `class` and `function` keywords push new context into this.context.
                // But there is no chance to pop the context if the keyword is consumed as an identifier such as a property name.
                // If the previous token is a dot, this does not apply because the context-managing code already ignored the keyword
                if ((name == "class" || name == "function") &&
                    (_lastTokEnd.Index != _lastTokStart.Index + 1 || _input.Get(_lastTokStart.Index) != 46))
                {
                    _context.Pop();
                }
            }
            else
            {
                Raise(Start, "Unexpected token");
            }

            Next();
            var node = new AstSymbol(this, startLocation, _lastTokEnd, name);
            if (!liberal) CheckUnreserved(node.Start, node.Start, node.Name);
            return node;
        }

        // Parses yield expression inside generator.
        [NotNull]
        AstYield ParseYield()
        {
            if (_yieldPos.Line == 0) _yieldPos = Start;

            var startLoc = Start;
            Next();
            var @delegate = false;
            AstNode argument = null;
            if (Type != TokenType.Semi && !CanInsertSemicolon() &&
                (Type == TokenType.Star || TokenInformation.Types[Type].StartsExpression))
            {
                @delegate = Eat(TokenType.Star);
                argument = ParseMaybeAssign();
            }

            return new AstYield(this, startLoc, _lastTokEnd, argument, @delegate);
        }

        [NotNull]
        AstAwait ParseAwait()
        {
            if (_awaitPos.Line == 0) _awaitPos = Start;

            var startLoc = Start;
            Next();
            var argument = ParseMaybeUnary(null, true);

            return new AstAwait(this, startLoc, _lastTokEnd, argument);
        }
    }
}
