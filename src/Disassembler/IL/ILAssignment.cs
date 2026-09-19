using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Disassembler
{
	public class ILAssignment : ILExpression
	{
		private ILExpression leftOperand;
		private ILExpression rightOperand;

		public ILAssignment(ILExpression left, ILExpression right)
		{
			this.leftOperand = left;
			this.rightOperand = right;
		}

		public ILExpression LeftOperand
		{
			get => this.leftOperand;
		}

		public ILExpression RightOperand
		{
			get => this.rightOperand;
		}

		public override string ToCSString()
		{
			// an indirect memory target is written through a Write call, it's not an lvalue
			if (this.leftOperand is ILMemoryReference memoryReference)
			{
				return memoryReference.ToCSWriteString(this.rightOperand.ToCSString());
			}

			return $"{this.leftOperand.ToCSString()} = {this.rightOperand.ToCSString()}";
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
