namespace Disassembler
{
	/// <summary>
	/// Represents a type cast: (type)expr
	/// Used for sign/zero extension (MOVSX/MOVZX) and other type conversions.
	/// </summary>
	public class ILTypeCast : ILExpression
	{
		private ILExpression operand;
		private ILValueType targetType;
		private bool isSigned;

		public ILTypeCast(ILExpression operand, ILValueType targetType, bool isSigned)
		{
			this.operand = operand;
			this.targetType = targetType;
			this.isSigned = isSigned;
		}

		public ILExpression Operand
		{
			get => this.operand;
		}

		public ILValueType TargetType
		{
			get => this.targetType;
		}

		public bool IsSigned
		{
			get => this.isSigned;
		}

		public override string ToCSString()
		{
			return $"({this.targetType.CSDeclaration}){this.operand.ToCSString()}";
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
