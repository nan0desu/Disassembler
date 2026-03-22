using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Disassembler
{
	public class ILUnaryAssignmentOperator : ILExpression
	{
		private ILExpression variable;
		private ILUnaryOperatorEnum op;

		public ILUnaryAssignmentOperator(ILExpression variable, ILUnaryOperatorEnum op)
		{
			this.variable = variable;
			this.op = op;
		}

		public ILExpression Variable
		{
			get => this.variable;
		}

		public ILUnaryOperatorEnum Operator
		{
			get => this.op;
		}

		public static string UnaryOperatorToString(ILUnaryOperatorEnum op)
		{
			switch (op)
			{
				case ILUnaryOperatorEnum.IncrementBefore:
				case ILUnaryOperatorEnum.IncrementAfter:
					return "++";

				case ILUnaryOperatorEnum.DecrementBefore:
				case ILUnaryOperatorEnum.DecrementAfter:
					return "--";

				case ILUnaryOperatorEnum.Negate:
					return "-";

				case ILUnaryOperatorEnum.BitwiseNot:
					return "~";

				default:
					return "UndefinedOperator";
			}
		}

		public override string ToCSString()
		{
			switch (this.op)
			{
				case ILUnaryOperatorEnum.IncrementBefore:
				case ILUnaryOperatorEnum.DecrementBefore:
					return $"{UnaryOperatorToString(this.op)}{this.variable.ToCSString()}";

				case ILUnaryOperatorEnum.IncrementAfter:
				case ILUnaryOperatorEnum.DecrementAfter:
					return $"{this.variable.ToCSString()}{UnaryOperatorToString(this.op)}";

				case ILUnaryOperatorEnum.Negate:
				case ILUnaryOperatorEnum.BitwiseNot:
					return $"{UnaryOperatorToString(this.op)}{this.variable.ToCSString()}";

				default:
					return "UndefinedOperator";
		}
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
