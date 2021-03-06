﻿using Njsast.Output;
using Njsast.Reader;

namespace Njsast.Ast
{
    /// A `let` statement
    public class AstLet : AstDefinitions
    {
        public AstLet(Parser parser, Position startPos, Position endPos, ref StructList<AstVarDef> definitions) : base(parser, startPos, endPos, ref definitions)
        {
        }

        public override void CodeGen(OutputContext output)
        {
            DoPrint(output, "let");
        }
    }
}
