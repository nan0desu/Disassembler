using Disassembler.CPU;
using Disassembler.Formats.MZ;
using Disassembler.Formats.OMF;
using IRB.Collections.Generic;

namespace Disassembler
{
	public class MainProgram
	{
		// the segment the executable is loaded at, as in OpenCiv1
		public const ushort LoadSegment = 0x1000;

		// base types
		private ILValueType voidValueType = ILValueType.Void;
		private ILValueType charValueType = new("char", ILBaseValueTypeEnum.Int8);
		private ILValueType unsignedCharValueType = new("unsigned char", ILBaseValueTypeEnum.UInt8);
		private ILValueType intValueType = new("int", ILBaseValueTypeEnum.Int16);
		private ILValueType unsignedIntValueType = new("unsigned int", ILBaseValueTypeEnum.UInt16);
		private ILValueType longValueType = new("long", ILBaseValueTypeEnum.Int32);
		private ILValueType unsignedLongValueType = new("unsigned long", ILBaseValueTypeEnum.UInt32);
		private ILValueType stringValueType = new("char*", ILBaseValueTypeEnum.Ptr16, new ILValueType(ILBaseValueTypeEnum.Int8));

		private MZExecutable executable;
		private List<ModuleMatch> libraryMatches;
		private int version = 0;
		private BDictionary<uint, ProgramSegment> segments = new();
		private uint defaultDS = 0;
		private BDictionary<string, APIFunctionDefinition> apiFunctions = new();
		private BDictionary<string, ILValueType> customValueTypes = new();
		private bool addressNames = false;
		private bool runnable = false;

		public MainProgram(MZExecutable executable, List<ModuleMatch> matches, int version)
		{
			this.executable = executable;
			this.libraryMatches = matches;
			this.version = version;

			//MainProgramFunction function = new MainProgramFunction(this, MainProgramFunctionCallTypeEnum.Cdecl, "DivisionByZero", 
			//	new List<Variable>(), CPUParameterSizeEnum.Undefined, 0, 0, 0);
		}

		/// <summary>
		/// Name segments and functions by address, the OpenCiv1 way (Segment_11a8, Overlay_18,
		/// F0_11a8_0268), instead of by ordinal.
		/// </summary>
		public bool AddressNames
		{
			get => this.addressNames;
			set => this.addressNames = value;
		}

		/// <summary>
		/// Write code that runs on the virtual CPU as is, instead of a readable model of it.
		/// The stack frame stays in emulated memory ([BP±n] is not turned into C# locals and
		/// parameters), calls push and returns pop the return address, ENTER and LEAVE are
		/// expanded, and CPU methods use the VCPU names (ADD_UInt16, not ADDUInt16).
		/// </summary>
		public bool Runnable
		{
			get => this.runnable;
			set => this.runnable = value;
		}

		private readonly Dictionary<int, HashSet<uint>> relocatedWords = new();

		/// <summary>
		/// Linear addresses of the segment words DOS (for the load module) or the overlay manager
		/// (for an overlay) adds the load segment to. Resident addresses are relative to the load
		/// module, overlay ones to the overlay's code, the same as instruction addresses.
		/// </summary>
		public HashSet<uint> GetRelocatedWords(int overlay)
		{
			if (this.relocatedWords.TryGetValue(overlay, out HashSet<uint>? words))
				return words;

			words = new HashSet<uint>();

			if (overlay == 0)
			{
				foreach (MZRelocationItem item in this.executable.Relocations)
				{
					words.Add(ToLinearAddress(item.Segment, item.Offset));
				}
			}
			else if (this.executable.OverlayContainer?.GetByNumber(overlay) is MZOverlay container)
			{
				foreach (int position in container.Relocations)
				{
					words.Add((uint)position);
				}
			}

			this.relocatedWords.Add(overlay, words);
			return words;
		}

		/// <summary>
		/// The segment a code segment is known by at run time. Resident segments are loaded at
		/// LoadSegment, as in OpenCiv1. Overlay code isn't in emulated memory at all, so each of
		/// its segments gets a made up value above the resident image and conventional memory,
		/// only ever used as a key to find the function a far pointer or a pushed CS names.
		/// </summary>
		public ushort RuntimeSegment(ProgramSegment segment)
		{
			if (segment.CPUOverlay == 0)
				return (ushort)(segment.CPUSegment + LoadSegment);

			MZOverlay overlay = this.executable.OverlayContainer!.GetByNumber(segment.CPUOverlay)!;
			int paragraphs = 0;

			foreach (MZOverlay other in this.executable.OverlayContainer.Overlays)
			{
				if (other.Index < overlay.Index)
					paragraphs += (other.Data.Length + 15) / 16;
			}

			return (ushort)(OverlaySegmentBase + paragraphs + segment.CPUSegment);
		}

		// overlay code segments are numbered from here, past 640 KB, where nothing else is
		public const ushort OverlaySegmentBase = 0xa000;

		/// <summary>
		/// Writes the table runnable code dispatches indirect calls and jumps through: every
		/// function at its run time address, and every thunk at its own address leading to the
		/// function it names. Addresses are linear (segment * 16 + offset), so any segment:offset
		/// spelling of the same place finds it. Unknown addresses fall back to Native, where the
		/// host registers its own implementations, and otherwise throw.
		/// </summary>
		public void WriteDispatchCS(string path)
		{
			SortedDictionary<uint, string> entries = new();

			foreach (ProgramSegment segment in this.segments.Values)
			{
				uint segmentBase = (uint)RuntimeSegment(segment) << 4;

				foreach (ProgramFunction function in segment.Functions.Values)
				{
					if (!function.DisassemblyFailed)
					{
						entries.TryAdd(segmentBase + function.FunctionOffset, $"this.oParent.{segment.Name}.{function.Name}(); return true;");
					}
				}
			}

			int thunks = 0;

			foreach (MZOverlayThunk thunk in this.executable.FindThunks())
			{
				ProgramFunction? target = thunk.IsOverlay
					? FindFunction((ushort)thunk.OverlayNumber, thunk.TargetSegment, (ushort)thunk.TargetOffset)
					: FindFunction(0, thunk.TargetSegment, (ushort)thunk.TargetOffset);

				if (target != null && !target.DisassemblyFailed)
				{
					entries.TryAdd(((uint)LoadSegment << 4) + (uint)thunk.Position,
						$"this.oParent.{target.ParentSegment.Name}.{target.Name}(); return true; // thunk");
					thunks++;
				}
			}

			using StreamWriter writer = new(Path.Combine(path, "CodeDispatch.cs"));

			writer.WriteLine("using IRB.VirtualCPU;");
			writer.WriteLine();
			writer.WriteLine("namespace OpenCiv1");
			writer.WriteLine("{");
			writer.WriteLine("\t/// <summary>");
			writer.WriteLine("\t/// Calls and jumps whose target is only known at run time, by linear address.");
			writer.WriteLine("\t/// Generated by the Disassembler; register host implementations in Native.");
			writer.WriteLine("\t/// </summary>");
			writer.WriteLine("\tpublic class CodeDispatch");
			writer.WriteLine("\t{");
			writer.WriteLine("\t\tprivate OpenCiv1Game oParent;");
			writer.WriteLine();
			writer.WriteLine("\t\tpublic CodeDispatch(OpenCiv1Game parent)");
			writer.WriteLine("\t\t{");
			writer.WriteLine("\t\t\tthis.oParent = parent;");
			writer.WriteLine("\t\t}");
			writer.WriteLine();
			writer.WriteLine("\t\t/// <summary>Host implementations, by linear address; checked first</summary>");
			writer.WriteLine("\t\tpublic Dictionary<uint, Action> Native { get; } = new();");
			writer.WriteLine();
			writer.WriteLine("\t\tpublic void CallNear(ushort segment, ushort offset) => Invoke(segment, offset);");
			writer.WriteLine();
			writer.WriteLine("\t\tpublic void CallFar(uint address) => Invoke((ushort)(address >> 16), (ushort)address);");
			writer.WriteLine();
			writer.WriteLine("\t\t// a jump is a tail call, the caller returns right after it");
			writer.WriteLine("\t\tpublic void JumpNear(ushort segment, ushort offset) => Invoke(segment, offset);");
			writer.WriteLine();
			writer.WriteLine("\t\tpublic void JumpFar(uint address) => Invoke((ushort)(address >> 16), (ushort)address);");
			writer.WriteLine();
			writer.WriteLine("\t\tprivate void Invoke(ushort segment, ushort offset)");
			writer.WriteLine("\t\t{");
			writer.WriteLine("\t\t\tuint address = ((uint)segment << 4) + offset;");
			writer.WriteLine();
			writer.WriteLine("\t\t\tif (this.Native.TryGetValue(address, out Action? action))");
			writer.WriteLine("\t\t\t\taction();");
			writer.WriteLine("\t\t\telse if (!Call(address))");
			writer.WriteLine("\t\t\t\tthrow new Exception($\"No function at {segment:x4}:{offset:x4} (linear 0x{address:x5})\");");
			writer.WriteLine("\t\t}");
			writer.WriteLine();
			writer.WriteLine($"\t\t// {entries.Count - thunks} functions, {thunks} thunks");
			writer.WriteLine("\t\tprivate bool Call(uint address)");
			writer.WriteLine("\t\t{");
			writer.WriteLine("\t\t\tswitch (address)");
			writer.WriteLine("\t\t\t{");

			foreach (KeyValuePair<uint, string> entry in entries)
			{
				writer.WriteLine($"\t\t\t\tcase 0x{entry.Key:x5}: {entry.Value}");
			}

			writer.WriteLine("\t\t\t\tdefault: return false;");
			writer.WriteLine("\t\t\t}");
			writer.WriteLine("\t\t}");
			writer.WriteLine("\t}");
			writer.WriteLine("}");
		}

		private record CodePointer(ushort Address, bool Far, ushort Overlay, ushort Segment);

		/// <summary>
		/// Disassembles code that is only reached through a pointer in DGROUP: a CALL or JMP
		/// through [disp16] whose variable is set from data (its initial value in the image)
		/// or from an immediate written into it (MOV [var], offset / MOV [var+2], segment).
		/// Far pointers to a thunk resolve to the function the thunk names. Repeats until no
		/// new function turns up, since the new code can have pointers of its own.
		/// </summary>
		public int DisassembleCodePointerTargets()
		{
			int added = 0;
			HashSet<string> seen = new();

			while (true)
			{
				List<ProgramFunction> functions = this.segments.Values.SelectMany(s => s.Functions.Values).ToList();
				Dictionary<ushort, CodePointer> pointers = new();

				foreach (ProgramFunction function in functions)
				{
					foreach (CPUInstruction instruction in function.AsmInstructions)
					{
						if ((instruction.InstructionType == CPUInstructionEnum.CALL || instruction.InstructionType == CPUInstructionEnum.JMP ||
							instruction.InstructionType == CPUInstructionEnum.CALLF || instruction.InstructionType == CPUInstructionEnum.JMPF) &&
							instruction.Parameters.Count == 1 && IsDGROUPVariable(instruction.Parameters[0]))
						{
							bool far = instruction.InstructionType == CPUInstructionEnum.CALLF || instruction.InstructionType == CPUInstructionEnum.JMPF;
							ushort address = (ushort)instruction.Parameters[0].Displacement;

							pointers.TryAdd(address, new CodePointer(address, far, function.ParentSegment.CPUOverlay, function.ParentSegment.CPUSegment));
						}
					}
				}

				List<(ushort Overlay, ushort Segment, ushort Offset, string Source)> targets = new();
				int dgroup = (int)(this.defaultDS * 16);

				foreach (CodePointer pointer in pointers.Values)
				{
					ushort offset = ReadImageWord(dgroup + pointer.Address);
					ushort segment = pointer.Far ? ReadImageWord(dgroup + pointer.Address + 2) : pointer.Segment;

					AddPointerTarget(targets, pointer, segment, offset, $"initial value of DS:{pointer.Address:x4}");
				}

				foreach (ProgramFunction function in functions)
				{
					List<CPUInstruction> instructions = function.AsmInstructions;

					for (int i = 0; i < instructions.Count; i++)
					{
						CPUInstruction instruction = instructions[i];

						if (instruction.InstructionType != CPUInstructionEnum.MOV || instruction.Parameters.Count != 2 ||
							!IsDGROUPVariable(instruction.Parameters[0]) ||
							!pointers.TryGetValue((ushort)instruction.Parameters[0].Displacement, out CodePointer? pointer) ||
							!TryGetConstant(instructions, i, out ushort offset))
							continue;

						ushort segment = pointer.Segment;

						if (pointer.Far)
						{
							// the segment half is written next to the offset half, either side of it
							int segmentWrite = -1;

							for (int j = Math.Max(0, i - 4); j < Math.Min(instructions.Count, i + 5); j++)
							{
								CPUInstruction other = instructions[j];

								if (other.InstructionType == CPUInstructionEnum.MOV && other.Parameters.Count == 2 &&
									IsDGROUPVariable(other.Parameters[0]) && (ushort)other.Parameters[0].Displacement == (ushort)(pointer.Address + 2))
								{
									segmentWrite = j;
									break;
								}
							}

							if (segmentWrite < 0 || !TryGetConstant(instructions, segmentWrite, out segment))
							{
								Console.WriteLine($"Far pointer DS:{pointer.Address:x4} gets offset 0x{offset:x4} in {function.ParentSegment.Name}.{function.Name} " +
									"without a constant segment next to it");
								continue;
							}
						}

						AddPointerTarget(targets, pointer, segment, offset, $"written to DS:{pointer.Address:x4} by {function.ParentSegment.Name}.{function.Name}");
					}
				}

				int before = added;

				foreach ((ushort overlay, ushort segment, ushort offset, string source) in targets)
				{
					string key = $"{overlay}:{segment:x4}:{offset:x4}";

					if (!seen.Add(key) || FindFunction(overlay, segment, offset) != null)
						continue;

					try
					{
						ProgramFunction function = Disassemble(overlay, segment, offset, null);
						Console.WriteLine($"Code pointer target {function.ParentSegment.Name}.{function.Name}, {source}");
						added++;
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Code pointer target {overlay}:{segment:x4}:{offset:x4} ({source}) failed to disassemble: {ex.Message}");
					}
				}

				if (added == before)
					return added;
			}
		}

		private void AddPointerTarget(List<(ushort, ushort, ushort, string)> targets, CodePointer pointer, ushort segment, ushort offset, string source)
		{
			// null and the -1 "not set" marker are guarded by the callers
			if ((offset == 0 && (!pointer.Far || segment == 0)) || offset == 0xffff)
				return;

			if (pointer.Far)
			{
				MZOverlayThunk? thunk = this.executable.GetThunkAt((int)ToLinearAddress(segment, offset));

				if (thunk != null)
				{
					targets.Add(thunk.IsOverlay
						? ((ushort)thunk.OverlayNumber, thunk.TargetSegment, (ushort)thunk.TargetOffset, source)
						: ((ushort)0, thunk.TargetSegment, (ushort)thunk.TargetOffset, source));
					return;
				}

				targets.Add((0, segment, offset, source));
			}
			else
			{
				targets.Add((pointer.Overlay, pointer.Segment, offset, source));
			}
		}

		/// <summary>
		/// The value a MOV [var], source stores, when it is a constant: an immediate, or a
		/// register that a MOV reg, imm a few instructions earlier loaded.
		/// </summary>
		private static bool TryGetConstant(List<CPUInstruction> instructions, int position, out ushort value)
		{
			value = 0;
			CPUParameter source = instructions[position].Parameters[1];

			if (source.Type == CPUParameterTypeEnum.Immediate)
			{
				value = (ushort)source.Value;
				return true;
			}

			if (source.Type != CPUParameterTypeEnum.Register || source.Size != CPUParameterSizeEnum.UInt16)
				return false;

			for (int i = position - 1; i >= 0 && i >= position - 4; i--)
			{
				CPUInstruction instruction = instructions[i];

				if (instruction.Parameters.Count > 0 && instruction.Parameters[0].Type == CPUParameterTypeEnum.Register &&
					instruction.Parameters[0].Value == source.Value)
				{
					// the last write to the register decides it
					if (instruction.InstructionType == CPUInstructionEnum.MOV &&
						instruction.Parameters[1].Type == CPUParameterTypeEnum.Immediate)
					{
						value = (ushort)instruction.Parameters[1].Value;
						return true;
					}

					return false;
				}

				if (instruction.Label)
					return false;
			}

			return false;
		}

		// BSS lies past the end of the file and starts out zero
		private ushort ReadImageWord(int address)
		{
			return address + 1 < this.executable.Data.Length ? this.executable.ReadUInt16(address) : (ushort)0;
		}

		private static bool IsDGROUPVariable(CPUParameter parameter)
		{
			// [disp16] in DS, or in SS which is the same segment in a small data model program
			return parameter.Type == CPUParameterTypeEnum.MemoryAddress && parameter.Value == 6 &&
				(parameter.DataSegment == CPUSegmentRegisterEnum.DS || parameter.DataSegment == CPUSegmentRegisterEnum.SS);
		}

		public ProgramFunction? FindFunction(ushort overlay, ushort segment, ushort offset)
		{
			uint absoluteSegment = ToAbsoluteSegment(overlay, segment);

			// look into function list
			if (this.segments.ContainsKey(absoluteSegment) && this.segments.GetValueByKey(absoluteSegment).Functions.ContainsKey(offset))
			{
				return this.segments.GetValueByKey(absoluteSegment).Functions.GetValueByKey(offset);
			}

			// try to find API function
			if (overlay == 0)
			{
				uint absoluteAddress = MainProgram.ToLinearAddress(segment, offset);

				// look into library matches
				for (int i = 0; i < this.libraryMatches.Count; i++)
				{
					ModuleMatch match = this.libraryMatches[i];

					if (absoluteAddress >= match.LinearAddress && absoluteAddress < match.LinearAddress + match.Length)
					{
						OMFOBJModule module = match.Module;

						for (int j = 0; j < module.PublicNames.Count; j++)
						{
							OMFPublicNameDefinition publicNameDef = module.PublicNames[j];

							for (int k = 0; k < publicNameDef.PublicNames.Count; k++)
							{
								if (match.LinearAddress + publicNameDef.PublicNames[k].Value == absoluteAddress)
								{
									string sName = publicNameDef.PublicNames[k].Key;
									sName = sName.StartsWith("_") ? sName.Substring(1) : sName;

									// add missing function
									ProgramSegment fnSegment = FindOrCreateSegment(overlay, segment);
									ProgramFunction function = new ProgramFunction(fnSegment, offset, sName);

									function.IsLibraryFunction = true;

									fnSegment.Functions.Add(offset, function);

									if (this.apiFunctions.ContainsKey(sName))
									{
										APIFunctionDefinition apiFunction = this.apiFunctions.GetValueByKey(sName);

										function.Parameters.Clear();
										// !!! Will have to introduce program memory model (Small, Medium, Large...)
										int parameterOffset = 6; // (function.CallType & ProgramFunctionTypeEnum.Far) == ProgramFunctionTypeEnum.Far ? 6 : 4;

										for (int l = 0; l < apiFunction.Parameters.Length; l++)
										{
											ILVariable parameter = apiFunction.Parameters[l];

											parameter.Offset = parameterOffset;
											function.Parameters.Add(parameterOffset, parameter);

											// minimum stack parameter size is 2
											parameterOffset += Math.Max(2, parameter.ValueType.SizeOf);
										}

										function.ReturnValue = apiFunction.ReturnValue;
									}
									else
									{
										Console.WriteLine($"Adding undefined API function {function.ParentSegment.Name}.{function.Name}()");
									}

									return function;
								}
							}
						}
					}
				}
			}

			return null;
		}

		public ProgramSegment FindOrCreateSegment(ushort overlay, ushort segment)
		{
			uint absoluteSegment = ToAbsoluteSegment(overlay, segment);

			if (this.segments.ContainsKey(absoluteSegment))
			{
				return this.segments.GetValueByKey(absoluteSegment);
			}
			else
			{
				ProgramSegment newSegment = new ProgramSegment(this, absoluteSegment);

				this.segments.Add(absoluteSegment, newSegment);

				return newSegment;
			}
		}

		public ProgramFunction Disassemble(ushort overlay, ushort segment, ushort offset, string? name)
		{
			ProgramFunction? function = FindFunction(overlay, segment, offset);

			if (function == null)
			{
				ProgramSegment fnSegment = FindOrCreateSegment(overlay, segment);
				function = new ProgramFunction(fnSegment, offset, name);

				fnSegment.Functions.Add(offset, function);

				try
				{
					function.Disassemble();
				}
				catch (Exception)
				{
					// the caller decides what to do about it, this only records that the function
					// is half built so the writer can leave it out
					function.DisassemblyFailed = true;
					throw;
				}
			}

			return function;
		}

		public void DisassembleOverlay()
		{
			MemoryStream reader = new MemoryStream(this.executable.Data);
			
			reader.Position = 0x30;

			ushort usCount = MZExecutable.ReadUInt16(reader);
			ushort[] aOffsets = new ushort[usCount];

			for (int i = 0; i < usCount; i++)
			{
				aOffsets[i] = MZExecutable.ReadUInt16(reader);
			}
			reader.Close();

			for (int i = 0; i < usCount; i++)
			{
				this.Disassemble(0, 0, aOffsets[i], null);
			}
			Console.WriteLine("--- Overlay offsets");
			for (int i = 0; i < usCount; i++)
			{
				Console.WriteLine($"0x{aOffsets[i]:x4}");
			}
			Console.WriteLine("--- End overlay offsets");
		}

		public void AssignOrdinals()
		{
			// enumerate Main segments and their functions
			uint[] segmentOffsets = this.segments.Keys.ToArray();
			int segmentOrdinal = 0;
			int overlayOrdinal = 0;
			ushort previousOverlay = 0;

			Array.Sort(segmentOffsets);


			for (int i = 0; i < segmentOffsets.Length; i++)
			{
				ProgramSegment segment = this.segments.GetValueByKey(segmentOffsets[i]);

				// differentiate between overlay and segments from main exe
				if (segment.CPUOverlay > 0 && segment.CPUOverlay == previousOverlay)
				{
					// another code segment of the same overlay, the name tells them apart
					segment.Ordinal = overlayOrdinal - 1;
				}
				else if (segment.CPUOverlay > 0)
				{
					previousOverlay = segment.CPUOverlay;
					segment.Ordinal = overlayOrdinal++;

					if (this.version == 1 && overlayOrdinal == 15)
					{
						// skip overlay 14 for version 1
						overlayOrdinal++;
					}
				}
				else
				{
					segment.Ordinal = segmentOrdinal++;
				}

				ushort[] functionOffsets = segment.Functions.Keys.ToArray();

				Array.Sort(functionOffsets);

				for (int j = 0; j < functionOffsets.Length; j++)
				{
					ProgramFunction function = segment.Functions.GetValueByKey(functionOffsets[j]);

					function.Ordinal = j + 1;
				}
			}

			for (int i = 0; i < this.segments.Count; i++)
			{
				ProgramSegment segment = this.segments[i].Value;

				for (int j = 0; j < segment.Functions.Count; j++)
				{
					ProgramFunction function = segment.Functions[j].Value;

					if (function.FlowGraph != null)
					{
						function.FlowGraph.TranslateFunction();
					}
				}
			}
		}

		public MZExecutable Executable
		{
			get
			{
				return this.executable;
			}
		}

		public List<ModuleMatch> LibraryMatches
		{
			get
			{
				return this.libraryMatches;
			}
		}

		public int Version { get { return this.version; } }

		public BDictionary<uint, ProgramSegment> Segments
		{
			get => this.segments;
		}

		public BDictionary<string, APIFunctionDefinition> APIFunctions { get => this.apiFunctions; }

		public uint DefaultDS
		{
			get => this.defaultDS;
			set => this.defaultDS = value;
		}

		public ILValueType VoidValueType { get => this.voidValueType; }

		public ILValueType CharValueType { get => this.charValueType; }

		public ILValueType UnsignedCharValueType { get => this.unsignedCharValueType; }

		public ILValueType IntValueType { get => this.intValueType; }

		public ILValueType UnsignedIntValueType { get => this.unsignedIntValueType; }

		public ILValueType LongValueType { get => this.longValueType; }

		public ILValueType UnsignedLongValueType { get => this.unsignedLongValueType; }

		public ILValueType StringValueType { get => this.stringValueType; }

		public BDictionary<string, ILValueType> CustomValueTypes { get => this.customValueTypes; }

		public ILValueType FromCPUParameterSizeEnum(CPUParameterSizeEnum parameterType)
		{
			switch (parameterType)
			{
				case CPUParameterSizeEnum.UInt8:
					return this.UnsignedCharValueType;

				case CPUParameterSizeEnum.UInt16:
					// the default is Int16
					return this.intValueType;

				case CPUParameterSizeEnum.UInt32:
					// the default is Int32
					return this.longValueType;

				default:
					return ILValueType.Void;
			}
		}

		public static uint ToAbsoluteSegment(ushort overlay, ushort segment)
		{
			return (uint)(((uint)overlay << 16) + segment);
		}

		public static ushort ToCPUSegment(uint segment)
		{
			return (ushort)(segment & 0xffff);
		}

		public static ushort ToCPUOverlay(uint segment)
		{
			return (ushort)((segment & 0xffff0000) >> 16);
		}

		public static uint ToLinearAddress(ushort segment, ushort offset)
		{
			return (uint)(((uint)segment << 4) + offset);
		}

		public static uint ToLinearAddress(ushort segment, uint offset)
		{
			return (uint)(((uint)segment << 4) + offset);
		}
	}
}
