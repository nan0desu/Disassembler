namespace Disassembler
{
	public class ILConditionExpression : ILExpression
	{
		private ILExpression leftOperand;
		private ILConditionEnum condition;
		private ILExpression rightOperand;

		public ILConditionExpression(ILExpression left, ILConditionEnum condition, ILExpression right)
		{
			this.leftOperand = left;
			this.condition = condition;
			this.rightOperand = right;
		}

		public ILExpression LeftOperand
		{
			get => this.leftOperand;
		}

		public ILConditionEnum Condition
		{
			get => this.condition;
		}

		public ILExpression RightOperand
		{
			get => this.rightOperand;
		}

		public static string ConditionToString(ILConditionEnum condition)
		{
			switch (condition)
			{
				case ILConditionEnum.Equal:
					return "==";
				case ILConditionEnum.NotEqual:
					return "!=";
				case ILConditionEnum.LessThan:
					return "<";
				case ILConditionEnum.LessThanOrEqual:
					return "<=";
				case ILConditionEnum.GreaterThan:
					return ">";
				case ILConditionEnum.GreaterThanOrEqual:
					return ">=";
				case ILConditionEnum.UnsignedLessThan:
					return "<";
				case ILConditionEnum.UnsignedLessThanOrEqual:
					return "<=";
				case ILConditionEnum.UnsignedGreaterThan:
					return ">";
				case ILConditionEnum.UnsignedGreaterThanOrEqual:
					return ">=";
				case ILConditionEnum.Sign:
					return "< 0";
				case ILConditionEnum.NotSign:
					return ">= 0";
				default:
					return "UndefinedCondition";
			}
		}

		public override string ToCSString()
		{
			switch (this.condition)
			{
				case ILConditionEnum.Sign:
					return $"{this.leftOperand.ToCSString()} < 0";
				case ILConditionEnum.NotSign:
					return $"{this.leftOperand.ToCSString()} >= 0";
				default:
					return $"{this.leftOperand.ToCSString()} {ConditionToString(this.condition)} {this.rightOperand.ToCSString()}";
			}
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
