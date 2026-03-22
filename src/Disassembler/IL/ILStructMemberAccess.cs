namespace Disassembler
{
	/// <summary>
	/// Represents struct/union member access: expr.member or expr->member
	/// </summary>
	public class ILStructMemberAccess : ILExpression
	{
		private ILExpression structExpr;
		private string memberName;
		private int memberOffset;
		private ILValueType memberType;
		private bool isPointerAccess;

		public ILStructMemberAccess(ILExpression structExpr, string memberName, int memberOffset, ILValueType memberType, bool isPointerAccess)
		{
			this.structExpr = structExpr;
			this.memberName = memberName;
			this.memberOffset = memberOffset;
			this.memberType = memberType;
			this.isPointerAccess = isPointerAccess;
		}

		public ILExpression StructExpression
		{
			get => this.structExpr;
		}

		public string MemberName
		{
			get => this.memberName;
		}

		public int MemberOffset
		{
			get => this.memberOffset;
		}

		public ILValueType MemberType
		{
			get => this.memberType;
		}

		public bool IsPointerAccess
		{
			get => this.isPointerAccess;
		}

		public override string ToCSString()
		{
			string op = this.isPointerAccess ? "->" : ".";
			return $"{this.structExpr.ToCSString()}{op}{this.memberName}";
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
