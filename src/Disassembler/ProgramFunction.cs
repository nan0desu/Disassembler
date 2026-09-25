using Disassembler.CPU;
using Disassembler.Formats.MZ;
using IRB.Collections.Generic;
using System.Reflection.Metadata;

namespace Disassembler
{
	public class ProgramFunction
	{
		private ProgramSegment parentSegment;

		private int ordinal = -1;
		private ushort fnOffset;
		private uint fnEntryPoint;
		private string? name;
		private bool isLibraryFunction = false;
		private ProgramFunctionOptionsEnum functionOptions = ProgramFunctionOptionsEnum.Cdecl;
		private ILVariable returnValue;
		private int stackSize = 0;
		private BDictionary<int, ILVariable> localParameters = new();
		private BDictionary<int, ILVariable> localVariables = new();
		private int localVariableSize = 0;
		private int localVariablePosition = 6;

		private List<ProgramFunction> referenceFunctions = new();

		private FlowGraph? flowGraph = null;
		private bool translatedToIL = false;

		// Assembly instructions
		private List<CPUInstruction> asmInstructions = new List<CPUInstruction>();
		// set when Disassemble() threw, so the writer can leave this function out
		private bool disassemblyFailed = false;

		public ProgramFunction(ProgramSegment segment, ushort offset, string? name)
		{
			this.parentSegment = segment;
			this.fnOffset = offset;
			this.fnEntryPoint = MainProgram.ToLinearAddress(segment.CPUSegment, offset);
			this.name = name;
			this.returnValue = new(this, this.parentSegment.ParentProgram.VoidValueType, 0);
		}

		public void Disassemble()
		{
			this.asmInstructions.Clear();
			uint fnLinearAddress = this.fnEntryPoint;

			#region Process function assembly instructions, including possible switch instructions
			uint fnSegment = MainProgram.ToLinearAddress(this.parentSegment.CPUSegment, 0);
			List<uint> aJumps = new();
			List<uint> aSwitches = new();
			byte[] exeData;
			uint fnAddress = this.fnEntryPoint;

			if (this.parentSegment.CPUOverlay > 0)
			{
				exeData = this.parentSegment.ParentProgram.Executable.GetOverlayData(this.parentSegment.CPUOverlay);
			}
			else
			{
				exeData = this.parentSegment.ParentProgram.Executable.Data;
			}

			if (fnAddress >= exeData.Length)
			{
				throw new Exception($"Trying to disassemble outside of executable range in function {this.parentSegment.Name}.{this.Name}, 0x{fnAddress:x}");
			}

			MemoryStream stream = new(exeData);

			stream.Seek(fnAddress, SeekOrigin.Begin);

			while (true)
			{
				CPUInstruction instruction1;
				bool bEnd = false;

				if (fnAddress >= stream.Length)
				{
					throw new Exception($"Trying to disassemble outside of executable range in function {this.parentSegment.Name}.{this.Name}, 0x{fnAddress:x}");
				}

				for (int i = 0; i < this.asmInstructions.Count; i++)
				{
					if (this.asmInstructions[i].LinearAddress == fnAddress)
					{
						bEnd = true;
						break;
					}
				}

				if (!bEnd)
				{
					CPUParameter parameter;
					CPUInstruction instruction = new CPUInstruction(this.parentSegment.CPUSegment, (ushort)(fnAddress - fnSegment), stream);

					this.asmInstructions.Add(instruction);
					fnAddress += (uint)instruction.Bytes.Count;

					switch (instruction.InstructionType)
					{
						case CPUInstructionEnum.JMP:
							parameter = instruction.Parameters[0];
							if (parameter.Type == CPUParameterTypeEnum.Immediate)
							{
								fnAddress = (uint)(fnSegment + parameter.Value);
								stream.Seek(fnAddress, SeekOrigin.Begin);
							}
							else if (parameter.Type == CPUParameterTypeEnum.MemoryAddress && parameter.Value == 6)
							{
								throw new Exception($"Relative jump to {parameter.ToString()} in function {this.parentSegment.Name}.{this.Name} " +
									$"(Instruction at 0x{instruction.LinearAddress:x})");
							}
							else if (parameter.Type == CPUParameterTypeEnum.MemoryAddress)
							{
								// probably switch statement
								aSwitches.Add(instruction.LinearAddress);
								bEnd = true;
							}
							else
							{
								Console.WriteLine($"Jump to computed address {parameter.ToString()} in function {this.parentSegment.Name}.{this.Name} " +
									$"(Instruction at 0x{instruction.LinearAddress:x})");
								// treat this as end of a instruction stream
								bEnd = true;
							}
							break;

						case CPUInstructionEnum.JMPF:
							parameter = instruction.Parameters[0];
							if (parameter.Type == CPUParameterTypeEnum.SegmentOffset)
							{
								fnAddress = MainProgram.ToLinearAddress(parameter.Segment, parameter.Value);

								if (fnAddress == 0)
								{
									this.parentSegment.GlobalVariables.Add((int)(instruction.Offset + 1), new ILVariable(this, ILVariableScopeEnum.Global,
										new ILValueType(ILBaseValueTypeEnum.FnPtr32), (int)(instruction.Offset + 1)));
									// treat this as end of a instruction stream
									bEnd = true;
								}
								else
								{
									stream.Seek(fnAddress, SeekOrigin.Begin);
								}
							}
							else
							{
								Console.WriteLine($"Jump to {parameter.ToString()} in function {this.parentSegment.Name}.{this.Name} " +
									$"(Instruction at 0x{instruction.LinearAddress:x})");
								// treat this as end of a instruction stream
								bEnd = true;
							}
							break;

						case CPUInstructionEnum.Jcc:
							parameter = instruction.Parameters[1];
							if (parameter.Type != CPUParameterTypeEnum.Immediate)
								throw new Exception(
									$"Relative offset expected, but got indirect parameter {parameter.ToString()} in function {this.parentSegment.Name}.{this.Name} " +
									$"(Instruction at 0x{instruction.LinearAddress:x})");
							aJumps.Add((uint)(fnSegment + parameter.Value));
							break;

						case CPUInstructionEnum.LOOP:
						case CPUInstructionEnum.LOOPNE:
						case CPUInstructionEnum.LOOPE:
						case CPUInstructionEnum.JCXZ:
							parameter = instruction.Parameters[0];
							if (parameter.Type != CPUParameterTypeEnum.Immediate)
								throw new Exception(
									$"Relative offset expected, but got indirect parameter {parameter.ToString()} in function {this.parentSegment.Name}.{this.Name} " +
									$"(Instruction at 0x{instruction.LinearAddress:x})");

							aJumps.Add((uint)(fnSegment + parameter.Value));
							break;

						case CPUInstructionEnum.INT:
							if (instruction.Parameters[0].Type == CPUParameterTypeEnum.Immediate &&
								instruction.Parameters[0].Value == 0x20)
							{
								// exit application instruction
								bEnd = true;
							}
							else if (instruction.Parameters[0].Type == CPUParameterTypeEnum.Immediate &&
								instruction.Parameters[0].Value == 0x3f)
							{
								// overlay manager
								instruction.InstructionType = CPUInstructionEnum.CallOverlay;
								int byte0 = stream.ReadByte();
								int byte1 = stream.ReadByte();
								int byte2 = stream.ReadByte();
								if (byte0 < 0 || byte1 < 0 || byte2 < 0)
									throw new Exception("Int 0x3F missing parameters");

								instruction.Bytes.Add((byte)(byte0 & 0xff));
								instruction.Bytes.Add((byte)(byte1 & 0xff));
								instruction.Bytes.Add((byte)(byte2 & 0xff));

								instruction.Parameters.Clear();
								instruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt8,
									(byte)(byte0 & 0xff)));
								instruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt16,
									(ushort)((byte1 & 0xff) | ((byte2 & 0xff) << 8))));
								fnAddress += 3;
							}
							else if (this.asmInstructions.Count > 1)
							{
								instruction1 = this.asmInstructions[this.asmInstructions.Count - 2];

								if (instruction1.Parameters.Count > 1 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[0].Size == CPUParameterSizeEnum.UInt8 &&
									instruction1.Parameters[0].Value == (uint)CPURegisterEnum.AH &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&
									instruction1.Parameters[1].Value == 0x0)
								{
									// exit application instruction
									bEnd = true;
								}
								else if (instruction1.Parameters.Count > 1 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[0].Size == CPUParameterSizeEnum.UInt8 &&
									instruction1.Parameters[0].Value == (uint)CPURegisterEnum.AH &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&
									instruction1.Parameters[1].Value == 0x4c)
								{
									// exit application instruction
									bEnd = true;
								}
								else if (instruction1.Parameters.Count > 1 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[0].Size == CPUParameterSizeEnum.UInt16 &&
									instruction1.Parameters[0].Value == (uint)CPURegisterEnum.AX &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&
									(instruction1.Parameters[1].Value & 0xff00) == 0x4c00)
								{
									// exit application instruction
									bEnd = true;
								}
							}
							break;

						case CPUInstructionEnum.RET:
							// convert near return to far return
							if ((this.functionOptions & ProgramFunctionOptionsEnum.Far) == ProgramFunctionOptionsEnum.Far)
							{
								Console.WriteLine($"Inconsistent function return type in {this.parentSegment.Name}.{this.Name}");
							}
							this.functionOptions |= ProgramFunctionOptionsEnum.Near;
							this.functionOptions |= ProgramFunctionOptionsEnum.Return;

							if (instruction.Parameters.Count == 1 && (this.functionOptions & ProgramFunctionOptionsEnum.Cdecl) == ProgramFunctionOptionsEnum.Cdecl)
							{
								Console.WriteLine($"Inconsistent function call type in {this.parentSegment.Name}.{this.Name}");
							}
							else if (instruction.Parameters.Count == 0 && (this.functionOptions & ProgramFunctionOptionsEnum.Pascal) == ProgramFunctionOptionsEnum.Pascal)
							{
								Console.WriteLine($"Inconsistent function call type in {this.parentSegment.Name}.{this.Name}");
							}
							else
							{
								if (instruction.Parameters.Count == 1)
								{
									this.functionOptions |= ProgramFunctionOptionsEnum.Pascal;
								}
								else
								{
									this.functionOptions |= ProgramFunctionOptionsEnum.Cdecl;
								}
							}
							bEnd = true;
							break;

						case CPUInstructionEnum.RETF:
							if ((this.functionOptions & ProgramFunctionOptionsEnum.Near) == ProgramFunctionOptionsEnum.Near)
							{
								Console.WriteLine($"Inconsistent function return type in {this.parentSegment.Name}.{this.Name}");
							}
							this.functionOptions |= ProgramFunctionOptionsEnum.Far;
							this.functionOptions |= ProgramFunctionOptionsEnum.Return;

							if (instruction.Parameters.Count == 1 && (this.functionOptions & ProgramFunctionOptionsEnum.Cdecl) == ProgramFunctionOptionsEnum.Cdecl)
							{
								Console.WriteLine($"Inconsistent function call type in {this.parentSegment.Name}.{this.Name}");
							}
							else if (instruction.Parameters.Count == 0 && (this.functionOptions & ProgramFunctionOptionsEnum.Pascal) == ProgramFunctionOptionsEnum.Pascal)
							{
								Console.WriteLine($"Inconsistent function call type in {this.parentSegment.Name}.{this.Name}");
							}
							else
							{
								if (instruction.Parameters.Count == 1)
								{
									this.functionOptions |= ProgramFunctionOptionsEnum.Pascal;
								}
								else
								{
									this.functionOptions |= ProgramFunctionOptionsEnum.Cdecl;
								}
							}
							bEnd = true;
							break;

						case CPUInstructionEnum.IRET:
							if ((this.functionOptions & ProgramFunctionOptionsEnum.Near) == ProgramFunctionOptionsEnum.Near ||
								(this.functionOptions & ProgramFunctionOptionsEnum.Pascal) == ProgramFunctionOptionsEnum.Pascal)
							{
								Console.WriteLine($"Inconsistent function call type in {this.parentSegment.Name}.{this.Name}");
							}
							this.functionOptions |= ProgramFunctionOptionsEnum.Far;
							this.functionOptions |= ProgramFunctionOptionsEnum.Interrupt;
							this.functionOptions |= ProgramFunctionOptionsEnum.Return;

							bEnd = true;
							break;
					}
				}

				if (bEnd)
				{
					// jumps
					if (aJumps.Count > 0)
					{
						fnAddress = aJumps[aJumps.Count - 1];
						aJumps.RemoveAt(aJumps.Count - 1);
						stream.Seek(fnAddress, SeekOrigin.Begin);

						continue;
					}

					// switches
					if (aSwitches.Count > 0)
					{
						// sort instructions by address before doing switches
						this.asmInstructions.Sort((item1, item2) => item1.LinearAddress.CompareTo(item2.LinearAddress));

						fnAddress = aSwitches[aSwitches.Count - 1];
						aSwitches.RemoveAt(aSwitches.Count - 1);

						int iPos = GetInstructionPositionByLinearAddress(fnAddress);
						if (iPos >= 0)
						{
							if (iPos > 5)
							{
								int iPos1;
								CPUParameter parameter;
								uint uiCount = 0;
								ushort usSwitchOffset = 0;

								// first pattern
								if ((instruction1 = this.asmInstructions[iPos1 = iPos - 5]).InstructionType == CPUInstructionEnum.CMP &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									(parameter = instruction1.Parameters[0]).RegisterValue == CPURegisterEnum.AX &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&
									(uiCount = instruction1.Parameters[1].Value) >= 0 &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.Jcc &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Condition &&
									instruction1.Parameters[0].Value == (uint)CPUJumpConditionEnum.BE &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.JMP &&
									instruction1.Parameters.Count == 1 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Immediate &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.ADD &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									(parameter = instruction1.Parameters[0]).RegisterValue == CPURegisterEnum.AX &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[1].Value == instruction1.Parameters[0].Value &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.XCHG &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[0].RegisterValue == CPURegisterEnum.AX &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[1].RegisterValue == CPURegisterEnum.BX &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.JMP &&
									instruction1.Parameters.Count == 1 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.MemoryAddress &&
									(usSwitchOffset = (ushort)instruction1.Parameters[0].Displacement) >= 0 &&

									iPos == iPos1)
								{
									//Console.WriteLine("Switch type 1 at {0}:0x{1:x4}", this.uiSegment, this.aInstructions[iPos].Location.Offset);

									this.asmInstructions[iPos - 2].InstructionType = CPUInstructionEnum.NOP;
									this.asmInstructions[iPos - 1].InstructionType = CPUInstructionEnum.NOP;
									instruction1 = this.asmInstructions[iPos];
									instruction1.InstructionType = CPUInstructionEnum.SWITCH;
									instruction1.Parameters.Clear();

									// switching parameter
									instruction1.Parameters.Add(parameter);

									stream.Seek(fnSegment + usSwitchOffset, SeekOrigin.Begin);

									// values and offsets
									for (int i = 0; i <= uiCount; i++)
									{
										ushort usWord = ReadWord(stream);
										aJumps.Add((uint)(fnSegment + usWord));
										instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate,
											CPUParameterSizeEnum.UInt16, (uint)i, usWord));
									}

									fnAddress = aJumps[aJumps.Count - 1];
									aJumps.RemoveAt(aJumps.Count - 1);

									stream.Seek(fnAddress, SeekOrigin.Begin);
								}
								else if ((instruction1 = this.asmInstructions[iPos1 = iPos - 5]).InstructionType == CPUInstructionEnum.SUB &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[0].RegisterValue == CPURegisterEnum.AX &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.CMP &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									(parameter = instruction1.Parameters[0]).RegisterValue == CPURegisterEnum.AX &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&
									(uiCount = instruction1.Parameters[1].Value) >= 0 &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.Jcc &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Condition &&
									instruction1.Parameters[0].Value == (uint)CPUJumpConditionEnum.A &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.ADD &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									(parameter = instruction1.Parameters[0]).Value == (uint)CPURegisterEnum.AX &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[1].Value == instruction1.Parameters[0].Value &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.XCHG &&
									instruction1.Parameters.Count == 2 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[0].RegisterValue == CPURegisterEnum.AX &&
									instruction1.Parameters[1].Type == CPUParameterTypeEnum.Register &&
									instruction1.Parameters[1].RegisterValue == CPURegisterEnum.BX &&

									(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.JMP &&
									instruction1.Parameters.Count == 1 &&
									instruction1.Parameters[0].Type == CPUParameterTypeEnum.MemoryAddress &&
									(usSwitchOffset = (ushort)instruction1.Parameters[0].Displacement) >= 0 &&

									iPos == iPos1)
								{
									//Console.WriteLine("Switch type 1 at {0}:0x{1:x4}", this.uiSegment, this.aInstructions[iPos].Location.Offset);

									this.asmInstructions[iPos - 2].InstructionType = CPUInstructionEnum.NOP;
									this.asmInstructions[iPos - 1].InstructionType = CPUInstructionEnum.NOP;
									instruction1 = this.asmInstructions[iPos];
									instruction1.InstructionType = CPUInstructionEnum.SWITCH;
									instruction1.Parameters.Clear();

									// switching parameter
									instruction1.Parameters.Add(parameter);

									stream.Seek(fnSegment + usSwitchOffset, SeekOrigin.Begin);

									// values and offsets
									for (int i = 0; i <= uiCount; i++)
									{
										ushort usWord = ReadWord(stream);
										aJumps.Add((uint)(fnSegment + usWord));
										instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate,
											CPUParameterSizeEnum.UInt16, (uint)i, usWord));
									}

									fnAddress = aJumps[aJumps.Count - 1];
									aJumps.RemoveAt(aJumps.Count - 1);

									stream.Seek(fnAddress, SeekOrigin.Begin);
								}
								else
								{
									instruction1 = this.asmInstructions[iPos];
									Console.WriteLine($"Undefined switch pattern {instruction1.Parameters[0].ToString()} in function {this.parentSegment.Name}.{this.Name} " +
										$"(Instruction at 0x{instruction1.LinearAddress:x})");
									//break;
								}
							}
						}
						else
						{
							Console.WriteLine($"Can't find location of switch statement in function {this.parentSegment.Name}.{this.Name} " +
								$"(Instruction at 0x{fnAddress:x})");
						}
						continue;
					}

					// no more jumps or switches, we are done
					break;
				}
			}

			stream.Close();
			#endregion

			#region Sort instructions by their address and asign them their ordinal to use when inserting and removing instructions
			this.asmInstructions.Sort((item1, item2) => item1.LinearAddress.CompareTo(item2.LinearAddress));

			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				this.asmInstructions[i].InstructionOrdinal = i;
			}
			#endregion

			#region Optimize GoTo's
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];
				CPUParameter parameter0;

				switch (instruction.InstructionType)
				{
					case CPUInstructionEnum.LOOP:
					case CPUInstructionEnum.LOOPE:
					case CPUInstructionEnum.LOOPNE:
					case CPUInstructionEnum.JCXZ:
					case CPUInstructionEnum.JMP:
					case CPUInstructionEnum.JMPF:
						parameter0 = instruction.Parameters[0];

						if (parameter0.Type == CPUParameterTypeEnum.Immediate || parameter0.Type == CPUParameterTypeEnum.SegmentOffset)
						{
							// optimize immediate jumps
							CPUInstruction instruction1;
							ushort segment;
							uint address;

							if (parameter0.Type == CPUParameterTypeEnum.Immediate)
							{
								segment = instruction.Segment;
								address = (uint)(fnSegment + parameter0.Value);
							}
							else
							{
								segment = parameter0.Segment;
								address = MainProgram.ToLinearAddress(parameter0.Segment, parameter0.Value);
							}

							if (instruction.InstructionType != CPUInstructionEnum.JMPF || address != 0)
							{
								uint newAddress = address;
								ushort newSegment = segment;

								int targetPosition;

								// the chain can leave this function, into an overlay that isn't loaded
								// or into code not disassembled yet. Stop there and leave the jump alone.
								while ((targetPosition = GetInstructionPositionByLinearAddress(newAddress)) >= 0 &&
									((instruction1 = this.asmInstructions[targetPosition]).InstructionType == CPUInstructionEnum.JMP ||
									instruction1.InstructionType == CPUInstructionEnum.JMPF))
								{
									CPUParameter parameter1 = instruction1.Parameters[0];

									if (parameter1.Type == CPUParameterTypeEnum.Immediate)
									{
										newSegment = instruction1.Segment;
										newAddress = MainProgram.ToLinearAddress(newSegment, instruction1.Parameters[0].Value);
									}
									else if (parameter1.Type == CPUParameterTypeEnum.SegmentOffset)
									{
										newSegment = instruction1.Parameters[0].Segment;
										newAddress = MainProgram.ToLinearAddress(newSegment, instruction1.Parameters[0].Value);
									}
									else
									{
										break;
									}

									if (instruction1.InstructionType == CPUInstructionEnum.JMPF && newAddress == 0)
										break;
								}

								if (address != newAddress)
								{
									if (segment != newSegment)
									{
										instruction.InstructionType = CPUInstructionEnum.JMPF;
										instruction.Parameters.Clear();
										instruction.Parameters.Add(new CPUParameter(newSegment, newAddress - MainProgram.ToLinearAddress(newSegment, 0)));
									}
									else
									{
										instruction.InstructionType = CPUInstructionEnum.JMP;
										instruction.Parameters.Clear();
										instruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, newAddress - MainProgram.ToLinearAddress(newSegment, 0)));
									}
								}
							}
						}
						break;

					case CPUInstructionEnum.Jcc:
						parameter0 = instruction.Parameters[1];

						if (parameter0.Type == CPUParameterTypeEnum.Immediate)
						{
							// optimize immediate jumps
							CPUInstruction instruction1;
							uint address = (uint)(fnSegment + parameter0.Value);
							uint newAddress = address;

							int targetPosition;

							// same as above, the chain can leave the instructions we have
							while ((targetPosition = GetInstructionPositionByLinearAddress(newAddress)) >= 0 &&
								(instruction1 = this.asmInstructions[targetPosition]).InstructionType == CPUInstructionEnum.JMP)
							{
								newAddress = (uint)(fnSegment + instruction1.Parameters[0].Value);
							}
							if (address != newAddress)
							{
								parameter0.Value = (ushort)(newAddress - fnSegment);
							}
						}
						break;
				}
			}
			#endregion

			#region Convert relative call(s) to absolute call(s)
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction asmInstruction = this.asmInstructions[i];
				CPUInstruction asmInstruction1;

				if (asmInstruction.InstructionType == CPUInstructionEnum.CALL)
				{
					if (this.parentSegment.CPUOverlay == 0)
					{
						if (i > 0 &&
							(asmInstruction1 = this.asmInstructions[i - 1]).InstructionType == CPUInstructionEnum.PUSH &&
							asmInstruction1.Parameters[0].Type == CPUParameterTypeEnum.SegmentRegister &&
							asmInstruction1.Parameters[0].Value == (uint)CPUSegmentRegisterEnum.CS)
						{
							asmInstruction.InstructionType = CPUInstructionEnum.CALLF;
							asmInstruction.Parameters[0] = new CPUParameter(asmInstruction.Segment, asmInstruction.Parameters[0].Value);

							asmInstruction1.InstructionType = CPUInstructionEnum.NOP;
							asmInstruction1.Parameters.Clear();
						}
					}
					else
					{
						// if we are calling inside of overlay translate this to call overlay function
						CPUParameter parameter = asmInstruction.Parameters[0];

						asmInstruction.InstructionType = CPUInstructionEnum.CallOverlay;
						asmInstruction.Parameters.Clear();
						asmInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt8, this.parentSegment.CPUOverlay));
						asmInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt16, parameter.Value));
					}
				}
				else if (asmInstruction.InstructionType == CPUInstructionEnum.CALLF &&
					asmInstruction.Parameters[0].Type == CPUParameterTypeEnum.SegmentOffset)
				{
					// Colonization calls an overlaid function through a thunk rather than through
					// an INT 3F, so the far call's target is the trampoline, not the function. The
					// real address is the overlay number and entry offset the thunk carries, which
					// is the same pair CallOverlay already expects.
					MZOverlayThunk? thunk = this.parentSegment.ParentProgram.Executable.GetThunkAt(
						(int)MainProgram.ToLinearAddress(asmInstruction.Parameters[0].Segment, asmInstruction.Parameters[0].Value));

					if (thunk != null && thunk.IsOverlay)
					{
						asmInstruction.InstructionType = CPUInstructionEnum.CallOverlay;
						asmInstruction.Parameters.Clear();
						asmInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt8, (uint)thunk.OverlayNumber));
						asmInstruction.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, CPUParameterSizeEnum.UInt16, (uint)thunk.TargetOffset));
					}
					else if (thunk != null)
					{
						// A thunk to an already resident function still has to be stepped over: the
						// thunk tables are a solid block of trampolines with nothing between them,
						// so disassembling one as if it were the function walks through every thunk
						// that follows it and off the end of the load module. Point the call at the
						// function the thunk names instead.
						asmInstruction.Parameters[0] = new CPUParameter(thunk.TargetSegment, (uint)thunk.TargetOffset);
					}
				}
			}
			#endregion

			#region Assign labels
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];
				CPUInstruction instruction1;
				CPUParameter parameter;
				uint newAddress;

				switch (instruction.InstructionType)
				{
					case CPUInstructionEnum.JMP:
						parameter = instruction.Parameters[0];

						if (parameter.Type == CPUParameterTypeEnum.Immediate)
						{
							newAddress = (uint)(fnSegment + parameter.Value);

							// optimize immediate jumps
							if (i + 1 < this.asmInstructions.Count &&
								this.asmInstructions[i + 1].LinearAddress == newAddress)
							{
								// this is just a jump to next instruction, ignore it
								this.asmInstructions[i].InstructionType = CPUInstructionEnum.NOP;
							}
							else
							{
								MarkLabel(newAddress);
							}
						}
						break;

					case CPUInstructionEnum.JMPF:
						parameter = instruction.Parameters[0];

						if (parameter.Type == CPUParameterTypeEnum.SegmentOffset)
						{
							newAddress = MainProgram.ToLinearAddress(parameter.Segment, parameter.Value);

							if (newAddress != 0)
							{
								MarkLabel(newAddress);
							}
						}
						break;

					case CPUInstructionEnum.Jcc:
						newAddress = (ushort)instruction.Parameters[1].Value;
						MarkLabel((uint)(fnSegment + newAddress));
						break;

					case CPUInstructionEnum.LOOP:
					case CPUInstructionEnum.LOOPE:
					case CPUInstructionEnum.LOOPNE:
					case CPUInstructionEnum.JCXZ:
						newAddress = (ushort)(instruction.Parameters[0].Value & 0xffff);
						MarkLabel((uint)(fnSegment + newAddress));
						break;

					case CPUInstructionEnum.SWITCH:
						for (int j = 1; j < instruction.Parameters.Count; j++)
						{
							parameter = instruction.Parameters[j];

							MarkLabel((uint)(fnSegment + parameter.Displacement));
						}
						break;
				}
			}
			#endregion

			#region Optimize (XOR, SUB), (PUSH word, PUSH word, POP dword) and (LEA, PUSH)
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];
				CPUInstruction instruction1;
				uint sourceReg1 = 0;
				uint sourceReg2 = 0;
				uint destinationReg = 0;

				// all xors and subs with same source and destination are 0
				if (i + 1 < this.asmInstructions.Count &&
					((instruction1 = this.asmInstructions[i]).InstructionType == CPUInstructionEnum.XOR ||
					instruction1.InstructionType == CPUInstructionEnum.SUB) &&
					instruction1.Parameters.Count == 2 &&
					instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
					instruction1.Parameters[1].Type == CPUParameterTypeEnum.Register &&
					instruction1.Parameters[0].Size == instruction1.Parameters[1].Size &&
					instruction1.Parameters[0].Value == instruction1.Parameters[1].Value &&

					(instruction1 = this.asmInstructions[i + 1]).InstructionType != CPUInstructionEnum.Jcc)
				{
					instruction1 = this.asmInstructions[i];
					instruction1.InstructionType = CPUInstructionEnum.MOV;
					instruction1.Parameters[1] = new CPUParameter(CPUParameterTypeEnum.Immediate, 0);
				}

				// optimize convert two words to dword
				if (i + 2 < this.asmInstructions.Count &&
					(instruction1 = this.asmInstructions[i]).InstructionType == CPUInstructionEnum.PUSH &&
					instruction1.OperandSize == CPUParameterSizeEnum.UInt16 &&
					instruction1.Parameters.Count == 1 &&
					instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
					(sourceReg1 = instruction1.Parameters[0].Value) != (uint)CPURegisterEnum.Invalid &&

					(instruction1 = this.asmInstructions[i + 1]).InstructionType == CPUInstructionEnum.PUSH &&
					instruction1.OperandSize == CPUParameterSizeEnum.UInt16 &&
					instruction1.Parameters.Count == 1 &&
					instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
					(sourceReg2 = instruction1.Parameters[0].Value) != (uint)CPURegisterEnum.Invalid &&

					(instruction1 = this.asmInstructions[i + 2]).InstructionType == CPUInstructionEnum.POP &&
					instruction1.OperandSize == CPUParameterSizeEnum.UInt32 &&
					instruction1.Parameters.Count == 1 &&
					instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
					(destinationReg = instruction1.Parameters[0].Value) != (uint)CPURegisterEnum.Invalid)
				{
					instruction1 = this.asmInstructions[i];
					instruction1.InstructionType = CPUInstructionEnum.WordsToDword;
					instruction1.OperandSize = CPUParameterSizeEnum.UInt32;
					instruction1.Parameters.Clear();
					instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Register,
						CPUParameterSizeEnum.UInt32, destinationReg));
					instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Register,
						CPUParameterSizeEnum.UInt16, sourceReg1));
					instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Register,
						CPUParameterSizeEnum.UInt16, sourceReg2));

					this.asmInstructions[i + 1].InstructionType = CPUInstructionEnum.NOP;
					this.asmInstructions[i + 2].InstructionType = CPUInstructionEnum.NOP;
				}
				if (i + 1 < this.asmInstructions.Count &&
					(instruction1 = this.asmInstructions[i]).InstructionType == CPUInstructionEnum.LEA &&
					instruction1.OperandSize == CPUParameterSizeEnum.UInt16 &&
					instruction1.Parameters.Count == 2 &&
					instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
					(sourceReg1 = instruction1.Parameters[0].Value) != (uint)CPURegisterEnum.Invalid &&

					(instruction1 = this.asmInstructions[i + 1]).InstructionType == CPUInstructionEnum.PUSH &&
					instruction1.OperandSize == CPUParameterSizeEnum.UInt16 &&
					instruction1.Parameters.Count == 1 &&
					instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
					instruction1.Parameters[0].Value == sourceReg1)
				{
					this.asmInstructions[i].InstructionType = CPUInstructionEnum.NOP;
					CPUParameter parameter = this.asmInstructions[i].Parameters[1];
					instruction1.Parameters[0] = new CPUParameter(CPUParameterTypeEnum.LEAMemoryAddress,
						parameter.Size, parameter.DataSegment, parameter.Value, parameter.Displacement);
				}
			}
			#endregion

			#region Remove Nop, Wait and unused GoTo instructions
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];
				CPUInstruction instruction1;

				switch (instruction.InstructionType)
				{
					case CPUInstructionEnum.NOP:
					case CPUInstructionEnum.WAIT:
						if (!instruction.Label)
						{
							this.asmInstructions.RemoveAt(i);
							i--;
						}
						break;

					case CPUInstructionEnum.JMP:
					case CPUInstructionEnum.JMPF:
						if (i + 1 < this.asmInstructions.Count &&
							((instruction1 = this.asmInstructions[i + 1]).InstructionType == CPUInstructionEnum.JMP ||
							instruction1.InstructionType == CPUInstructionEnum.JMPF) &&
							!instruction1.Label)
						{
							this.asmInstructions.RemoveAt(i + 1);
							i--;
						}
						break;
				}
			}
			#endregion

			#region Convert Relative stack Memory addresses to local variables and parameters
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];
				CPUParameter parameter;

				for (int j = 0; j < instruction.Parameters.Count; j++)
				{
					parameter = instruction.Parameters[j];
					int varOffset;

					switch (parameter.Type)
					{
						case CPUParameterTypeEnum.MemoryAddress:
						case CPUParameterTypeEnum.LEAMemoryAddress:
							if (parameter.Size == CPUParameterSizeEnum.UInt32)
								throw new Exception("x32 addressing mode not yet implemented");

							if (parameter.DataSegment == CPUSegmentRegisterEnum.SS)
							{
								switch (parameter.Value)
								{
									case 2: // [BP + SI]
									case 3: // [BP + DI]
										Console.WriteLine($"Stack parameter with register offset {parameter.ToString()} in function {this.parentSegment.Name}.{this.Name} " +
											$"(Instruction at 0x{instruction.LinearAddress:x})");
										break;

									case 10:
									case 18:
										// [BP + SI {0}]
										varOffset = parameter.Displacement;
										if (varOffset < 0)
										{
											// local variable
											varOffset = -varOffset;

											/*if (iVarOffset > this.LocalStackSize)
											{
												Console.WriteLine($"Local variable 0x{iVarOffset:x} is outside of a local stack space 0x{this.LocalStackSize:x} "+
													$"at function {this.parent.ToString()}.{this.Name}' (instruction at 0x{instruction.Offset:x4})");
											}*/

											instruction.Parameters[j] = new CPUParameter(CPUParameterTypeEnum.LocalVariableWithSI,
												CPUParameterSizeEnum.UInt16, CPUSegmentRegisterEnum.SS, 0, varOffset);

											if (!this.localVariables.ContainsKey(varOffset))
											{
												ILVariable variable = new ILVariable(this, ILVariableScopeEnum.LocalVariable,
													this.parentSegment.ParentProgram.FromCPUParameterSizeEnum(instruction.OperandSize), varOffset);
												variable.ArraySize = 1;
												this.localVariables.Add(varOffset, variable);
											}
											else
											{
												// ansure that we mark this as an array
												this.localVariables.GetValueByKey(varOffset).ArraySize = 1;
											}
										}
										else if (varOffset > 0)
										{
											throw new Exception($"Local parameters can't be indexed with SI in {this.ParentSegment.Name}.{this.Name} at 0x{this.fnOffset:x}");
										}
										break;

									case 11:
									case 19:
										// [BP + DI {0}]
										varOffset = parameter.Displacement;
										if (varOffset < 0)
										{
											// local variable
											varOffset = -varOffset;

											/*if (iVarOffset > this.LocalStackSize)
											{
												Console.WriteLine($"Local variable 0x{iVarOffset:x} is outside of a local stack space 0x{this.LocalStackSize:x} " +
													$"at function {this.parent.ToString()}.{this.Name}' (instruction at 0x{instruction.Offset:x4})");
											}*/

											instruction.Parameters[j] = new CPUParameter(CPUParameterTypeEnum.LocalVariableWithDI,
												CPUParameterSizeEnum.UInt16, CPUSegmentRegisterEnum.SS, 0, varOffset);

											if (!this.localVariables.ContainsKey(varOffset))
											{
												ILVariable variable = new ILVariable(this, ILVariableScopeEnum.LocalVariable,
													this.parentSegment.ParentProgram.FromCPUParameterSizeEnum(instruction.OperandSize), varOffset);
												variable.ArraySize = 1;
												this.localVariables.Add(varOffset, variable);
											}
											else
											{
												// ansure that we mark this as an array
												this.localVariables.GetValueByKey(varOffset).ArraySize = 1;
											}
										}
										else if (varOffset > 0)
										{
											throw new Exception($"Local parameters can't be indexed with DI in {this.ParentSegment.Name}.{this.Name} at 0x{this.fnOffset:x}");
										}
										break;

									case 14:
									case 22:
										// [BP {0}]
										varOffset = parameter.Displacement;
										if (varOffset < 0)
										{
											// local variable
											varOffset = -varOffset;

											/*if (iVarOffset > this.LocalStackSize)
											{
												Console.WriteLine($"Local variable 0x{iVarOffset:x} is outside of a local stack space 0x{this.LocalStackSize:x} " +
													$"at function {this.parent.ToString()}.{this.Name}' (instruction at 0x{instruction.Offset:x4})");
											}*/

											instruction.Parameters[j] = new CPUParameter(CPUParameterTypeEnum.LocalVariable,
												CPUParameterSizeEnum.UInt16, CPUSegmentRegisterEnum.SS, 0, varOffset);

											if (!this.localVariables.ContainsKey(varOffset))
											{
												this.localVariables.Add(varOffset, new ILVariable(this,
													this.parentSegment.ParentProgram.FromCPUParameterSizeEnum(instruction.OperandSize), varOffset));
											}
										}
										else if (varOffset > 0)
										{
											// local parameters
											instruction.Parameters[j] = new CPUParameter(CPUParameterTypeEnum.LocalParameter,
												CPUParameterSizeEnum.UInt16, CPUSegmentRegisterEnum.SS, 0, varOffset);

											if (!this.localParameters.ContainsKey(varOffset))
											{
												this.localParameters.Add(varOffset, new ILVariable(this, ILVariableScopeEnum.LocalParameter,
													this.parentSegment.ParentProgram.FromCPUParameterSizeEnum(instruction.OperandSize), varOffset));
											}
										}
										break;
								}
							}
							break;
					}
				}
			}
			#endregion

			#region Assign ordinals to labels

			int startPos = 0;

			while (startPos < this.asmInstructions.Count && this.asmInstructions[startPos].InstructionType == CPUInstructionEnum.Remark)
			{
				startPos++;
			}

			if (this.asmInstructions.Count > 0 && this.fnEntryPoint != this.asmInstructions[startPos].LinearAddress)
			{
				CPUInstruction? instruction = GetInstructionByLinearAddress(this.fnEntryPoint);

				if (instruction != null)
				{
					instruction.Label = true;
				}
			}

			int labelOrdinal = 1;

			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];

				if (instruction.Label)
				{
					instruction.LabelOrdinal = labelOrdinal++;
				}
			}
			#endregion

			#region Adjust Local paremeter and variable size according to instruction
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];
				CPUParameter parameter;

				switch (instruction.InstructionType)
				{
					case CPUInstructionEnum.LDS:
					case CPUInstructionEnum.LES:
					case CPUInstructionEnum.JMPF:
					case CPUInstructionEnum.CALLF:
						parameter = instruction.Parameters[0];

						if (parameter.Type == CPUParameterTypeEnum.LocalParameter)
						{
							ILVariable local = this.localParameters.GetValueByKey(parameter.Displacement);

							local.ValueType = this.parentSegment.ParentProgram.UnsignedLongValueType;
							local.ValueTypeConfidence = 5;

							parameter.Size = CPUParameterSizeEnum.UInt32;
						}
						else if (parameter.Type == CPUParameterTypeEnum.LocalVariable ||
							parameter.Type == CPUParameterTypeEnum.LocalVariableWithSI ||
							parameter.Type == CPUParameterTypeEnum.LocalVariableWithDI ||
							parameter.Type == CPUParameterTypeEnum.LocalArray)
						{
							ILVariable local = this.localVariables.GetValueByKey(parameter.Displacement);

							local.ValueType = this.parentSegment.ParentProgram.UnsignedLongValueType;
							local.ValueTypeConfidence = 5;

							parameter.Size = CPUParameterSizeEnum.UInt32;
						}
						break;
				}
			}
			#endregion

			#region Find multiple different Return instructions and add a fixup warning to them
			int retCount = 0;
			int retfCount = 0;
			int iretCount = 0;

			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];

				switch (instruction.InstructionType)
				{
					case CPUInstructionEnum.RET:
						retCount++;
						break;

					case CPUInstructionEnum.RETF:
						retfCount++;
						break;

					case CPUInstructionEnum.IRET:
						iretCount++;
						break;
				}
			}

			if ((retCount > 0 && (retfCount + iretCount) > 0) ||
				(retfCount > 0 && (retCount + iretCount) > 0) ||
				(iretCount > 0 && (retCount + retfCount) > 0))
			{
				for (int i = 0; i < this.asmInstructions.Count; i++)
				{
					CPUInstruction instruction = this.asmInstructions[i];

					switch (instruction.InstructionType)
					{
						case CPUInstructionEnum.RET:
						case CPUInstructionEnum.RETF:
						case CPUInstructionEnum.IRET:
							int newOrdinal = instruction.InstructionOrdinal;

							this.AddInstruction(newOrdinal,
								new CPUInstruction(newOrdinal, "Function fixup needed, there are multiple function return type instructions"));

							newOrdinal++;
							i++;
							break;
					}
				}
			}
			#endregion

			#region Replace Absolute Jumps to 0 with Calls to variable functions
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];
				CPUParameter parameter;

				switch (instruction.InstructionType)
				{
					case CPUInstructionEnum.JMPF:
						parameter = instruction.Parameters[0];
						if (parameter.Type == CPUParameterTypeEnum.SegmentOffset)
						{
							fnAddress = MainProgram.ToLinearAddress(parameter.Segment, parameter.Value);

							if (fnAddress == 0)
							{
								int newOrdinal = instruction.InstructionOrdinal;

								this.AddInstruction(newOrdinal,
									new CPUInstruction(newOrdinal, "Function fixup needed, the calling stack is out of aligment"));

								newOrdinal++;
								i++;

								this.AddInstruction(newOrdinal, 
									new CPUInstruction(newOrdinal, $"Call to variable function. Address of a call is located at {this.parentSegment}:0x{(instruction.Offset + 1):x4}"));

								newOrdinal++;
								i++;
							}
						}
						break;
				}
			}
			#endregion

			#region Translate inline If statements
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				int iPos1 = i;
				CPUInstruction instruction = this.asmInstructions[i];
				CPUInstruction instruction1;
				uint jumpOffset1;
				uint jumpOffset2;
				CPUJumpConditionEnum condition;
				CPURegisterEnum register;
				CPUParameterSizeEnum registerSize;
				uint value1;
				uint value2;

				if (iPos1 + 4 < this.asmInstructions.Count)
				{
					switch (instruction.InstructionType)
					{
						case CPUInstructionEnum.Jcc:
							if ((instruction1 = this.asmInstructions[iPos1]).InstructionType == CPUInstructionEnum.Jcc &&
							instruction1.Parameters.Count == 2 &&
							instruction1.Parameters[0].Type == CPUParameterTypeEnum.Condition &&
							(condition = (CPUJumpConditionEnum)instruction1.Parameters[0].Value) != CPUJumpConditionEnum.Undefined &&
							instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&
							(jumpOffset1 = instruction1.Parameters[1].Value) >= 0 &&

							(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.MOV &&
							!instruction1.Label &&
							instruction1.Parameters.Count == 2 &&
							instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
							(register = instruction1.Parameters[0].RegisterValue) >= 0 &&
							(registerSize = instruction1.Parameters[0].Size) >= 0 &&
							instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&
							(value1 = instruction1.Parameters[1].Value) >= 0 &&

							(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.JMP &&
							!instruction1.Label &&
							instruction1.Parameters.Count == 1 &&
							instruction1.Parameters[0].Type == CPUParameterTypeEnum.Immediate &&
							(jumpOffset2 = instruction1.Parameters[0].Value) >= 0 &&

							(instruction1 = this.asmInstructions[++iPos1]).InstructionType == CPUInstructionEnum.MOV &&
							instruction1.Label &&
							instruction1.ReferenceCount == 1 &&
							instruction1.Offset == jumpOffset1 &&
							instruction1.Parameters.Count == 2 &&
							instruction1.Parameters[0].Type == CPUParameterTypeEnum.Register &&
							instruction1.Parameters[0].Size == registerSize &&
							instruction1.Parameters[0].RegisterValue == register &&
							instruction1.Parameters[1].Type == CPUParameterTypeEnum.Immediate &&
							(value2 = instruction1.Parameters[1].Value) >= 0 &&

							(instruction1 = this.asmInstructions[++iPos1]).Label &&
							instruction1.Offset == jumpOffset2)
							{
								//Console.Write("");
								instruction1 = this.asmInstructions[i];
								instruction1.InstructionType = CPUInstructionEnum.IIf;
								instruction1.OperandSize = registerSize;
								instruction1.Parameters.Clear();

								instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Register, (CPUParameterSizeEnum)registerSize, (uint)register));
								instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Condition, (uint)condition));
								instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, (CPUParameterSizeEnum)registerSize, (uint)value2));
								instruction1.Parameters.Add(new CPUParameter(CPUParameterTypeEnum.Immediate, (CPUParameterSizeEnum)registerSize, (uint)value1));

								RemoveInstruction(instruction1.InstructionOrdinal + 1);
								RemoveInstruction(instruction1.InstructionOrdinal + 1);
								RemoveInstruction(instruction1.InstructionOrdinal + 1);

								// adjust next instruction reference count
								instruction1 = this.asmInstructions[i + 1];
								if (instruction1.ReferenceCount == 1)
								{
									instruction1.Label = false;
								}
								instruction1.ReferenceCount--;
							}
							break;
					}
				}
			}
			#endregion

			this.flowGraph = new FlowGraph(this, this.Name);

			#region Check If statements, but only for non assembly functions
			if (this.flowGraph.HasBPFrame)
			{
				for (int i = 0; i < this.asmInstructions.Count; i++)
				{
					CPUInstruction instruction = this.asmInstructions[i];
					CPUInstruction instruction1;

					switch (instruction.InstructionType)
					{
						case CPUInstructionEnum.Jcc:
							if (!instruction.Label && i > 0)
							{
								int count = 0;
								int pos = i - 1;
								CPUJumpConditionEnum condition;

								instruction1 = this.asmInstructions[pos];

								// search backwards for compare instruction up to 3 instruction before main jcc instruction
								while (count < 3 && pos >= 0 && (instruction1 = this.asmInstructions[pos]).InstructionType == CPUInstructionEnum.Jcc)
								{
									if (instruction1.Label)
									{
										Console.WriteLine($"Detected label while searching for Jcc compare instruction in function {this.parentSegment.Name}.{this.Name}, 0x{fnAddress:x}");
										pos = -1;
										break;
									}

									pos--;
									count++;
								}

								if (pos != i - 1)
								{
									Console.WriteLine($"Adjusted position to {instruction1} on {instruction}");
								}

								if (pos >= 0)
								{
									switch (instruction1.InstructionType)
									{
										case CPUInstructionEnum.CMP:
											break;

										case CPUInstructionEnum.TEST:
											break;

										case CPUInstructionEnum.AND:
											break;

										case CPUInstructionEnum.OR:
											break;

										case CPUInstructionEnum.XOR:
											break;

										case CPUInstructionEnum.ADD:
											break;

										case CPUInstructionEnum.SUB:
											break;

										case CPUInstructionEnum.INC:
											break;

										case CPUInstructionEnum.DEC:
											break;

										case CPUInstructionEnum.INT:
											if (instruction.Parameters[0].Type == CPUParameterTypeEnum.Condition &&
												((condition = (CPUJumpConditionEnum)instruction.Parameters[0].Value) == CPUJumpConditionEnum.B ||
												condition == CPUJumpConditionEnum.AE))
											{
												Console.WriteLine($"'{instruction1}' before '{instruction}' in function {this.parentSegment.Name}.{this.Name}, 0x{fnAddress:x}");
											}
											else
											{
												Console.WriteLine($"Instruction '{instruction1}' precedes '{instruction}' in function {this.parentSegment.Name}.{this.Name}, 0x{fnAddress:x}");
											}
											break;

										default:
											Console.WriteLine($"Instruction '{instruction1}' precedes '{instruction}' in function {this.parentSegment.Name}.{this.Name}, 0x{fnAddress:x}");
											break;
									}
								}
							}
							else if (instruction.Label)
							{
								Console.WriteLine($"Label on Jcc instruction in function {this.parentSegment.Name}.{this.Name}, 0x{fnAddress:x}");
							}
							else
							{
								throw new Exception($"Jcc instruction is first in function {this.parentSegment.Name}.{this.Name}, 0x{fnAddress:x}");
							}
							break;
					}
				}
			}
			#endregion

			#region Process calls to other functions
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				CPUInstruction instruction = this.asmInstructions[i];
				CPUParameter parameter;
				ProgramFunction? function;

				switch (instruction.InstructionType)
				{
					case CPUInstructionEnum.CALL:
						parameter = instruction.Parameters[0];

						if (parameter.Type == CPUParameterTypeEnum.Immediate)
						{
							function = this.parentSegment.ParentProgram.FindFunction(0, this.parentSegment.CPUSegment, (ushort)parameter.Value);

							if (function == null)
							{
								// function is not yet defined, define it
								function = DisassembleCallee(0, this.parentSegment.CPUSegment, (ushort)parameter.Value);
							}

							function?.ReferenceFunctions.Add(this);
						}
						break;

					case CPUInstructionEnum.CALLF:
						parameter = instruction.Parameters[0];
						if (parameter.Type == CPUParameterTypeEnum.SegmentOffset)
						{
							function = this.parentSegment.ParentProgram.FindFunction(0, parameter.Segment, (ushort)parameter.Value);

							if (function == null)
							{
								// function is not yet defined, define it
								function = DisassembleCallee(0, parameter.Segment, (ushort)parameter.Value);
							}

							function?.ReferenceFunctions.Add(this);
						}
						break;

					case CPUInstructionEnum.CallOverlay:
						if (instruction.Parameters[0].Value == 0)
						{
							throw new Exception("Overlay manager references overlay 0");
						}

						function = this.parentSegment.ParentProgram.FindFunction((ushort)instruction.Parameters[0].Value, 0, (ushort)instruction.Parameters[1].Value);

						if (function == null)
						{
							// function is not yet defined, define it
							function = DisassembleCallee((ushort)instruction.Parameters[0].Value, 0, (ushort)instruction.Parameters[1].Value);
						}

						function?.ReferenceFunctions.Add(this);
						break;

					case CPUInstructionEnum.JMPF:
						//throw new Exception("Jump to a far address");
						/*parameter = instruction.Parameters[0];
						if (parameter.Type == CPUInstructionParameterTypeEnum.SegmentOffset)
						{
							function = decompiler.FindFunction(0, parameter.Segment, (ushort)parameter.Value);
							if (function == null)
							{
								// function is not yet defined, define it
								decompiler.Disassemble(0, parameter.Segment, (ushort)parameter.Value, null);
								//function = decompiler.GetFunction(parameter.Segment, (ushort)parameter.Value, this.uiStreamOffset);
							}
						}*/
						break;
				}
			}
			#endregion
		}

		private CPUJumpConditionEnum NegateCondition(CPUJumpConditionEnum condition)
		{
			switch (condition)
			{
				case CPUJumpConditionEnum.O:
					return CPUJumpConditionEnum.NO;
				case CPUJumpConditionEnum.NO:
					return CPUJumpConditionEnum.O;
				case CPUJumpConditionEnum.B:
					return CPUJumpConditionEnum.AE;
				case CPUJumpConditionEnum.AE:
					return CPUJumpConditionEnum.B;
				case CPUJumpConditionEnum.E:
					return CPUJumpConditionEnum.NE;
				case CPUJumpConditionEnum.NE:
					return CPUJumpConditionEnum.E;
				case CPUJumpConditionEnum.BE:
					return CPUJumpConditionEnum.A;
				case CPUJumpConditionEnum.A:
					return CPUJumpConditionEnum.BE;
				case CPUJumpConditionEnum.S:
					return CPUJumpConditionEnum.NS;
				case CPUJumpConditionEnum.NS:
					return CPUJumpConditionEnum.S;
				case CPUJumpConditionEnum.P:
					return CPUJumpConditionEnum.NP;
				case CPUJumpConditionEnum.NP:
					return CPUJumpConditionEnum.P;
				case CPUJumpConditionEnum.L:
					return CPUJumpConditionEnum.GE;
				case CPUJumpConditionEnum.GE:
					return CPUJumpConditionEnum.L;
				case CPUJumpConditionEnum.LE:
					return CPUJumpConditionEnum.G;
				case CPUJumpConditionEnum.G:
					return CPUJumpConditionEnum.LE;
			}

			return CPUJumpConditionEnum.Undefined;
		}

		public CPUInstruction? GetInstructionByOffset(ushort offset)
		{
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				if (this.asmInstructions[i].Offset == offset)
				{
					return this.asmInstructions[i];
				}
			}

			return null;
		}

		public int GetInstructionPositionByOffset(ushort offset)
		{
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				if (this.asmInstructions[i].Offset == offset)
				{
					return i;
				}
			}

			return -1;
		}

		public CPUInstruction? GetInstructionByLinearAddress(uint address)
		{
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				if (this.asmInstructions[i].LinearAddress == address)
				{
					return this.asmInstructions[i];
				}
			}

			return null;
		}

		/// <summary>
		/// Marks the instruction at a linear address as a jump target. The address can fall outside
		/// the instructions we have, into an overlay that isn't loaded or code that hasn't been
		/// disassembled, in which case there is nothing to mark.
		/// </summary>
		/// <returns>false when the address isn't one of this function's instructions</returns>
		private bool MarkLabel(uint address)
		{
			int position = GetInstructionPositionByLinearAddress(address);

			if (position < 0)
			{
				return false;
			}

			CPUInstruction target = this.asmInstructions[position];

			target.Label = true;
			target.ReferenceCount++;

			return true;
		}

		/// <summary>
		/// Disassembles a function this one calls.
		///
		/// A callee that fails to disassemble still has to exist as a function: the writer resolves
		/// every call it emits, so letting the failure escape would abandon the rest of this
		/// function's calls and leave the writer unable to name them. The failed callee is already
		/// registered by the time it throws, so it is returned empty rather than not at all.
		/// </summary>
		private ProgramFunction? DisassembleCallee(ushort overlay, ushort segment, ushort offset)
		{
			try
			{
				return this.parentSegment.ParentProgram.Disassemble(overlay, segment, offset, null);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Warning, can't disassemble {overlay:x2}:{segment:x4}:{offset:x4} " +
					$"called from {this.parentSegment.Name}.{this.Name}: {ex.Message}");

				return this.parentSegment.ParentProgram.FindFunction(overlay, segment, offset);
			}
		}

		/// <summary>
		/// True when <see cref="Disassemble"/> threw. Such a function is still registered, so calls
		/// to it can be resolved by name, but it isn't written out: its instruction list stops
		/// wherever the failure happened and its jump targets no longer all exist.
		/// </summary>
		public bool DisassemblyFailed
		{
			get => this.disassemblyFailed;
			set => this.disassemblyFailed = value;
		}

		public int GetInstructionPositionByLinearAddress(uint address)
		{
			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				if (this.asmInstructions[i].LinearAddress == address)
				{
					return i;
				}
			}

			return -1;
		}

		public int AddInstruction(int ordinal, CPUInstruction instruction)
		{
			bool found = false;
			int position = -1;

			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				if (this.asmInstructions[i].InstructionOrdinal == ordinal)
				{
					found = true;
					position = i;
				}
			}

			if (found)
			{
				// Insert instruction
				for (int i = 0; i < this.asmInstructions.Count; i++)
				{
					if (this.asmInstructions[i].InstructionOrdinal >= ordinal)
					{
						this.asmInstructions[i].InstructionOrdinal++;
					}
				}

				instruction.InstructionOrdinal = ordinal;
				this.asmInstructions.Insert(position, instruction);
			}
			else
			{
				// Append instruction
				for (int i = 0; i < this.asmInstructions.Count; i++)
				{
					if (this.asmInstructions[i].InstructionOrdinal >= ordinal)
					{
						ordinal = this.asmInstructions[i].InstructionOrdinal;
					}
				}

				ordinal++;
				instruction.InstructionOrdinal = ordinal;
				this.asmInstructions.Add(instruction);
			}

			return ordinal;
		}

		public bool RemoveInstruction(int ordinal)
		{
			bool found = false;
			int position = -1;

			for (int i = 0; i < this.asmInstructions.Count; i++)
			{
				if (this.asmInstructions[i].InstructionOrdinal == ordinal)
				{
					found = true;
					position = i;
				}
			}

			if (found)
			{
				this.asmInstructions.RemoveAt(position);

				for (int i = 0; i < this.asmInstructions.Count; i++)
				{
					if (this.asmInstructions[i].InstructionOrdinal >= ordinal)
					{
						this.asmInstructions[i].InstructionOrdinal--;
					}
				}
			}

			return found;
		}

		public ILLocalVariableReference GetNewLocalVariable(ILValueType valueType)
		{
			ILVariable newVariable = new ILVariable(this, valueType, this.localVariablePosition);

			this.localVariablePosition++;

			return new ILLocalVariableReference(this, newVariable.Offset);
		}

		private ushort ReadWord(MemoryStream stream)
		{
			int byte0 = stream.ReadByte();
			int byte1 = stream.ReadByte();

			byte0 &= 0xff;
			byte1 &= 0xff;

			return (ushort)((byte1 << 8) | byte0);
		}

		public void WriteAsmCS(StreamWriter writer, int tabLevel, int verbosity)
		{
			bool retInstruction = false;

			writer.WriteLine();
			writer.Write($"{GetTabs(tabLevel)}public {this.returnValue.ValueType.CSDeclaration} {this.Name}(");
			
			ILVariable[] parameters = this.localParameters.Values.ToArray();
			Array.Sort(parameters, (item1, item2) => item1.Offset.CompareTo(item2.Offset));

			for (int k = 0; k < parameters.Length; k++)
			{
				if (k > 0)
					writer.Write(", ");
				writer.Write(parameters[k].CSDeclaration);
			}

			writer.WriteLine(")");
			writer.WriteLine($"{GetTabs(tabLevel)}{{");

			writer.WriteLine($"{GetTabs(tabLevel + 1)}// This function is referenced {this.referenceFunctions.Count} time(s)");

			if (this.isLibraryFunction)
			{
				writer.WriteLine($"{GetTabs(tabLevel + 1)}// Will not disassemble library function at 0x{this.fnOffset:x}");
			}
			else
			{
				if (this.flowGraph != null)
				{
					writer.WriteLine($"{GetTabs(tabLevel + 1)}// {(this.flowGraph.HasBPFrame ? "Standard C frame" : "Assembly")}");
				}

				if (verbosity > 0)
				{
					writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Log.EnterBlock(\"'{this.Name}'({this.functionOptions.ToString()}) at {this.parentSegment.Name}:0x{this.fnOffset:x}\");");
					writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CS.UInt16 = 0x{this.parentSegment.Segment:x4}; // set this function segment");
				}
				else
				{
					writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Log.EnterBlock(\"'{this.Name}'({this.functionOptions.ToString()}) at 0x{this.fnOffset:x}\");");
				}

				if (this.localVariables.Count > 0)
				{
					writer.WriteLine();
					writer.WriteLine($"{GetTabs(tabLevel + 1)}// Local variables");

					ILVariable[] variables = this.localVariables.Values.ToArray();
					Array.Sort(variables, (item1, item2) => item1.Offset.CompareTo(item2.Offset));

					for (int k = 0; k < variables.Length; k++)
					{
						writer.WriteLine($"{GetTabs(tabLevel + 1)}{variables[k].CSDeclaration};");
					}
				}

				writer.WriteLine();
				writer.WriteLine($"{GetTabs(tabLevel + 1)}// function body");

				int startPos = 0;

				while (startPos < this.asmInstructions.Count && this.asmInstructions[startPos].InstructionType == CPUInstructionEnum.Remark)
				{
					startPos++;
				}

				if (this.asmInstructions.Count > 0 && this.fnEntryPoint != this.asmInstructions[startPos].LinearAddress)
				{
					writer.WriteLine($"{GetTabs(tabLevel + 1)}goto {GetInstructionByLinearAddress(this.fnEntryPoint)?.LabelName};");
				}

				for (int k = 0; k < this.asmInstructions.Count; k++)
				{
					// writer.WriteLine("{GetTabs(tabLevel)}{0}\t{1}", function.Instructions[j].Location.ToString(), function.Instructions[j]);
					CPUInstruction instruction = this.asmInstructions[k];

					if (instruction.Label)
					{
						writer.WriteLine();
						if (verbosity > 0)
						{
							writer.WriteLine($"{GetTabs(tabLevel)}{instruction.LabelName}: // 0x{instruction.Offset:x}");
						}
						else
						{
							writer.WriteLine($"{GetTabs(tabLevel)}{instruction.LabelName}:");
						}
					}

					uint uiOffset = 0;
					uint address = 0;
					CPUParameter parameter;
					ProgramFunction? function1;
					int instructionIndex;

					switch (instruction.InstructionType)
					{
						case CPUInstructionEnum.ADC:
						case CPUInstructionEnum.ADD:
						case CPUInstructionEnum.AND:
						case CPUInstructionEnum.OR:
						case CPUInstructionEnum.SBB:
						case CPUInstructionEnum.SUB:
						case CPUInstructionEnum.XOR:
							parameter = instruction.Parameters[0];
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, string.Format("this.oCPU.{0}{1}({2}, {3})",
								instruction.InstructionType.ToString(), instruction.OperandSize.ToString(),
								parameter.ToSourceCSTextMZ(instruction.OperandSize), instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize))));
							break;

						case CPUInstructionEnum.DEC:
						case CPUInstructionEnum.INC:
						case CPUInstructionEnum.NEG:
						case CPUInstructionEnum.NOT:
							parameter = instruction.Parameters[0];
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, string.Format("this.oCPU.{0}{1}({2})",
								instruction.InstructionType.ToString(), instruction.OperandSize.ToString(), parameter.ToSourceCSTextMZ(instruction.OperandSize))));
							break;

						case CPUInstructionEnum.DAS:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.DAS();");
							break;

						case CPUInstructionEnum.AAA:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.AAA();");
							break;

						case CPUInstructionEnum.SAR:
						case CPUInstructionEnum.SHL:
						case CPUInstructionEnum.SHR:
						case CPUInstructionEnum.RCR:
						case CPUInstructionEnum.RCL:
						case CPUInstructionEnum.ROL:
						case CPUInstructionEnum.ROR:
							parameter = instruction.Parameters[0];
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, string.Format("this.oCPU.{0}{1}({2}, {3})",
								instruction.InstructionType.ToString(), instruction.OperandSize.ToString(),
								parameter.ToSourceCSTextMZ(instruction.OperandSize), instruction.Parameters[1].ToSourceCSTextMZ(instruction.Parameters[1].Size))));
							break;

						case CPUInstructionEnum.SHLD:
						case CPUInstructionEnum.SHRD:
							parameter = instruction.Parameters[0];
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, string.Format("this.oCPU.{0}{1}({2}, {3}, {4})",
								instruction.InstructionType.ToString(), instruction.OperandSize.ToString(),
								parameter.ToSourceCSTextMZ(instruction.OperandSize), instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize),
								instruction.Parameters[2].ToSourceCSTextMZ(instruction.Parameters[2].Size))));
							break;

						case CPUInstructionEnum.CBW:
							if (instruction.OperandSize == CPUParameterSizeEnum.UInt16)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CBW();");
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CWDE();");
							}
							break;

						case CPUInstructionEnum.CWD:
							if (instruction.OperandSize == CPUParameterSizeEnum.UInt16)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CWD();");
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CDQ();");
							}
							break;

						case CPUInstructionEnum.CMPS:
							writer.Write($"{GetTabs(tabLevel + 1)}this.oCPU.");
							if (instruction.RepPrefix == CPUInstructionPrefixEnum.REPE || instruction.RepPrefix == CPUInstructionPrefixEnum.REPNE)
							{
								writer.WriteLine("{0}_CMPS{1}(this.oCPU.ES, this.oCPU.DI, {2}, this.oCPU.SI);",
									instruction.RepPrefix.ToString(), instruction.OperandSize.ToString(), instruction.GetDefaultDataSegmentTextMZ());
							}
							else
							{
								writer.WriteLine("CMPS{0}(this.oCPU.ES, this.oCPU.DI, {1}, this.oCPU.SI);",
									instruction.OperandSize.ToString(), instruction.GetDefaultDataSegmentTextMZ());
							}
							break;

						case CPUInstructionEnum.CMP:
						case CPUInstructionEnum.TEST:
							parameter = instruction.Parameters[0];
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.{instruction.InstructionType.ToString()}{instruction.OperandSize.ToString()}" +
								$"({parameter.ToSourceCSTextMZ(instruction.OperandSize)}, {instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize)});");
							break;

						case CPUInstructionEnum.DIV:
						case CPUInstructionEnum.IDIV:
							if (instruction.OperandSize == CPUParameterSizeEnum.UInt8)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.{instruction.InstructionType.ToString()}{instruction.OperandSize.ToString()}" +
									$"(this.oCPU.AX, {instruction.Parameters[0].ToSourceCSTextMZ(instruction.OperandSize)});");
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.{instruction.InstructionType.ToString()}{instruction.OperandSize.ToString()}" +
									$"(this.oCPU.AX, this.oCPU.DX, {instruction.Parameters[0].ToSourceCSTextMZ(instruction.OperandSize)});");
							}
							break;

						case CPUInstructionEnum.MUL:
							if (instruction.OperandSize == CPUParameterSizeEnum.UInt8)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.{instruction.InstructionType.ToString()}{instruction.OperandSize.ToString()}" +
									$"(this.oCPU.AX, {instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize)}," +
									$"{instruction.Parameters[2].ToSourceCSTextMZ(instruction.OperandSize)});");
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.{instruction.InstructionType.ToString()}{instruction.OperandSize.ToString()}" +
									$"(this.oCPU.AX, this.oCPU.DX, {instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize)}, " +
									$"{instruction.Parameters[2].ToSourceCSTextMZ(instruction.OperandSize)});");
							}
							break;

						case CPUInstructionEnum.IMUL:
							parameter = instruction.Parameters[0];
							writer.Write($"{GetTabs(tabLevel + 1)}");

							if (parameter.Type == CPUParameterTypeEnum.Register)
							{
								if (parameter.RegisterValue == CPURegisterEnum.AX_DX)
								{
									writer.WriteLine($"this.oCPU.{instruction.InstructionType.ToString()}{instruction.OperandSize.ToString()}(" +
										"this.oCPU.AX, this.oCPU.DX, " +
										$"{instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize)}, " +
										$"{instruction.Parameters[2].ToSourceCSTextMZ(instruction.OperandSize)});");
								}
								else if (parameter.RegisterValue == CPURegisterEnum.AX)
								{
									writer.WriteLine($"this.oCPU.{instruction.InstructionType.ToString()}{instruction.OperandSize.ToString()}(" +
										"this.oCPU.AX, " +
										$"{instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize)}, " +
										$"{instruction.Parameters[2].ToSourceCSTextMZ(instruction.OperandSize)});");
								}
								else
								{
									writer.WriteLine($"this.oCPU.{instruction.InstructionType.ToString()}{instruction.OperandSize.ToString()}(" +
										$"this.oCPU.{parameter.RegisterValue.ToString()}, " +
										$"{instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize)}, " +
										$"{instruction.Parameters[2].ToSourceCSTextMZ(instruction.OperandSize)});");
								}
							}
							else
							{
								throw new Exception("Invalid IMUL instruction");
							}
							break;

						case CPUInstructionEnum.LDS:
							parameter = instruction.Parameters[1];
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// LDS");
							writer.WriteLine($"{GetTabs(tabLevel + 1)}{instruction.Parameters[0].ToDestinationCSTextMZ(instruction.OperandSize, parameter.ToSourceCSTextMZ(instruction.OperandSize))}");
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.DS.{instruction.OperandSize.ToString()} = this.oCPU.Read{instruction.OperandSize.ToString()}" +
								$"({parameter.GetSegmentTextMZ()}, (ushort)({parameter.ToCSTextMZ(instruction.OperandSize)} + 2));");
							break;

						case CPUInstructionEnum.LES:
							parameter = instruction.Parameters[1];
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// LES");
							writer.WriteLine($"{GetTabs(tabLevel + 1)}{instruction.Parameters[0].ToDestinationCSTextMZ(instruction.OperandSize, parameter.ToSourceCSTextMZ(instruction.OperandSize))}");
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.ES.{instruction.OperandSize.ToString()} = this.oCPU.Read{instruction.OperandSize.ToString()}" +
								$"({parameter.GetSegmentTextMZ()}, (ushort)({parameter.ToCSTextMZ(instruction.OperandSize)} + 2));");
							break;

						case CPUInstructionEnum.LEA:
							parameter = instruction.Parameters[1];
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// LEA");
							writer.Write($"{GetTabs(tabLevel + 1)}{instruction.Parameters[0].ToDestinationCSTextMZ(instruction.OperandSize, parameter.ToCSTextMZ(instruction.OperandSize))}");
							writer.WriteLine();
							break;

						case CPUInstructionEnum.LODS:
							writer.Write($"{GetTabs(tabLevel + 1)}this.oCPU.");
							if (instruction.RepPrefix == CPUInstructionPrefixEnum.REPE || instruction.RepPrefix == CPUInstructionPrefixEnum.REPNE)
							{
								writer.WriteLine("{0}_LODS{1}();", instruction.RepPrefix.ToString(), instruction.OperandSize.ToString());
							}
							else
							{
								writer.WriteLine("LODS{0}();", instruction.OperandSize.ToString());
							}
							break;

						case CPUInstructionEnum.MOV:
							parameter = instruction.Parameters[1];
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.Write(instruction.Parameters[0].ToDestinationCSTextMZ(instruction.OperandSize, parameter.ToSourceCSTextMZ(instruction.OperandSize)));
							writer.WriteLine();
							break;

						case CPUInstructionEnum.MOVS:
							writer.Write($"{GetTabs(tabLevel + 1)}this.oCPU.");
							if (instruction.RepPrefix == CPUInstructionPrefixEnum.REPE || instruction.RepPrefix == CPUInstructionPrefixEnum.REPNE)
							{
								writer.WriteLine("{0}_MOVS{1}({2}, this.oCPU.SI, this.oCPU.ES, this.oCPU.DI, this.oCPU.CX);",
									instruction.RepPrefix.ToString(), instruction.OperandSize.ToString(), instruction.GetDefaultDataSegmentTextMZ());
							}
							else
							{
								writer.WriteLine("MOVS{0}({1}, this.oCPU.SI, this.oCPU.ES, this.oCPU.DI);",
									instruction.OperandSize.ToString(), instruction.GetDefaultDataSegmentTextMZ());
							}
							break;

						case CPUInstructionEnum.MOVSX:
						case CPUInstructionEnum.MOVZX:
							parameter = instruction.Parameters[0];
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, string.Format("this.oCPU.{0}{1}({2})",
								instruction.InstructionType.ToString(), instruction.OperandSize.ToString(),
								instruction.Parameters[1].ToSourceCSTextMZ(instruction.Parameters[1].Size))));
							break;

						case CPUInstructionEnum.NOP:
							// ignore this instruction
							break;

						case CPUInstructionEnum.WAIT:
							// ignore this instruction
							break;

						case CPUInstructionEnum.ENTER:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Enter();");
							break;

						case CPUInstructionEnum.LEAVE:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Leave();");
							break;

						// stack instructions
						case CPUInstructionEnum.POP:
							parameter = instruction.Parameters[0];
							/*Console.WriteLine("POP {0} in {1}.{2} ", instruction.Parameters[0].ToString(),
								segment.Namespace, function.Name);*/
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, string.Format("this.oCPU.POP{0}()",
								instruction.OperandSize.ToString())));
							break;

						case CPUInstructionEnum.POPA:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.POPA{instruction.OperandSize.ToString()}();");
							break;

						case CPUInstructionEnum.POPF:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.POPFUInt16();");
							break;

						case CPUInstructionEnum.PUSH:
							parameter = instruction.Parameters[0];
							writer.Write($"{GetTabs(tabLevel + 1)}this.oCPU.PUSH{instruction.OperandSize.ToString()}({parameter.ToSourceCSTextMZ(instruction.OperandSize)});");
							writer.WriteLine();
							break;

						case CPUInstructionEnum.PUSHA:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PUSHA{instruction.OperandSize.ToString()}();");
							break;

						case CPUInstructionEnum.PUSHF:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PUSHFUInt16();");
							break;

						case CPUInstructionEnum.CLD:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Flags.D = false;");
							break;

						case CPUInstructionEnum.STD:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Flags.D = true;");
							break;

						case CPUInstructionEnum.CLC:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Flags.C = false;");
							break;

						case CPUInstructionEnum.STC:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Flags.C = true;");
							break;

						case CPUInstructionEnum.CMC:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Flags.C = !this.oCPU.Flags.C;");
							break;

						case CPUInstructionEnum.CLI:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CLI();");
							break;

						case CPUInstructionEnum.STI:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.STI();");
							break;

						case CPUInstructionEnum.SCAS:
							writer.Write($"{GetTabs(tabLevel + 1)}this.oCPU.");
							if (instruction.RepPrefix == CPUInstructionPrefixEnum.REPE || instruction.RepPrefix == CPUInstructionPrefixEnum.REPNE)
							{
								writer.WriteLine("{0}_SCAS{1}();",
									instruction.RepPrefix.ToString(), instruction.OperandSize.ToString());
							}
							else
							{
								writer.WriteLine("SCAS{0}();", instruction.OperandSize.ToString());
							}
							break;

						case CPUInstructionEnum.STOS:
							writer.Write($"{GetTabs(tabLevel + 1)}this.oCPU.");
							if (instruction.RepPrefix == CPUInstructionPrefixEnum.REPE || instruction.RepPrefix == CPUInstructionPrefixEnum.REPNE)
							{
								writer.WriteLine("{0}_STOS{1}();",
									instruction.RepPrefix.ToString(), instruction.OperandSize.ToString());
							}
							else
							{
								writer.WriteLine("STOS{0}();", instruction.OperandSize.ToString());
							}
							break;

						case CPUInstructionEnum.XCHG:
							parameter = instruction.Parameters[0];
							//writer.WriteLine("{GetTabs(tabLevel)}// XCHG");
							writer.Write($"{GetTabs(tabLevel + 1)}");
							if (instruction.OperandSize == CPUParameterSizeEnum.UInt8)
							{
								writer.WriteLine("this.oCPU.Temp.Low{0} = {1};", instruction.OperandSize.ToString(), parameter.ToSourceCSTextMZ(instruction.OperandSize));
							}
							else
							{
								writer.WriteLine("this.oCPU.Temp.{0} = {1};", instruction.OperandSize.ToString(), parameter.ToSourceCSTextMZ(instruction.OperandSize));
							}
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, instruction.Parameters[1].ToSourceCSTextMZ(instruction.OperandSize)));
							parameter = instruction.Parameters[1];
							writer.Write($"{GetTabs(tabLevel + 1)}");
							if (instruction.OperandSize == CPUParameterSizeEnum.UInt8)
							{
								writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, string.Format("this.oCPU.Temp.Low{0}", instruction.OperandSize.ToString())));
							}
							else
							{
								writer.WriteLine(parameter.ToDestinationCSTextMZ(instruction.OperandSize, string.Format("this.oCPU.Temp.{0}", instruction.OperandSize.ToString())));
							}
							break;

						case CPUInstructionEnum.XLAT:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.XLATUInt8({instruction.GetDefaultDataSegmentTextMZ()});");
							break;

						// input and output port instructions
						case CPUInstructionEnum.IN:
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine("{0}",
								instruction.Parameters[0].ToDestinationCSTextMZ(instruction.Parameters[0].Size,
								string.Format("this.oCPU.IN{0}({1})",
								instruction.OperandSize.ToString(),
								instruction.Parameters[1].ToSourceCSTextMZ(instruction.Parameters[1].Size))));
							break;
						case CPUInstructionEnum.OUT:
							writer.Write($"{GetTabs(tabLevel + 1)}");
							writer.WriteLine("this.oCPU.OUT{0}({1}, {2});",
								instruction.OperandSize.ToString(),
								instruction.Parameters[0].ToSourceCSTextMZ(instruction.Parameters[0].Size),
								instruction.Parameters[1].ToSourceCSTextMZ(instruction.Parameters[1].Size));
							break;

						case CPUInstructionEnum.OUTS:
							writer.Write($"{GetTabs(tabLevel + 1)}this.oCPU.");
							if (instruction.RepPrefix == CPUInstructionPrefixEnum.REPE || instruction.RepPrefix == CPUInstructionPrefixEnum.REPNE)
							{
								writer.WriteLine("{0}_{1}{2}({3}, this.oCPU.SI, this.oCPU.CX);",
									instruction.RepPrefix.ToString(),
									instruction.InstructionType.ToString(), instruction.OperandSize.ToString(),
									instruction.GetDefaultDataSegmentTextMZ());
							}
							else
							{
								writer.WriteLine("{0}{1}({2}, this.oCPU.SI);",
									instruction.InstructionType.ToString(), instruction.OperandSize.ToString(),
									instruction.GetDefaultDataSegmentTextMZ());
							}
							break;

						// special syntetic functions
						case CPUInstructionEnum.WordsToDword:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.{instruction.Parameters[0].RegisterValue.ToString()}.UInt32 = " +
								$"(uint)(((uint)this.oCPU.{instruction.Parameters[1].RegisterValue.ToString()}.UInt16 << 16) | " +
								$"(uint)this.oCPU.{instruction.Parameters[2].RegisterValue.ToString()}.UInt16);");
							break;

						// flow control instructions
						case CPUInstructionEnum.SWITCH:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}switch({instruction.Parameters[0].ToSourceCSTextMZ(instruction.OperandSize)})");
							writer.WriteLine($"{GetTabs(tabLevel + 1)}{{");
							for (int l = 1; l < instruction.Parameters.Count; l++)
							{
								parameter = instruction.Parameters[l];
								instructionIndex = this.GetInstructionPositionByOffset((ushort)parameter.Displacement);

								if (instructionIndex < 0)
								{
									throw new Exception($"Can't find instruction in function {this.parentSegment.Name}.{this.Name} at offset 0x{parameter.Displacement:x4}");
								}
								else
								{
									writer.WriteLine($"{GetTabs(tabLevel + 2)}case {parameter.Value}:");
									writer.WriteLine($"{GetTabs(tabLevel + 3)}goto {this.asmInstructions[instructionIndex].LabelName};");
								}
							}
							writer.WriteLine($"{GetTabs(tabLevel + 1)}}}");
							break;

						case CPUInstructionEnum.Jcc:
							uiOffset = instruction.Parameters[1].Value;
							instructionIndex = this.GetInstructionPositionByOffset((ushort)uiOffset);

							if (instructionIndex < 0)
							{
								throw new Exception($"Can't find instruction in function {this.parentSegment.Name}.{this.Name} at offset 0x{uiOffset:x4}");
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}if (this.oCPU.Flags.{((CPUJumpConditionEnum)instruction.Parameters[0].Value).ToString()}) goto " +
									$"{this.asmInstructions[instructionIndex].LabelName};");
							}
							break;

						case CPUInstructionEnum.IIf:
							parameter = instruction.Parameters[0];
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// IIf");
							writer.WriteLine($"{GetTabs(tabLevel + 1)}{instruction.Parameters[0].ToSourceCSTextMZ(instruction.OperandSize)} = "+
								$"((this.oCPU.Flags.{((CPUJumpConditionEnum)instruction.Parameters[1].Value).ToString()}) ? " +
								$"{instruction.Parameters[2].ToSourceCSTextMZ(instruction.OperandSize)} : {instruction.Parameters[3].ToSourceCSTextMZ(instruction.OperandSize)});");
							break;

						case CPUInstructionEnum.JCXZ:
							uiOffset = (uint)instruction.Parameters[0].Value;
							instructionIndex = this.GetInstructionPositionByOffset((ushort)uiOffset);

							if (instructionIndex < 0)
							{
								throw new Exception($"Can't find instruction in function {this.parentSegment.Name}.{this.Name} at offset 0x{uiOffset:x4}");
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}if (this.oCPU.CX.UInt16 == 0) goto {this.asmInstructions[instructionIndex].LabelName};");
							}
							break;

						case CPUInstructionEnum.LOOP:
							uiOffset = (uint)instruction.Parameters[0].Value;
							instructionIndex = this.GetInstructionPositionByOffset((ushort)uiOffset);

							if (instructionIndex < 0)
							{
								throw new Exception($"Can't find instruction in function {this.parentSegment.Name}.{this.Name} at offset 0x{uiOffset:x4}");
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}if (this.oCPU.LoopUInt16(this.oCPU.CX)) goto {this.asmInstructions[instructionIndex].LabelName};");
							}
							break;

						case CPUInstructionEnum.JMP:
							parameter = instruction.Parameters[0];
							if (parameter.Type == CPUParameterTypeEnum.Immediate)
							{
								uiOffset = (uint)instruction.Parameters[0].Value;
								instructionIndex = this.GetInstructionPositionByOffset((ushort)uiOffset);

								if (instructionIndex < 0)
								{
									throw new Exception($"Can't find instruction in function {this.parentSegment.Name}.{this.Name} at offset 0x{uiOffset:x4}");
								}
								else
								{
									writer.WriteLine($"{GetTabs(tabLevel + 1)}goto {this.asmInstructions[instructionIndex].LabelName};");
								}
							}
							else if (parameter.Type == CPUParameterTypeEnum.Register)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}// Probably a switch statement - near jump to register value");
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.JmpUInt16({parameter.ToCSTextMZ(instruction.OperandSize)});");
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}// Probably a switch statement - near jump to indirect address");
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.JmpUInt16(this.oCPU.ReadUInt16({parameter.GetSegmentTextMZ()}, {parameter.ToCSTextMZ(instruction.OperandSize)}));");
							}
							break;

						case CPUInstructionEnum.JMPF:
							parameter = instruction.Parameters[0];
							if (parameter.Type == CPUParameterTypeEnum.SegmentOffset)
							{
								address = MainProgram.ToLinearAddress(parameter.Segment, parameter.Value);

								if (address == 0)
								{
									ProgramSegment segment = this.parentSegment;
									if (segment.GlobalVariables.ContainsKey(instruction.Offset + 1))
									{
										ILVariable variable = segment.GlobalVariables.GetValueByKey(instruction.Offset + 1);
										writer.WriteLine($"{GetTabs(tabLevel + 1)}this.{variable.Name}();");
									}
									else
									{
										writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.JmpUInt32(this.oCPU.ReadUInt32(this.oCPU.CS.UInt16, 0x{(instruction.Offset + 1):x}));");
									}
								}
								else
								{
									instructionIndex = this.GetInstructionPositionByLinearAddress(address);

									if (instructionIndex < 0)
									{
										// A far jump out of the function, not a jump to a label in
										// it. An overlay thunk ends in one of these, with an overlay
										// entry id where the address would be.
										writer.WriteLine($"{GetTabs(tabLevel + 1)}// far jump out of this function, to 0x{parameter.Segment:x4}:0x{parameter.Value:x4}");
									}
									else
									{
										writer.WriteLine($"{GetTabs(tabLevel + 1)}goto {this.asmInstructions[instructionIndex].LabelName};");
									}
								}
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.JmpUInt32(this.oCPU.ReadUInt32({parameter.GetSegmentTextMZ()}, {parameter.ToCSTextMZ(instruction.OperandSize)}));");
							}
							break;

						case CPUInstructionEnum.CALL:
							parameter = instruction.Parameters[0];
							if (verbosity > 0)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PushUInt16(0x{instruction.Offset + instruction.Bytes.Count:x4}); // stack management - push return offset");
								writer.WriteLine($"{GetTabs(tabLevel + 1)}// Instruction address 0x{instruction.Segment:x4}:0x{instruction.Offset:x4}, size: {instruction.Bytes.Count}");
							}
							else
							{
								//writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PushUInt16(0); // stack management - push return offset");
							}

							if (parameter.Type != CPUParameterTypeEnum.Immediate)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CallUInt16(this.oCPU.ReadUInt16({parameter.GetSegmentTextMZ()}, {parameter.ToCSTextMZ(instruction.OperandSize)}));");
							}
							else
							{
								function1 = this.parentSegment.ParentProgram.FindFunction(0, instruction.Segment, (ushort)parameter.Value);

								if (function1 != null)
								{
									if ((function1.FunctionOptions & ProgramFunctionOptionsEnum.Near) != ProgramFunctionOptionsEnum.Near &&
										(function1.FunctionOptions & ProgramFunctionOptionsEnum.Far) == ProgramFunctionOptionsEnum.Far)
									{
										Console.WriteLine($"Function '{function1.Name}' doesn't support near return");
									}

									if (this.parentSegment.Segment != function1.ParentSegment.Segment)
									{
										if ((this.functionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI)
										{
											writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.MSCAPI.{function1.Name}();");
										}
										else
										{
											writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.{function1.ParentSegment.Name}.{function1.Name}();");
										}
									}
									else
									{
										if (((this.functionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI &&
											(function1.FunctionOptions & ProgramFunctionOptionsEnum.CAPI) != ProgramFunctionOptionsEnum.CAPI) ||
											((this.functionOptions & ProgramFunctionOptionsEnum.CAPI) != ProgramFunctionOptionsEnum.CAPI &&
											(function1.FunctionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI))
										{
											if ((function1.FunctionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI)
											{
												writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.MSCAPI.{function1.Name}();");
											}
											else
											{
												writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.{function1.ParentSegment.Name}.{function1.Name}();");
											}
										}
										else
										{
											writer.WriteLine($"{GetTabs(tabLevel + 1)}{function1.Name}();");
										}
									}
								}
								else
								{
									// The callee failed to disassemble, so it was never defined. Leaving a
									// marker keeps the rest of this function readable instead of losing it.
									writer.WriteLine($"{GetTabs(tabLevel + 1)}// unresolved call to F0_{parameter.Segment:x4}_{parameter.Value:x4}");
								}
							}

							// all calls in medium memory model are far
							if (verbosity > 0)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PopUInt16(); // stack management - pop return offset");
								//writer.WriteLine("{GetTabs(tabLevel)+1}this.oCPU.CS.UInt16 = 0x{0:x4}; // restore this function segment", function.Segment);
							}
							break;

						case CPUInstructionEnum.CALLF:
							parameter = instruction.Parameters[0];
							if (verbosity > 0)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PushUInt16(this.oCPU.CS.UInt16); // stack management - push return segment");
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PushUInt16(0x{instruction.Offset + instruction.Bytes.Count:x4}); // stack management - push return offset");
								writer.WriteLine($"{GetTabs(tabLevel + 1)}// Instruction address 0x{instruction.Segment:x4}:0x{instruction.Offset:x4}, size: {instruction.Bytes.Count}");
							}
							else
							{
								//writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PushUInt32(0); // stack management - push return segment, offset");
							}

							if (parameter.Type == CPUParameterTypeEnum.SegmentOffset)
							{
								function1 = this.parentSegment.ParentProgram.FindFunction(0, parameter.Segment, (ushort)parameter.Value);
								if (function1 != null)
								{
									if ((function1.FunctionOptions & ProgramFunctionOptionsEnum.Far) != ProgramFunctionOptionsEnum.Far &&
										(function1.FunctionOptions & ProgramFunctionOptionsEnum.Near) == ProgramFunctionOptionsEnum.Near)
									{
										Console.WriteLine($"Function '{function1.Name}' doesn't support far return");
									}

									if (this.parentSegment.Segment != function1.ParentSegment.Segment)
									{
										if ((function1.FunctionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI)
										{
											writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.MSCAPI.{function1.Name}();");
										}
										else
										{
											writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.{function1.ParentSegment.Name}.{function1.Name}();");
										}
									}
									else
									{
										if (((this.functionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI &&
											(function1.FunctionOptions & ProgramFunctionOptionsEnum.CAPI) != ProgramFunctionOptionsEnum.CAPI) ||
											((this.functionOptions & ProgramFunctionOptionsEnum.CAPI) != ProgramFunctionOptionsEnum.CAPI &&
											(function1.FunctionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI))
										{
											if ((function1.FunctionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI)
											{
												writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.MSCAPI.{function1.Name}();");
											}
											else
											{
												writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.{function1.ParentSegment.Name}.{function1.Name}();");
											}
										}
										else
										{
											writer.WriteLine($"{GetTabs(tabLevel + 1)}{function1.Name}();");
										}
									}
								}
								else
								{
									// The callee failed to disassemble, so it was never defined. Leaving a
									// marker keeps the rest of this function readable instead of losing it.
									writer.WriteLine($"{GetTabs(tabLevel + 1)}// unresolved call to F0_{parameter.Segment:x4}_{parameter.Value:x4}");
								}
							}
							else
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CallUInt32(this.oCPU.ReadUInt32({parameter.GetSegmentTextMZ()}, {parameter.ToCSTextMZ(instruction.OperandSize)}));");
							}
							
							if (verbosity > 0)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PopUInt32(); // stack management - pop return offset and segment");
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CS.UInt16 = 0x{this.parentSegment.CPUSegment:x4}; // restore this function segment");
							}
							break;

						case CPUInstructionEnum.CallOverlay:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// Call to overlay");
							if (verbosity > 0)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PushUInt16(this.oCPU.CS.UInt16); // stack management - push return segment");
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PushUInt16(0x{instruction.Offset + instruction.Bytes.Count:x4}); // stack management - push return offset");
							}
							else
							{
								//writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PushUInt32(0); // stack management - push return segment, offset");
							}

							function1 = this.parentSegment.ParentProgram.FindFunction((ushort)instruction.Parameters[0].Value, 0, (ushort)instruction.Parameters[1].Value);
							if (function1 != null)
							{
								if ((function1.FunctionOptions & ProgramFunctionOptionsEnum.Far) != ProgramFunctionOptionsEnum.Far && (function1.FunctionOptions & ProgramFunctionOptionsEnum.Near) == ProgramFunctionOptionsEnum.Near)
								{
									Console.WriteLine($"Function '{function1.Name}' doesn't support far return");
								}

								if (this.parentSegment != function1.ParentSegment)
								{
									if ((function1.FunctionOptions & ProgramFunctionOptionsEnum.CAPI) == ProgramFunctionOptionsEnum.CAPI)
									{
										writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.MSCAPI.{function1.Name}();");
									}
									else
									{
										writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oParent.{function1.ParentSegment.Name}.{function1.Name}();");
									}
								}
								else
								{
									writer.WriteLine($"{GetTabs(tabLevel + 1)}{function1.Name}();");
								}
							}
							else
							{
								// as above, an overlay callee that failed to disassemble
								writer.WriteLine($"{GetTabs(tabLevel + 1)}// unresolved call to F{instruction.Parameters[0].Value}_0000_{instruction.Parameters[1].Value:x4}");
							}

							if (verbosity > 0)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.PopUInt32(); // stack management - pop return offset and segment");
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.CS.UInt16 = 0x{this.parentSegment.CPUSegment:x4}; // restore this function segment");
							}
							break;

						case CPUInstructionEnum.RET:
							writer.WriteLine();
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// Near return");
							if (instruction.Parameters.Count > 0)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.SP.UInt16 = this.oCPU.ADDUInt16(this.oCPU.SP.UInt16, 0x{instruction.Parameters[0].Value:x});");
							}
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Log.ExitBlock(\"'{this.Name}'\");");
							if (k != this.asmInstructions.Count - 1)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}return;");
							}
							retInstruction = true;
							break;

						case CPUInstructionEnum.RETF:
							writer.WriteLine();
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// Far return");
							if (instruction.Parameters.Count > 0)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.SP.UInt16 = this.oCPU.ADDUInt16(this.oCPU.SP.UInt16, 0x{instruction.Parameters[0].Value:x});");
							}
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Log.ExitBlock(\"'{this.Name}'\");");
							if (k != this.asmInstructions.Count - 1)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}return;");
							}
							retInstruction = true;
							break;

						case CPUInstructionEnum.IRET:
							writer.WriteLine();
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// IRET - Pop flags and Far return");
							writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Log.ExitBlock(\"'{this.Name}'\");");
							if (k != this.asmInstructions.Count - 1)
							{
								writer.WriteLine($"{GetTabs(tabLevel + 1)}return;");
							}
							retInstruction = true;
							break;

						case CPUInstructionEnum.INT:
							{
								uint intNum = instruction.Parameters[0].Value;
								// Try to find the AH value set before this INT to determine the DOS function
								int? ahValue = FindPrecedingAHValue(k);
								string intComment = GetInterruptComment((byte)intNum, ahValue);
								if (!string.IsNullOrEmpty(intComment))
								{
									writer.WriteLine($"{GetTabs(tabLevel + 1)}// {intComment}");
								}
								writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.INTUInt8(0x{intNum:x2});");
							}
							break;

						case CPUInstructionEnum.Remark:
							writer.WriteLine($"{GetTabs(tabLevel + 1)}// {instruction.Description}");
							break;

						default:
							//throw new Exception($"Unexpected instruction type: {instruction.InstructionType}");
							Console.WriteLine($"Unexpected instruction type '{instruction.InstructionType}' " +
								$"in function {this.parentSegment.Name}.{this.Name} at offset 0x{instruction.Offset:x4}");
							break;
					}
				}
				if (!retInstruction)
				{
					writer.WriteLine();
					writer.WriteLine($"{GetTabs(tabLevel + 1)}this.oCPU.Log.ExitBlock(\"'{this.Name}'\");");
				}
			}

			writer.WriteLine($"{GetTabs(tabLevel)}}}");
		}

		public static string ConditionToCSText(CPUJumpConditionEnum condition)
		{
			switch (condition)
			{
				case CPUJumpConditionEnum.B:
					return "<";
				case CPUJumpConditionEnum.AE:
					return ">=";
				case CPUJumpConditionEnum.E:
					return "==";
				case CPUJumpConditionEnum.NE:
					return "!=";
				case CPUJumpConditionEnum.BE:
					return "<=";
				case CPUJumpConditionEnum.A:
					return ">";
				case CPUJumpConditionEnum.L:
					return "<";
				case CPUJumpConditionEnum.GE:
					return ">=";
				case CPUJumpConditionEnum.LE:
					return "<=";
				case CPUJumpConditionEnum.G:
					return ">";
			}

			return "!!!";
		}

		/// <summary>
		/// Scan backwards from instruction index to find the last AH assignment value.
		/// </summary>
		private int? FindPrecedingAHValue(int fromIndex)
		{
			for (int j = fromIndex - 1; j >= 0 && j >= fromIndex - 10; j--)
			{
				CPUInstruction prev = this.asmInstructions[j];
				if (prev.InstructionType == CPUInstructionEnum.MOV && prev.Parameters.Count == 2)
				{
					CPUParameter dest = prev.Parameters[0];
					CPUParameter src = prev.Parameters[1];

					// MOV AH, imm8
					if (dest.Type == CPUParameterTypeEnum.Register &&
						dest.Value == (uint)CPURegisterEnum.AH &&
						src.Type == CPUParameterTypeEnum.Immediate)
					{
						return (int)src.Value;
					}

					// MOV AX, imm16 — AH is the high byte
					if (dest.Type == CPUParameterTypeEnum.Register &&
						dest.Value == (uint)CPURegisterEnum.AX &&
						src.Type == CPUParameterTypeEnum.Immediate)
					{
						return (int)(src.Value >> 8);
					}
				}
			}

			return null;
		}

		/// <summary>
		/// Returns a human-readable comment for a DOS/BIOS interrupt call.
		/// </summary>
		private static string GetInterruptComment(byte intNum, int? ahFunction)
		{
			switch (intNum)
			{
				case 0x10:
					if (ahFunction == null) return "INT 10h - BIOS Video Services";
					return (ahFunction.Value) switch
					{
						0x00 => "INT 10h/AH=00h - Set Video Mode",
						0x01 => "INT 10h/AH=01h - Set Cursor Shape",
						0x02 => "INT 10h/AH=02h - Set Cursor Position",
						0x03 => "INT 10h/AH=03h - Get Cursor Position",
						0x05 => "INT 10h/AH=05h - Set Active Display Page",
						0x06 => "INT 10h/AH=06h - Scroll Up Window",
						0x07 => "INT 10h/AH=07h - Scroll Down Window",
						0x08 => "INT 10h/AH=08h - Read Char and Attribute at Cursor",
						0x09 => "INT 10h/AH=09h - Write Char and Attribute at Cursor",
						0x0A => "INT 10h/AH=0Ah - Write Char at Cursor",
						0x0B => "INT 10h/AH=0Bh - Set Color Palette",
						0x0C => "INT 10h/AH=0Ch - Write Pixel",
						0x0D => "INT 10h/AH=0Dh - Read Pixel",
						0x0E => "INT 10h/AH=0Eh - Write Char in Teletype Mode",
						0x0F => "INT 10h/AH=0Fh - Get Current Video Mode",
						0x10 => "INT 10h/AH=10h - Set/Get Palette Registers",
						0x11 => "INT 10h/AH=11h - Character Generator",
						0x12 => "INT 10h/AH=12h - Alternate Select",
						0x13 => "INT 10h/AH=13h - Write String",
						_ => $"INT 10h/AH={ahFunction.Value:X2}h - BIOS Video Services"
					};

				case 0x11:
					return "INT 11h - BIOS Equipment List";

				case 0x12:
					return "INT 12h - BIOS Memory Size";

				case 0x13:
					if (ahFunction == null) return "INT 13h - BIOS Disk Services";
					return (ahFunction.Value) switch
					{
						0x00 => "INT 13h/AH=00h - Reset Disk System",
						0x01 => "INT 13h/AH=01h - Get Disk Status",
						0x02 => "INT 13h/AH=02h - Read Sectors",
						0x03 => "INT 13h/AH=03h - Write Sectors",
						0x04 => "INT 13h/AH=04h - Verify Sectors",
						0x05 => "INT 13h/AH=05h - Format Track",
						0x08 => "INT 13h/AH=08h - Get Drive Parameters",
						_ => $"INT 13h/AH={ahFunction.Value:X2}h - BIOS Disk Services"
					};

				case 0x16:
					if (ahFunction == null) return "INT 16h - BIOS Keyboard Services";
					return (ahFunction.Value) switch
					{
						0x00 => "INT 16h/AH=00h - Read Keyboard Character",
						0x01 => "INT 16h/AH=01h - Check Keyboard Buffer",
						0x02 => "INT 16h/AH=02h - Get Keyboard Shift Flags",
						_ => $"INT 16h/AH={ahFunction.Value:X2}h - BIOS Keyboard Services"
					};

				case 0x1A:
					if (ahFunction == null) return "INT 1Ah - BIOS Time Services";
					return (ahFunction.Value) switch
					{
						0x00 => "INT 1Ah/AH=00h - Read System Clock Counter",
						0x01 => "INT 1Ah/AH=01h - Set System Clock Counter",
						0x02 => "INT 1Ah/AH=02h - Read Real-Time Clock",
						_ => $"INT 1Ah/AH={ahFunction.Value:X2}h - BIOS Time Services"
					};

				case 0x21:
					if (ahFunction == null) return "INT 21h - DOS Services";
					return (ahFunction.Value) switch
					{
						0x00 => "INT 21h/AH=00h - Terminate Program",
						0x01 => "INT 21h/AH=01h - Read Character with Echo",
						0x02 => "INT 21h/AH=02h - Display Character",
						0x06 => "INT 21h/AH=06h - Direct Console I/O",
						0x07 => "INT 21h/AH=07h - Direct Console Input Without Echo",
						0x08 => "INT 21h/AH=08h - Console Input Without Echo",
						0x09 => "INT 21h/AH=09h - Display String",
						0x0A => "INT 21h/AH=0Ah - Buffered Keyboard Input",
						0x0B => "INT 21h/AH=0Bh - Check Keyboard Status",
						0x0C => "INT 21h/AH=0Ch - Flush Buffer and Read Input",
						0x0D => "INT 21h/AH=0Dh - Disk Reset",
						0x0E => "INT 21h/AH=0Eh - Select Disk",
						0x0F => "INT 21h/AH=0Fh - Open File (FCB)",
						0x10 => "INT 21h/AH=10h - Close File (FCB)",
						0x11 => "INT 21h/AH=11h - Find First File (FCB)",
						0x12 => "INT 21h/AH=12h - Find Next File (FCB)",
						0x13 => "INT 21h/AH=13h - Delete File (FCB)",
						0x19 => "INT 21h/AH=19h - Get Current Default Drive",
						0x1A => "INT 21h/AH=1Ah - Set Disk Transfer Address",
						0x25 => "INT 21h/AH=25h - Set Interrupt Vector",
						0x29 => "INT 21h/AH=29h - Parse Filename",
						0x2A => "INT 21h/AH=2Ah - Get System Date",
						0x2C => "INT 21h/AH=2Ch - Get System Time",
						0x30 => "INT 21h/AH=30h - Get DOS Version",
						0x33 => "INT 21h/AH=33h - Get/Set Break Flag",
						0x35 => "INT 21h/AH=35h - Get Interrupt Vector",
						0x36 => "INT 21h/AH=36h - Get Free Disk Space",
						0x38 => "INT 21h/AH=38h - Get/Set Country Info",
						0x39 => "INT 21h/AH=39h - Create Directory",
						0x3A => "INT 21h/AH=3Ah - Remove Directory",
						0x3B => "INT 21h/AH=3Bh - Change Current Directory",
						0x3C => "INT 21h/AH=3Ch - Create File",
						0x3D => "INT 21h/AH=3Dh - Open File",
						0x3E => "INT 21h/AH=3Eh - Close File",
						0x3F => "INT 21h/AH=3Fh - Read File",
						0x40 => "INT 21h/AH=40h - Write File",
						0x41 => "INT 21h/AH=41h - Delete File",
						0x42 => "INT 21h/AH=42h - Move File Pointer (Seek)",
						0x43 => "INT 21h/AH=43h - Get/Set File Attributes",
						0x44 => "INT 21h/AH=44h - IOCTL",
						0x47 => "INT 21h/AH=47h - Get Current Directory",
						0x48 => "INT 21h/AH=48h - Allocate Memory",
						0x49 => "INT 21h/AH=49h - Free Memory",
						0x4A => "INT 21h/AH=4Ah - Resize Memory Block",
						0x4B => "INT 21h/AH=4Bh - Execute Program (EXEC)",
						0x4C => "INT 21h/AH=4Ch - Terminate with Return Code",
						0x4D => "INT 21h/AH=4Dh - Get Return Code",
						0x4E => "INT 21h/AH=4Eh - Find First File",
						0x4F => "INT 21h/AH=4Fh - Find Next File",
						0x56 => "INT 21h/AH=56h - Rename File",
						0x57 => "INT 21h/AH=57h - Get/Set File Date and Time",
						0x5B => "INT 21h/AH=5Bh - Create New File",
						_ => $"INT 21h/AH={ahFunction.Value:X2}h - DOS Services"
					};

				case 0x2F:
					return "INT 2Fh - Multiplex Interrupt";

				case 0x33:
					if (ahFunction == null) return "INT 33h - Mouse Services";
					return (ahFunction.Value) switch
					{
						0x00 => "INT 33h/AX=0000h - Reset/Check Mouse Installed",
						0x01 => "INT 33h/AX=0001h - Show Mouse Cursor",
						0x02 => "INT 33h/AX=0002h - Hide Mouse Cursor",
						0x03 => "INT 33h/AX=0003h - Get Mouse Position and Button Status",
						0x04 => "INT 33h/AX=0004h - Set Mouse Cursor Position",
						0x07 => "INT 33h/AX=0007h - Set Horizontal Min/Max Position",
						0x08 => "INT 33h/AX=0008h - Set Vertical Min/Max Position",
						0x0C => "INT 33h/AX=000Ch - Set Mouse User Handler",
						0x0F => "INT 33h/AX=000Fh - Set Mouse Mickey/Pixel Ratio",
						0x10 => "INT 33h/AX=0010h - Set Mouse Exclusion Area",
						_ => $"INT 33h/AX={ahFunction.Value:X4}h - Mouse Services"
					};

				default:
					return $"INT {intNum:X2}h";
			}
		}

		private string GetTabs(int tabLevel)
		{
			StringWriter tabs = new StringWriter();

			for (int i = 0; i < tabLevel; i++)
			{
				tabs.Write("\t");
			}

			return tabs.ToString();
		}

		public ProgramSegment ParentSegment { get => this.parentSegment; }

		public int Ordinal { get => this.ordinal; set => this.ordinal = value; }

		public ushort FunctionOffset { get => this.fnOffset; }

		public uint FunctionEntryPoint { get => this.fnEntryPoint; }

		public string Name
		{
			get 
			{
				if (string.IsNullOrEmpty(this.name))
				{
					if (this.ordinal >= 0)
					{
						return $"Fn{this.ordinal}";
					}
					else
					{
						return $"Fn_{this.fnOffset:x4}";
					}
				}

				return this.name;
			}
		}

		public bool IsLibraryFunction { get => this.isLibraryFunction; set => this.isLibraryFunction = value; }

		public ProgramFunctionOptionsEnum FunctionOptions { get => this.functionOptions; }

		public BDictionary<int, ILVariable> Parameters { get => this.localParameters; }

		public int ParameterSize
		{
			get
			{
				int parameterSize = 0;

				for (int i = 0; i < this.localParameters.Count; i++)
				{
					ILVariable parameter = this.localParameters[i].Value;

					parameterSize += parameter.ValueType.SizeOf;
				}

				return parameterSize;
			}
		}

		public BDictionary<int, ILVariable> LocalVariables { get => this.localVariables; }

		public int LocalVariableSize { get => this.localVariableSize; set => this.localVariableSize = value; }

		public int LocalVariablePosition { get => this.localVariablePosition; set => this.localVariablePosition = value; }

		public ILVariable ReturnValue { get => this.returnValue; set => this.returnValue = value; }

		public int LocalStackSize { get => this.stackSize; }

		public List<ProgramFunction> ReferenceFunctions { get => this.referenceFunctions; }

		public FlowGraph? FlowGraph { get => this.flowGraph; }

		public bool TranslatedToIL { get => this.translatedToIL; set => this.translatedToIL = value; }

		public List<CPUInstruction> AsmInstructions { get => this.asmInstructions; }
	}
}
