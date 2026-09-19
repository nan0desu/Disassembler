using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Disassembler.CPU;

namespace Disassembler
{
	/// <summary>
	/// The value a CPU register holds on entry to the function, whatever the caller left in it.
	/// Registers that are read before they are written resolve to this, so that the usual
	/// 'PUSH SI ... POP SI' register preservation round trips instead of failing the translation.
	/// </summary>
	public class ILRegisterReference : ILExpression
	{
		private CPURegisterEnum register;
		private ILValueType valueType;

		public ILRegisterReference(CPURegisterEnum register, ILValueType valueType)
		{
			this.register = register;
			this.valueType = valueType;
		}

		public CPURegisterEnum Register { get => this.register; }

		public ILValueType ValueType { get => this.valueType; }

		public override string ToCSString()
		{
			switch (this.register)
			{
				case CPURegisterEnum.AL:
					return "this.oCPU.AX.LowUInt8";

				case CPURegisterEnum.CL:
					return "this.oCPU.CX.LowUInt8";

				case CPURegisterEnum.DL:
					return "this.oCPU.DX.LowUInt8";

				case CPURegisterEnum.BL:
					return "this.oCPU.BX.LowUInt8";

				case CPURegisterEnum.AH:
					return "this.oCPU.AX.HighUInt8";

				case CPURegisterEnum.CH:
					return "this.oCPU.CX.HighUInt8";

				case CPURegisterEnum.DH:
					return "this.oCPU.DX.HighUInt8";

				case CPURegisterEnum.BH:
					return "this.oCPU.BX.HighUInt8";

				default:
					return $"this.oCPU.{this.register}.UInt16";
			}
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
