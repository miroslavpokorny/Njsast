using System;
using System.Collections.Generic;
using Njsast.Ast;
using Njsast.Runtime;
using Njsast.Utils;

namespace Njsast.Compress
{
    public class UnreachableCodeEliminationTreeWalker : TreeWalker
    {
        AstBlock? _lastBlock;
        List<ControlFlow>? _controlFlows;
        protected override void Visit(AstNode node)
        {
            if (node is AstBlock astBlock)
            {
                var safeLastBlock = _lastBlock;
                _lastBlock = astBlock;
                Descend();
                _lastBlock = safeLastBlock;
                StopDescending();
            }

            if (node is AstStatementWithBody astStatementWithBody)
            {
                if (_lastBlock == null)
                {
                    throw new SemanticError("Statement with body must be placed within block", astStatementWithBody);
                }
                _controlFlows.Add(new ControlFlow(astStatementWithBody, _lastBlock));
            }
        }

        public IEnumerable<ControlFlow> FindOutControlFlows(AstToplevel astToplevel)
        {
            _controlFlows = new List<ControlFlow>();
            Walk(astToplevel);
            return _controlFlows;
        }

        public static void RemoveUnreachableCode(IEnumerable<ControlFlow> controlFlows)
        {
            foreach (var controlFlow in controlFlows)
            {
                switch (controlFlow.Statement)
                {
                    case AstIf ifStatement:
                        RemoveUnreachableCode(controlFlow.Parent, ifStatement);
                        break;
                    case AstWhile whileStatement:
                        RemoveUnreachableCode(controlFlow.Parent, whileStatement);
                        break;
                    case AstDo doStatement:
                        RemoveUnreachableCode(controlFlow.Parent, doStatement);
                        break;
                    default:
                        // TODO implement other controlFlows 
                        throw new NotImplementedException();
                }
            }
        }

        static void RemoveUnreachableCode(AstBlock parent, AstIf ifStatement)
        {
            if (!ifStatement.Condition.IsConstValue())
                return;
            
            var statement = TypeConverter.ToBoolean(ifStatement.Condition.ConstValue())
                ? ifStatement.Body
                : ifStatement.Alternative;

            switch (statement)
            {
                case null:
                    parent.Body.RemoveItem(ifStatement);
                    break;
                case AstBlock blockStatement:
                    parent.Body.ReplaceItem(ifStatement, blockStatement.Body);
                    break;
                default:
                    parent.Body.ReplaceItem(ifStatement, statement);
                    break;
            }
        }

        static void RemoveUnreachableCode(AstBlock parent, AstWhile whileStatement)
        {
            if (!whileStatement.Condition.IsConstValue() || TypeConverter.ToBoolean(whileStatement.Condition.ConstValue()))
                return;
            
            parent.Body.RemoveItem(whileStatement);
        }

        static void RemoveUnreachableCode(AstBlock parent, AstDo doStatement)
        {
            if (!doStatement.Condition.IsConstValue() || TypeConverter.ToBoolean(doStatement.Condition.ConstValue()))
                return;
            
            // TODO detect if doStatement contains break (we can not inline code if break is present in code block)
            switch (doStatement.Body)
            {
                case null: // Body should not be null at all
                case AstEmptyStatement emptyStatement:
                    parent.Body.RemoveItem(doStatement);
                    break;
                case AstBlock blockStatement:
                    parent.Body.ReplaceItem(doStatement, blockStatement.Body);
                    break;
                default:
                    parent.Body.ReplaceItem(doStatement, doStatement.Body);
                    break;
            }
        }
    }
}