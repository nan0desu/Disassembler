namespace Disassembler
{
	/// <summary>
	/// Represents array element access: base[index]
	/// In DOS 16-bit context, this is typically a pointer + offset calculation.
	/// </summary>
	public class ILArrayAccess : ILExpression
	{
		private ILExpression baseExpr;
		private ILExpression indexExpr;
		private ILValueType elementType;

		public ILArrayAccess(ILExpression baseExpr, ILExpression indexExpr, ILValueType elementType)
		{
			this.baseExpr = baseExpr;
			this.indexExpr = indexExpr;
			this.elementType = elementType;
		}

		public ILExpression BaseExpression
		{
			get => this.baseExpr;
		}

		public ILExpression IndexExpression
		{
			get => this.indexExpr;
		}

		public ILValueType ElementType
		{
			get => this.elementType;
		}

		public override string ToCSString()
		{
			return $"{this.baseExpr.ToCSString()}[{this.indexExpr.ToCSString()}]";
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
