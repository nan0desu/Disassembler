namespace Disassembler
{
	/// <summary>
	/// Represents pointer dereference: *expr
	/// In 16-bit DOS context, this is reading from a segment:offset address.
	/// </summary>
	public class ILPointerDereference : ILExpression
	{
		private ILExpression pointerExpr;
		private ILValueType pointedType;

		public ILPointerDereference(ILExpression pointerExpr, ILValueType pointedType)
		{
			this.pointerExpr = pointerExpr;
			this.pointedType = pointedType;
		}

		public ILExpression PointerExpression
		{
			get => this.pointerExpr;
		}

		public ILValueType PointedType
		{
			get => this.pointedType;
		}

		public override string ToCSString()
		{
			return $"*({this.pointerExpr.ToCSString()})";
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
