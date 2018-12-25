﻿using Njsast.Output;
using Njsast.Reader;

namespace Njsast.Ast
{
    /// A sequence expression (comma-separated expressions)
    public class AstSequence : AstNode
    {
        /// [AstNode*] array of expressions (at least two)
        public StructList<AstNode> Expressions;

        public AstSequence(Parser parser, Position startLoc, Position endLoc, ref StructList<AstNode> expressions) :
            base(parser, startLoc, endLoc)
        {
            Expressions.TransferFrom(ref expressions);
        }

        public override void Visit(TreeWalker w)
        {
            base.Visit(w);
            w.WalkList(Expressions);
        }

        public override void CodeGen(OutputContext output)
        {
            for (var i = 0u; i < Expressions.Count; i++)
            {
                if (i > 0)
                {
                    output.Comma();
                    if (output.ShouldBreak())
                    {
                        output.Newline();
                        output.Indent();
                    }
                }

                Expressions[i].Print(output);
            }
        }

        public override bool NeedParens(OutputContext output)
        {
            var p = output.Parent();
            return p is AstCall // (foo, bar)() or foo(1, (2, 3), 4)
                   || p is AstUnary // !(foo, bar, baz)
                   || p is AstBinary // 1 + (2, 3) + 4 ==> 8
                   || p is AstVarDef // var a = (1, 2), b = a + a; ==> b == 4
                   || p is AstPropAccess // (1, {foo:2}).foo or (1, {foo:2})["foo"] ==> 2
                   || p is AstArray // [ 1, (2, 3), 4 ] ==> [ 1, 3, 4 ]
                   || p is AstObjectProperty // { foo: (1, 2) }.foo ==> 2
                   // (false, true) ? (a = 10, b = 20) : (c = 30) ==> 20 (side effect, set a := 10 and b := 20)
                   || p is AstConditional
                   || p is AstArrow // x => (x, x)
                   || p is AstDefaultAssign // x => (x = (0, function(){}))
                   || p is AstExpansion // [...(a, b)]
                   || p is AstForOf forOf && this == forOf.Object // for (e of (foo, bar)) {}
                   || p is AstYield // yield (foo, bar)
                   || p is AstExport // export default (foo, bar)
                ;
        }
    }
}
