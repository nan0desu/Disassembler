namespace Disassembler
{
	/// <summary>
	/// Represents an indirect function call through a function pointer.
	/// The target address is computed at runtime from a memory location.
	/// </summary>
	public class ILIndirectFunctionCall : ILExpression
	{
		private ILExpression targetExpression;
		private List<ILExpression> parameters;
		private bool isFarCall;

		public ILIndirectFunctionCall(ILExpression targetExpression, List<ILExpression> parameters, bool isFarCall)
		{
			this.targetExpression = targetExpression;
			this.parameters = parameters;
			this.isFarCall = isFarCall;
		}

		public ILExpression TargetExpression
		{
			get => this.targetExpression;
		}

		public List<ILExpression> Parameters
		{
			get => this.parameters;
		}

		public bool IsFarCall
		{
			get => this.isFarCall;
		}

		public override string ToCSString()
		{
			string args = string.Join(", ", this.parameters.ConvertAll(p => p.ToCSString()));
			return $"(({this.targetExpression.ToCSString()})({args}))";
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
