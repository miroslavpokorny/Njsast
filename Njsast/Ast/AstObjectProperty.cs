﻿using Njsast.Output;
using Njsast.Reader;

namespace Njsast.Ast
{
    /// Base class for literal object properties
    public abstract class AstObjectProperty : AstNode
    {
        /// [AstNode] property name.
        public AstNode Key;

        /// [AstNode] property value. For getters and setters this is an AstAccessor.
        public AstNode Value;

        protected AstObjectProperty(Parser parser, Position startLoc, Position endLoc, AstNode key, AstNode value) : base(
            parser, startLoc, endLoc)
        {
            Key = key;
            Value = value;
        }

        protected AstObjectProperty(AstNode key, AstNode value)
        {
            Key = key;
            Value = value;
        }

        public override void Visit(TreeWalker w)
        {
            base.Visit(w);
            w.Walk(Key);
            w.Walk(Value);
        }

        public override void Transform(TreeTransformer tt)
        {
            base.Transform(tt);
            Key = tt.Transform(Key);
            Value = tt.Transform(Value);
        }

        protected void PrintGetterSetter(OutputContext output, string? type, bool @static)
        {
            if (@static)
            {
                output.Print("static");
                output.Space();
            }

            if (type != null)
            {
                output.Print(type);
                output.Space();
            }

            if (Key is AstSymbolMethod symbolMethod)
            {
                output.PrintPropertyName(symbolMethod.Name);
            }
            else
            {
                output.Print("[");
                Key.Print(output);
                output.Print("]");
            }

            ((AstLambda) Value).DoPrint(output, true);
        }
    }
}
