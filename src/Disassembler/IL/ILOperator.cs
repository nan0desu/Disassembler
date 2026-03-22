using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Disassembler
{
	public class ILOperator : ILExpression
	{
		private ILExpression leftOperand;
		private ILOperatorEnum op;
		private ILExpression rightOperand;

		public ILOperator(ILExpression left, ILOperatorEnum op, ILExpression right)
		{
			this.leftOperand = left;
			this.op = op;
			this.rightOperand = right;
		}

		public ILExpression LeftOperand
		{
			get => this.leftOperand;
		}

		public ILOperatorEnum Operator
		{
			get => this.op;
		}

		public ILExpression RightOperand
		{
			get => this.rightOperand;
		}

		public static string OperatorToString(ILOperatorEnum op)
		{
			switch (op)
			{
				case ILOperatorEnum.Add:
					return "+";

				case ILOperatorEnum.Substract:
					return "-";

				case ILOperatorEnum.Multiply:
					return "*";

				case ILOperatorEnum.Divide:
					return "/";

				case ILOperatorEnum.Modulo:
					return "%";

				case ILOperatorEnum.BitwiseAnd:
					return "&";

				case ILOperatorEnum.BitwiseOr:
					return "|";

				case ILOperatorEnum.BitwiseXor:
					return "^";

				case ILOperatorEnum.ShiftLeft:
					return "<<";

				case ILOperatorEnum.ShiftRight:
					return ">>";

				case ILOperatorEnum.ArithmeticShiftRight:
					return ">>";

				default:
					return "UndefinedOperator";
			}
		}

		public override string ToCSString()
		{
			return $"{this.leftOperand.ToCSString()} {OperatorToString(this.op)} {this.rightOperand.ToCSString()}";
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
