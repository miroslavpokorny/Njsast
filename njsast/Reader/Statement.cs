using System;
using System.Collections.Generic;
using System.Diagnostics;
using JetBrains.Annotations;
using Njsast.Ast;

namespace Njsast.Reader
{
    public sealed partial class Parser
    {
        // Parse a program. Initializes the parser, reads any number of
        // statements, and wraps them in a Program node.  Optionally takes a
        // `program` argument.  If present, the statements will be appended
        // to its body instead of creating a new node.
        void ParseTopLevel([NotNull] AstToplevel node)
        {
            var exports = new Dictionary<string, bool>();
            _canBeDirective = true;
            while (Type != TokenType.Eof)
            {
                var stmt = ParseStatement(true, true, exports);

                if (_canBeDirective)
                {
                    if (IsDirectiveCandidate(stmt))
                    {
                        if (IsUseStrictDirective(stmt))
                        {
                            node.HasUseStrictDirective = true;
                            _strict = true;
                            continue;
                        }
                    }
                    else
                    {
                        _canBeDirective = false;
                    }
                }
                node.Body.Add(stmt);
            }

            Next();
            node.End = _lastTokEnd;
        }

        bool IsLet()
        {
            if (Type != TokenType.Name || Options.EcmaVersion < 6 || (string) Value != "let") return false;
            var skip = SkipWhiteSpace.Match(_input, _pos.Index);
            var next = _pos.Index + skip.Groups[0].Length;
            var nextCh = _input[next];
            if (nextCh == 91 || nextCh == 123) return true; // '{' and '['
            if (IsIdentifierStart(nextCh, true))
            {
                var pos = next + 1;
                while (IsIdentifierChar(_input.Get(pos), true))
                    ++pos;
                var ident = _input.Substring(next, pos - next);
                if (!KeywordRelationalOperator.IsMatch(ident))
                    return true;
            }

            return false;
        }

        // check 'async [no LineTerminator here] function'
        // - 'async /*foo*/ function' is OK.
        // - 'async /*\n*/ function' is invalid.
        bool IsAsyncFunction()
        {
            if (Type != TokenType.Name || Options.EcmaVersion < 8 || (string) Value != "async")
                return false;

            var skip = SkipWhiteSpace.Match(_input, _pos.Index);
            var next = _pos.Index + skip.Groups[0].Length;
            return !LineBreak.IsMatch(_input.Substring(_pos.Index, next - _pos.Index)) &&
                   _input.Length >= next + 8 && _input.AsSpan(next, 8).SequenceEqual("function") &&
                   (next + 8 == _input.Length || !IsIdentifierChar(_input.Get(next + 8)));
        }

        // Parse a single statement.
        //
        // If expecting a statement and finding a slash operator, parse a
        // regular expression literal. This is to handle cases like
        // `if (foo) /blah/.exec(foo)`, where looking at the previous token
        // does not help.
        [NotNull]
        AstNode ParseStatement(bool declaration, bool topLevel = false,
            [CanBeNull] IDictionary<string, bool> exports = null)
        {
            var starttype = Type;
            var startLocation = Start;
            VariableKind? kind = null;

            if (IsLet())
            {
                starttype = TokenType.Var;
                kind = VariableKind.Let;
            }

            // Most types of statements are recognized by the keyword they
            // start with. Many are trivial to parse, some require a bit of
            // complexity.

            switch (starttype)
            {
                case TokenType.Break:
                case TokenType.Continue:
                    return ParseBreakContinueStatement(startLocation, TokenInformation.Types[starttype].Keyword);
                case TokenType.Debugger:
                    return ParseDebuggerStatement(startLocation);
                case TokenType.Do:
                    return ParseDoStatement(startLocation);
                case TokenType.For:
                    return ParseForStatement(startLocation);
                case TokenType.Function:
                    if (!declaration && Options.EcmaVersion >= 6)
                    {
                        Raise(startLocation, "Unexpected token");
                    }

                    return ParseFunctionStatement(startLocation, false);
                case TokenType.Class:
                    if (!declaration)
                    {
                        Raise(startLocation, "Unexpected token");
                    }

                    return ParseClass(startLocation, true, false);
                case TokenType.If:
                    return ParseIfStatement(startLocation);
                case TokenType.Return:
                    return ParseReturnStatement(startLocation);
                case TokenType.Switch:
                    return ParseSwitchStatement(startLocation);
                case TokenType.Throw:
                    return ParseThrowStatement(startLocation);
                case TokenType.Try:
                    return ParseTryStatement(startLocation);
                case TokenType.Const:
                case TokenType.Var:
                    var realKind = kind ?? ToVariableKind((string) Value);
                    if (!declaration && realKind != VariableKind.Var)
                    {
                        Raise(startLocation, "Unexpected token");
                    }

                    return ParseVarStatement(startLocation, realKind);
                case TokenType.While:
                    return ParseWhileStatement(startLocation);
                case TokenType.With:
                    return ParseWithStatement(startLocation);
                case TokenType.BraceL:
                    return ParseBlock();
                case TokenType.Semi:
                    return ParseEmptyStatement(startLocation);
                case TokenType.Export:
                case TokenType.Import:
                    if (!Options.AllowImportExportEverywhere)
                    {
                        if (!topLevel)
                            Raise(startLocation, "'import' and 'export' may only appear at the top level");
                        if (!_inModule)
                            Raise(startLocation, "'import' and 'export' may appear only with 'sourceType: module'");
                    }

                    return starttype == TokenType.Import
                        ? ParseImport(startLocation)
                        : ParseExport(startLocation, exports);
            }

            if (IsAsyncFunction() && declaration)
            {
                Next();
                return ParseFunctionStatement(startLocation, true);
            }

            var maybeName = Value;
            var expr = ParseExpression();
            if (starttype == TokenType.Name && expr is AstSymbol identifierNode && Eat(TokenType.Colon))
                return ParseLabelledStatement(startLocation, (string) maybeName, identifierNode);
            return ParseExpressionStatement(startLocation, expr);
        }

        static VariableKind ToVariableKind([NotNull] string s)
        {
            switch (s)
            {
                case "var":
                    return VariableKind.Var;
                case "let":
                    return VariableKind.Let;
                case "const":
                    return VariableKind.Const;
                default:
                    throw new ArgumentException();
            }
        }

        [NotNull]
        AstNode ParseBreakContinueStatement(Position nodeStart, string keyword)
        {
            var isBreak = keyword == "break";
            AstLabelRef label = null;
            Next();
            if (Eat(TokenType.Semi) || InsertSemicolon())
            {
            }
            else if (Type != TokenType.Name)
            {
                Raise(Start, "Unexpected token");
            }
            else
            {
                label = new AstLabelRef(ParseIdent());
                Semicolon();
            }

            // Verify that there is an actual destination to break or
            // continue to.

            if (label == null)
            {
                if (isBreak && !_allowBreak || !isBreak && !_allowContinue)
                    Raise(nodeStart, "Unsyntactic " + keyword);
            }
            else
            {
                var i = 0;
                for (; i < _labels.Count; ++i)
                {
                    var lab = _labels[(uint) i];
                    if (lab.Name == label.Name)
                    {
                        if (lab.IsLoop && (isBreak || lab.IsLoop))
                        {
                            break;
                        }

                        if (isBreak) break;
                    }
                }

                if (i == _labels.Count) Raise(nodeStart, "Unsyntactic " + keyword);
            }

            if (isBreak)
            {
                return new AstBreak(this, nodeStart, _lastTokEnd, label);
            }

            return new AstContinue(this, nodeStart, _lastTokEnd, label);
        }

        [NotNull]
        AstDebugger ParseDebuggerStatement(Position nodeStart)
        {
            Next();
            Semicolon();
            return new AstDebugger(this, nodeStart, _lastTokEnd);
        }

        [NotNull]
        AstDo ParseDoStatement(Position nodeStart)
        {
            Next();
            var backupAllowBreak = _allowBreak;
            var backupAllowContinue = _allowContinue;
            _allowBreak = true;
            _allowContinue = true;
            var body = (AstStatement) ParseStatement(false);
            Expect(TokenType.While);
            _allowBreak = backupAllowBreak;
            _allowContinue = backupAllowContinue;
            var test = ParseParenExpression();
            if (Options.EcmaVersion >= 6)
                Eat(TokenType.Semi);
            else
                Semicolon();

            return new AstDo(this, nodeStart, _lastTokEnd, test, body);
        }

        // Disambiguating between a `for` and a `for`/`in` or `for`/`of`
        // loop is non-trivial. Basically, we have to parse the init `var`
        // statement or expression, disallowing the `in` operator (see
        // the second parameter to `parseExpression`), and then check
        // whether the next token is `in` or `of`. When there is no init
        // part (semicolon immediately after the opening parenthesis), it
        // is a regular `for` loop.
        [NotNull]
        AstNode ParseForStatement(Position nodeStart)
        {
            Next();
            EnterLexicalScope();
            Expect(TokenType.ParenL);
            if (Type == TokenType.Semi) return ParseFor(nodeStart, null);
            var isLet = IsLet();
            if (Type == TokenType.Var || Type == TokenType.Const || isLet)
            {
                var startLoc = Start;
                var kind = isLet ? VariableKind.Let : ToVariableKind((string) Value);
                Next();
                var declarations = new StructList<AstVarDef>();
                ParseVar(ref declarations, true, kind);
                AstDefinitions init;
                if (kind == VariableKind.Let)
                {
                    init = new AstLet(this, startLoc, _lastTokEnd, ref declarations);
                }
                else if (kind == VariableKind.Const)
                {
                    init = new AstConst(this, startLoc, _lastTokEnd, ref declarations);
                }
                else
                {
                    init = new AstVar(this, startLoc, _lastTokEnd, ref declarations);
                }

                if ((Type == TokenType.In || Options.EcmaVersion >= 6 && IsContextual("of")) &&
                    init.Definitions.Count == 1 &&
                    !(kind != VariableKind.Var && init.Definitions[0].Value != null))
                    return ParseForIn(nodeStart, init);
                return ParseFor(nodeStart, init);
            }
            else
            {
                var refDestructuringErrors = new DestructuringErrors();
                var init = ParseExpression(true, refDestructuringErrors);
                if (Type == TokenType.In || Options.EcmaVersion >= 6 && IsContextual("of"))
                {
                    init = ToAssignable(init);
                    CheckLVal(init, false, null);
                    CheckPatternErrors(refDestructuringErrors, true);
                    return ParseForIn(nodeStart, init);
                }

                CheckExpressionErrors(refDestructuringErrors, true);
                return ParseFor(nodeStart, init);
            }
        }

        [NotNull]
        AstNode ParseFunctionStatement(Position nodeStart, bool isAsync)
        {
            Next();
            return ParseFunction(nodeStart, true, false, false, isAsync);
        }

        bool IsFunction()
        {
            return Type == TokenType.Function || IsAsyncFunction();
        }

        [NotNull]
        AstIf ParseIfStatement(Position nodeStart)
        {
            Next();
            var test = ParseParenExpression();
            // allow function declarations in branches, but only in non-strict mode
            var consequent = (AstStatement) ParseStatement(!_strict && IsFunction());
            var alternate = Eat(TokenType.Else) ? (AstStatement) ParseStatement(!_strict && IsFunction()) : null;
            return new AstIf(this, nodeStart, _lastTokEnd, test, consequent, alternate);
        }

        [NotNull]
        AstReturn ParseReturnStatement(Position nodeStart)
        {
            if (!_inFunction && !Options.AllowReturnOutsideFunction)
                Raise(Start, "'return' outside of function");
            Next();

            // In `return` (and `break`/`continue`), the keywords with
            // optional arguments, we eagerly look for a semicolon or the
            // possibility to insert one.

            AstNode argument = null;
            if (!Eat(TokenType.Semi) && !InsertSemicolon())
            {
                argument = ParseExpression();
                Semicolon();
            }

            return new AstReturn(this, nodeStart, _lastTokEnd, argument);
        }

        [NotNull]
        AstSwitch ParseSwitchStatement(Position nodeStart)
        {
            Next();
            var discriminant = ParseParenExpression();
            var cases = new StructList<AstNode>();
            Expect(TokenType.BraceL);
            EnterLexicalScope();

            var startLoc = Start;
            AstSwitchBranch consequent = null;
            var backupAllowBreak = _allowBreak;
            for (var sawDefault = false; Type != TokenType.BraceR;)
            {
                if (Type == TokenType.Case || Type == TokenType.Default)
                {
                    var isCase = Type == TokenType.Case;
                    if (consequent != null)
                    {
                        consequent.End = _lastTokEnd;
                    }

                    startLoc = Start;
                    Next();
                    _allowBreak = true;
                    if (isCase)
                    {
                        var test = ParseExpression();
                        consequent = new AstCase(this, startLoc, startLoc, test);
                    }
                    else
                    {
                        if (sawDefault) RaiseRecoverable(_lastTokStart, "Multiple default clauses");
                        sawDefault = true;
                        consequent = new AstDefault(this, startLoc, startLoc);
                    }

                    cases.Add(consequent);
                    Expect(TokenType.Colon);
                }
                else
                {
                    if (consequent == null)
                    {
                        Raise(Start, "Unexpected token");
                    }

                    consequent.Body.Add(ParseStatement(true));
                }
            }

            ExitLexicalScope();
            if (consequent != null)
            {
                consequent.End = _lastTokEnd;
            }

            Next(); // Closing brace
            _allowBreak = backupAllowBreak;
            return new AstSwitch(this, nodeStart, _lastTokEnd, discriminant, ref cases);
        }

        [NotNull]
        AstThrow ParseThrowStatement(Position nodeStart)
        {
            Next();
            if (LineBreak.IsMatch(_input.Substring(_lastTokEnd.Index, Start.Index - _lastTokEnd.Index)))
                Raise(_lastTokEnd, "Illegal newline after throw");
            var argument = ParseExpression();
            Semicolon();
            return new AstThrow(this, nodeStart, _lastTokEnd, argument);
        }

        [NotNull]
        AstTry ParseTryStatement(Position nodeStart)
        {
            Next();
            var block = ParseBlock();
            AstCatch handler = null;
            if (Type == TokenType.Catch)
            {
                var startLocation = Start;
                Next();
                Expect(TokenType.ParenL);
                var param = ParseBindingAtom();
                EnterLexicalScope();
                CheckLVal(param, true, VariableKind.Let);
                param = new AstSymbolCatch((AstSymbol)param);
                Expect(TokenType.ParenR);
                var body = ParseBlock(false);
                ExitLexicalScope();
                handler = new AstCatch(this, startLocation, _lastTokEnd, param, ref body.Body);
            }

            var startOfFinally = CurPosition();
            var finalizerBody = Eat(TokenType.Finally) ? ParseBlock() : null;
            if (handler == null && finalizerBody == null)
                Raise(nodeStart, "Missing catch or finally clause");
            var finalizer = finalizerBody != null
                ? new AstFinally(this, startOfFinally, finalizerBody.End, ref finalizerBody.Body)
                : null;
            return new AstTry(this, nodeStart, _lastTokEnd, ref block.Body, handler, finalizer);
        }

        [NotNull]
        AstDefinitions ParseVarStatement(Position nodeStart, VariableKind kind)
        {
            Next();
            var declarations = new StructList<AstVarDef>();
            ParseVar(ref declarations, false, kind);
            Semicolon();
            if (kind == VariableKind.Let)
            {
                return new AstLet(this, nodeStart, _lastTokEnd, ref declarations);
            }

            if (kind == VariableKind.Const)
            {
                return new AstConst(this, nodeStart, _lastTokEnd, ref declarations);
            }

            Debug.Assert(kind == VariableKind.Var);
            return new AstVar(this, nodeStart, _lastTokEnd, ref declarations);
        }

        [NotNull]
        AstWhile ParseWhileStatement(Position nodeStart)
        {
            Next();
            var test = ParseParenExpression();
            var backupAllowBreak = _allowBreak;
            var backupAllowContinue = _allowContinue;
            _allowBreak = true;
            _allowContinue = true;
            var body = ParseStatement(false) as AstStatement;
            _allowBreak = backupAllowBreak;
            _allowContinue = backupAllowContinue;
            return new AstWhile(this, nodeStart, _lastTokEnd, test, body);
        }

        [NotNull]
        AstWith ParseWithStatement(Position nodeStart)
        {
            if (_strict) Raise(Start, "'with' in strict mode");
            Next();
            var @object = ParseParenExpression();
            var body = ParseStatement(false) as AstStatement;
            return new AstWith(this, nodeStart, _lastTokEnd, body, @object);
        }

        [NotNull]
        AstEmptyStatement ParseEmptyStatement(Position nodeStart)
        {
            Next();
            return new AstEmptyStatement(this, nodeStart, _lastTokEnd);
        }

        [NotNull]
        AstNode ParseLabelledStatement(Position nodeStart, string maybeName, AstSymbol expr)
        {
            foreach (var label in _labels)
            {
                if (label.Name == maybeName)
                    Raise(expr.Start, "Label '" + maybeName + "' is already declared");
            }

            var newlabel = new AstLabel(this, nodeStart, _lastTokEnd, maybeName);
            newlabel.IsLoop = TokenInformation.Types[Type].IsLoop;
            _labels.Add(newlabel);
            var body = ParseStatement(true);
            if (body is AstClass ||
                body is AstLet || body is AstConst ||
                body is AstFunction functionDeclaration && (_strict || functionDeclaration.IsGenerator))
                RaiseRecoverable(body.Start, "Invalid labelled declaration");
            _labels.Pop();

            return new AstLabeledStatement(this, nodeStart, _lastTokEnd, (AstStatement) body, newlabel);
        }

        [NotNull]
        AstSimpleStatement ParseExpressionStatement(Position nodeStart, AstNode expr)
        {
            if (_canBeDirective && expr is AstString directive && directive.Value == "use strict")
            {
                _strict = true;
            }
            Semicolon();
            return new AstSimpleStatement(this, nodeStart, _lastTokEnd, expr);
        }

        // Parse a semicolon-enclosed block of statements, handling `"use
        // strict"` declarations when `allowStrict` is true (used for
        // function bodies).
        [NotNull]
        AstBlock ParseBlock(bool createNewLexicalScope = true)
        {
            var startLocation = Start;
            var body = new StructList<AstNode>();
            Expect(TokenType.BraceL);
            if (createNewLexicalScope)
            {
                EnterLexicalScope();
            }

            while (!Eat(TokenType.BraceR))
            {
                var stmt = ParseStatement(true);
                body.Add(stmt);
            }

            if (createNewLexicalScope)
            {
                ExitLexicalScope();
            }

            return new AstBlock(this, startLocation, _lastTokEnd, ref body);
        }

        // Parse a regular `for` loop. The disambiguation code in
        // `parseStatement` will already have parsed the init statement or
        // expression.
        [NotNull]
        AstStatement ParseFor(Position nodeStart, AstNode init)
        {
            Expect(TokenType.Semi);
            var test = Type == TokenType.Semi ? null : ParseExpression();
            Expect(TokenType.Semi);
            var update = Type == TokenType.ParenR ? null : ParseExpression();
            Expect(TokenType.ParenR);
            ExitLexicalScope();
            var backupAllowBreak = _allowBreak;
            var backupAllowContinue = _allowContinue;
            _allowBreak = true;
            _allowContinue = true;
            var body = ParseStatement(false) as AstStatement;
            _allowBreak = backupAllowBreak;
            _allowContinue = backupAllowContinue;
            return new AstFor(this, nodeStart, _lastTokEnd, body, init, test, update);
        }

        // Parse a `for`/`in` and `for`/`of` loop, which are almost
        // same from parser's perspective.
        [NotNull]
        AstNode ParseForIn(Position nodeStart, AstNode init)
        {
            var isIn = Type == TokenType.In;
            Next();
            var right = ParseExpression();
            Expect(TokenType.ParenR);
            ExitLexicalScope();
            var backupAllowBreak = _allowBreak;
            var backupAllowContinue = _allowContinue;
            _allowBreak = true;
            _allowContinue = true;
            var body = ParseStatement(false) as AstStatement;
            _allowBreak = backupAllowBreak;
            _allowContinue = backupAllowContinue;
            if (isIn)
            {
                return new AstForIn(this, nodeStart, _lastTokEnd, body, init, right);
            }

            return new AstForOf(this, nodeStart, _lastTokEnd, body, init, right);
        }

        // Parse a list of variable declarations.
        void ParseVar(ref StructList<AstVarDef> declarations, bool isFor, VariableKind kind)
        {
            for (;;)
            {
                var startLocation = Start;
                var id = ParseVarId(kind);
                AstNode init = null;
                if (Eat(TokenType.Eq))
                {
                    init = ParseMaybeAssign(isFor);
                }
                else if (kind == VariableKind.Const &&
                         !(Type == TokenType.In || Options.EcmaVersion >= 6 && IsContextual("of")))
                {
                    Raise(Start, "Unexpected token");
                }
                else if (!(id is AstSymbol) && !(isFor && (Type == TokenType.In || IsContextual("of"))))
                {
                    Raise(_lastTokEnd, "Complex binding patterns require an initialization value");
                }

                var decl = new AstVarDef(this, startLocation, _lastTokEnd, id, init);
                declarations.Add(decl);
                if (!Eat(TokenType.Comma)) break;
            }
        }

        [NotNull]
        AstNode ParseVarId(VariableKind kind)
        {
            var id = ParseBindingAtom();
            CheckLVal(id, true, kind);
            if (id is AstSymbol)
            {
                if (kind == VariableKind.Let)
                {
                    id = new AstSymbolLet((AstSymbol) id);
                }
                else if (kind == VariableKind.Const)
                {
                    id = new AstSymbolConst((AstSymbol) id);
                }
                else
                {
                    id = new AstSymbolVar((AstSymbol) id);
                }
            }

            return id;
        }

        // Parse a function declaration or literal (depending on the
        // `isStatement` parameter).
        [NotNull]
        AstNode ParseFunction(Position startLoc, bool isStatement, bool isNullableId, bool allowExpressionBody = false,
            bool isAsync = false)
        {
            var generator = false;
            if (Options.EcmaVersion >= 6 && !isAsync)
                generator = Eat(TokenType.Star);
            if (Options.EcmaVersion < 8 && isAsync)
                throw new InvalidOperationException();

            AstSymbol id = null;
            if (isStatement || isNullableId)
            {
                id = isNullableId && Type != TokenType.Name ? null : ParseIdent();
                if (id != null)
                {
                    CheckLVal(id, true, VariableKind.Var);
                }
            }

            var oldInGen = _inGenerator;
            var oldInAsync = _inAsync;
            var oldYieldPos = _yieldPos;
            var oldAwaitPos = _awaitPos;
            var oldInFunc = _inFunction;
            _inGenerator = generator;
            _inAsync = isAsync;
            _yieldPos = default;
            _awaitPos = default;
            _inFunction = true;
            EnterFunctionScope();

            if (isStatement == false && isNullableId == false)
                id = Type == TokenType.Name ? ParseIdent() : null;

            var parameters = new StructList<AstNode>();
            ParseFunctionParams(ref parameters);
            MakeSymbolFunArg(ref parameters);
            var body = new StructList<AstNode>();
            var useStrict = false;
            var expression = ParseFunctionBody(parameters, startLoc, id, allowExpressionBody, ref body, ref useStrict);

            _inGenerator = oldInGen;
            _inAsync = oldInAsync;
            _yieldPos = oldYieldPos;
            _awaitPos = oldAwaitPos;
            _inFunction = oldInFunc;

            if (isStatement || isNullableId)
            {
                if (id != null)
                    id = new AstSymbolDefun(id);
                return new AstDefun(this, startLoc, _lastTokEnd, (AstSymbolDefun) id, ref parameters, generator,
                    isAsync, ref body).SetUseStrict(useStrict);
            }

            if (id != null)
                id = new AstSymbolLambda(id);
            return new AstFunction(this, startLoc, _lastTokEnd, (AstSymbolLambda) id, ref parameters, generator,
                isAsync, ref body).SetUseStrict(useStrict);
        }

        void ParseFunctionParams(ref StructList<AstNode> parameters)
        {
            Expect(TokenType.ParenL);
            ParseBindingList(ref parameters, TokenType.ParenR, false, Options.EcmaVersion >= 8);
            CheckYieldAwaitInDefaultParams();
        }

        // Parse a class declaration or literal (depending on the
        // `isStatement` parameter).
        [NotNull]
        AstNode ParseClass(Position nodeStart, bool isStatement, bool isNullableId)
        {
            Next();

            var id = ParseClassId(isStatement);
            var superClass = ParseClassSuper();
            var hadConstructor = false;
            var body = new StructList<AstObjectProperty>();
            Expect(TokenType.BraceL);
            while (!Eat(TokenType.BraceR))
            {
                if (Eat(TokenType.Semi)) continue;
                var methodStart = Start;
                var isGenerator = Eat(TokenType.Star);
                var isAsync = false;
                var isMaybeStatic = Type == TokenType.Name && (string) Value == "static";
                var (computed, key) = ParsePropertyName();
                var @static = isMaybeStatic && Type != TokenType.ParenL;
                if (@static)
                {
                    if (isGenerator)
                    {
                        Raise(Start, "Unexpected token");
                    }

                    isGenerator = Eat(TokenType.Star);
                    (computed, key) = ParsePropertyName();
                }

                if (Options.EcmaVersion >= 8 && !isGenerator && !computed &&
                    key is AstSymbol identifierNode && identifierNode.Name == "async" &&
                    Type != TokenType.ParenL &&
                    !CanInsertSemicolon())
                {
                    isAsync = true;
                    (computed, key) = ParsePropertyName();
                }

                var kind = PropertyKind.Method;
                var isGetSet = false;
                if (!computed)
                {
                    if (!isGenerator && !isAsync && key is AstSymbol identifierNode2 && Type != TokenType.ParenL &&
                        (identifierNode2.Name == "get" || identifierNode2.Name == "set"))
                    {
                        isGetSet = true;
                        kind = identifierNode2.Name == "get" ? PropertyKind.Get : PropertyKind.Set;
                        (computed, key) = ParsePropertyName();
                    }

                    if (!@static && !computed &&
                        (key is AstSymbol identifierNode3 && identifierNode3.Name == "constructor" ||
                         key is AstString literal && literal.Value == "constructor"))
                    {
                        if (hadConstructor) Raise(key.Start, "Duplicate constructor in the same class");
                        if (isGetSet) Raise(key.Start, "Constructor can't have get/set modifier");
                        if (isGenerator) Raise(key.Start, "Constructor can't be a generator");
                        if (isAsync) Raise(key.Start, "Constructor can't be an async method");
                        kind = PropertyKind.Constructor;
                        hadConstructor = true;
                    }
                    else if (@static && key is AstSymbol keyIdentifier && keyIdentifier.Name == "prototype")
                    {
                        Raise(key.Start, "Classes may not have a static property named prototype");
                    }
                }

                var methodValue = ParseMethod(isGenerator, isAsync);

                if (isGetSet)
                {
                    var paramCount = kind == PropertyKind.Get ? 0 : 1;
                    if (methodValue.ArgNames.Count != paramCount)
                    {
                        var startLocation = methodValue.Start;
                        if (kind == PropertyKind.Get)
                            RaiseRecoverable(startLocation, "getter should have no params");
                        else
                            RaiseRecoverable(startLocation, "setter should have exactly one param");
                    }
                    else
                    {
                        if (kind == PropertyKind.Set && methodValue.ArgNames[0] is AstExpansion)
                            RaiseRecoverable(methodValue.ArgNames[0].Start, "Setter cannot use rest params");
                    }
                }

                if (kind == PropertyKind.Get)
                {
                    body.Add(new AstObjectGetter(this, methodStart, _lastTokEnd, key, methodValue, @static));
                }
                else if (kind == PropertyKind.Set)
                {
                    body.Add(new AstObjectSetter(this, methodStart, _lastTokEnd, key, methodValue, @static));
                }
                else if (kind == PropertyKind.Method || kind == PropertyKind.Constructor)
                {
                    body.Add(new AstConciseMethod(this, methodStart, _lastTokEnd, key, methodValue, @static,
                        isGenerator, isAsync));
                }
                else
                {
                    throw new InvalidOperationException("parseClass unknown kind " + kind);
                }
            }

            if (isStatement || isNullableId)
            {
                return new AstClass(this, nodeStart, _lastTokEnd, id, superClass, ref body);
            }

            return new AstClassExpression(this, nodeStart, _lastTokEnd, id, superClass, ref body);
        }

        [CanBeNull]
        AstSymbolDeclaration ParseClassId(bool isStatement)
        {
            if (Type == TokenType.Name)
                return new AstSymbolDeclaration(ParseIdent());
            if (isStatement)
            {
                Raise(Start, "Unexpected token");
            }

            return null;
        }

        [CanBeNull]
        AstNode ParseClassSuper()
        {
            return Eat(TokenType.Extends) ? ParseExpressionSubscripts() : null;
        }

        // Parses module export declaration.
        [NotNull]
        AstNode ParseExport(Position nodeStart, IDictionary<string, bool> exports)
        {
            Next();
            // export * from '...'
            if (Eat(TokenType.Star))
            {
                var specifiers = new StructList<AstNameMapping>();
                specifiers.Add(new AstNameMapping(this, _lastTokStart, _lastTokEnd,
                    new AstSymbolExportForeign(this, _lastTokStart, _lastTokEnd, "*"),
                    new AstSymbolExport(this, _lastTokStart, _lastTokEnd, "*")));
                ExpectContextual("from");
                if (Type != TokenType.String)
                    Raise(Start, "Unexpected token");
                var source = ParseExpressionAtom() as AstString;
                Semicolon();
                return new AstExport(this, nodeStart, _lastTokEnd, source, null, ref specifiers);
            }

            if (Eat(TokenType.Default))
            {
                // export default ...
                CheckExport(exports, "default", _lastTokStart);
                var isAsync = false;
                AstNode declaration;
                if (Type == TokenType.Function || (isAsync = IsAsyncFunction()))
                {
                    var startLoc = Start;
                    Next();
                    if (isAsync) Next();
                    declaration = ParseFunction(startLoc, false, true, false, isAsync);
                }
                else if (Type == TokenType.Class)
                {
                    declaration = ParseClass(Start, false, true);
                }
                else
                {
                    declaration = ParseMaybeAssign();
                    Semicolon();
                }

                return new AstExport(this, nodeStart, _lastTokEnd, declaration, true);
            }
            else
            {
                // export var|const|let|function|class ...
                AstNode declaration;
                var specifiers = new StructList<AstNameMapping>();
                AstString source = null;
                if (ShouldParseExportStatement())
                {
                    declaration = ParseStatement(true);
                    if (declaration is AstDefinitions variableDeclaration)
                    {
                        CheckVariableExport(exports, in variableDeclaration.Definitions);
                    }
                    else
                    {
                        var declarationNode = (AstSymbolDeclaration) declaration;
                        CheckExport(exports, declarationNode.Name, declarationNode.Start);
                    }
                }
                else
                {
                    // export { x, y as z } [from '...']
                    declaration = null;
                    ParseExportSpecifiers(ref specifiers, exports);
                    if (EatContextual("from"))
                    {
                        if (Type != TokenType.String)
                            Raise(Start, "Unexpected token");
                        source = ParseExpressionAtom() as AstString;
                    }
                    else
                    {
                        // check for keywords used as local names
                        foreach (var spec in specifiers)
                        {
                            CheckUnreserved(spec.Name.Start, spec.Name.End, spec.Name.Name);
                        }
                    }

                    Semicolon();
                }

                return new AstExport(this, nodeStart, _lastTokEnd, source, declaration, ref specifiers);
            }
        }

        static void CheckExport([CanBeNull] IDictionary<string, bool> exports, string name, Position pos)
        {
            if (exports == null) return;
            if (exports.ContainsKey(name))
                RaiseRecoverable(pos, "Duplicate export '" + name + "'");
            exports[name] = true;
        }

        static void CheckPatternExport(IDictionary<string, bool> exports, AstNode pattern)
        {
            switch (pattern)
            {
                case AstSymbol identifierNode:
                    CheckExport(exports, identifierNode.Name, pattern.Start);
                    break;
                case AstDestructuring patternDest:
                    foreach (var prop in patternDest.Names)
                    {
                        CheckPatternExport(exports, prop);
                    }

                    break;
                case AstDefaultAssign assignmentPattern:
                    CheckPatternExport(exports, assignmentPattern.Left);
                    break;
                default:
                    throw new InvalidOperationException("checkPattenExport unhandled " + pattern);
            }
        }

        static void CheckVariableExport([CanBeNull] IDictionary<string, bool> exports, in StructList<AstVarDef> decls)
        {
            if (exports == null)
                return;
            foreach (var decl in decls)
            {
                CheckPatternExport(exports, decl.Name);
            }
        }

        bool ShouldParseExportStatement()
        {
            return TokenInformation.Types[Type].Keyword == "var" ||
                   TokenInformation.Types[Type].Keyword == "const" ||
                   TokenInformation.Types[Type].Keyword == "class" ||
                   TokenInformation.Types[Type].Keyword == "function" ||
                   IsLet() ||
                   IsAsyncFunction();
        }

        // Parses a comma-separated list of module exports.
        void ParseExportSpecifiers(ref StructList<AstNameMapping> nodes, IDictionary<string, bool> exports)
        {
            var first = true;
            // export { x, y as z } [from '...']
            Expect(TokenType.BraceL);
            while (!Eat(TokenType.BraceR))
            {
                if (!first)
                {
                    Expect(TokenType.Comma);
                    if (AfterTrailingComma(TokenType.BraceR)) break;
                }
                else first = false;

                var startLoc = Start;
                var local = ParseIdent(true);
                var exported = EatContextual("as") ? ParseIdent(true) : local;
                CheckExport(exports, exported.Name, exported.Start);
                nodes.Add(new AstNameMapping(this, startLoc, _lastTokEnd, new AstSymbolExportForeign(local),
                    new AstSymbolExport(exported)));
            }
        }

        // Parses import declaration.
        [NotNull]
        AstImport ParseImport(Position nodeStart)
        {
            Next();
            // import '...'
            var importNames = new StructList<AstNameMapping>();
            AstSymbolImport importName = null;
            AstString source = null;
            if (Type == TokenType.String)
            {
                source = ParseExpressionAtom() as AstString;
            }
            else
            {
                ParseImportSpecifiers(ref importNames, ref importName);
                ExpectContextual("from");
                if (Type == TokenType.String)
                {
                    source = ParseExpressionAtom() as AstString;
                }
                else
                {
                    Raise(Start, "Unexpected token");
                }
            }

            Semicolon();
            return new AstImport(this, nodeStart, _lastTokEnd, source, importName, ref importNames);
        }

        // Parses a comma-separated list of module imports.
        void ParseImportSpecifiers(ref StructList<AstNameMapping> importNames, ref AstSymbolImport importName)
        {
            var first = true;
            if (Type == TokenType.Name)
            {
                // import defaultObj, { x, y as z } from '...'
                var local = ParseIdent();
                CheckLVal(local, true, VariableKind.Let);
                importName = new AstSymbolImport(local);
                if (!Eat(TokenType.Comma))
                    return;
            }

            if (Type == TokenType.Star)
            {
                var startLoc = Start;
                var starSymbol = new AstSymbolImportForeign(this, Start, End, null);
                Next();
                ExpectContextual("as");
                var local = ParseIdent();
                CheckLVal(local, true, VariableKind.Let);
                importNames.Add(new AstNameMapping(this, startLoc, _lastTokEnd, starSymbol,
                    new AstSymbolImport(local)));
                return;
            }

            Expect(TokenType.BraceL);
            while (!Eat(TokenType.BraceR))
            {
                if (!first)
                {
                    Expect(TokenType.Comma);
                    if (AfterTrailingComma(TokenType.BraceR)) break;
                }
                else first = false;

                var startLoc = Start;
                var imported = ParseIdent(true);
                AstSymbol local;
                if (EatContextual("as"))
                {
                    local = ParseIdent();
                }
                else
                {
                    CheckUnreserved(imported.Start, imported.End, imported.Name);
                    local = imported;
                }

                CheckLVal(local, true, VariableKind.Let);
                importNames.Add(new AstNameMapping(this, startLoc, _lastTokEnd, new AstSymbolImportForeign(local),
                    new AstSymbolImport(imported)));
            }
        }

        bool IsUseStrictDirective([NotNull] AstNode statement)
        {
            var literal2 = (AstString) ((AstSimpleStatement)statement).Body;
            return literal2.Value == "use strict";
        }

        bool IsDirectiveCandidate([NotNull] AstNode statement)
        {
            return statement is AstSimpleStatement expressionStatementNode &&
                   expressionStatementNode.Body is AstString &&
                   // Reject parenthesized strings.
                   (_input[statement.Start.Index] == '\"' || _input[statement.Start.Index] == '\'');
        }
    }
}
