using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Disassembler
{
	[Flags]
	public enum ProgramFunctionOptionsEnum
	{
		Undefined = 0,
		Cdecl = 1,
		Pascal = 2,
		Near = 0x10,
		Far = 0x20,
		Interrupt = 0x40,
		CAPI = 0x80,
		Return = 0x100,
		VariableArguments = 0x200,
		CompilerInternal = 0x400
	}
}
