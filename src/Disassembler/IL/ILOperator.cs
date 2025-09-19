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

				case ILOperatorEnum.Subtract:
					return "-";

				case ILOperatorEnum.Multiply:
					return "*";

				case ILOperatorEnum.Divide:
					return "/";

				default:
					return "UndefinedOperator";
			}
		}

		public override string ToCSString()
		{
			StringBuilder sb = new StringBuilder();

			if (leftOperand is ILOperator)
			{
				sb.Append($"({this.leftOperand.ToCSString()})");
			}
			else
			{
				sb.Append($"{this.leftOperand.ToCSString()}");
			}

			sb.Append($"{OperatorToString(this.op)}");

			if (rightOperand is ILOperator)
			{
				sb.Append($"({this.rightOperand.ToCSString()})");
			}
			else
			{
				sb.Append($"{this.rightOperand.ToCSString()}");
			}

			return sb.ToString();
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
