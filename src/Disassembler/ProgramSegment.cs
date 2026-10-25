using Disassembler.CPU;
using IRB.Collections.Generic;
using System.Numerics;

namespace Disassembler
{
	public class ProgramSegment
	{
		private MainProgram parentProgram;

		private ProgramSegmentTypeEnum programSegmentType = ProgramSegmentTypeEnum.None;
		private uint segment;
		private int ordinal = -1;
		private string? name = null;
		private BDictionary<int, ILVariable> globalVariables = [];
		private BDictionary<ushort, ProgramFunction> functions = [];

		public ProgramSegment(MainProgram program, uint segment) : this(program, ProgramSegmentTypeEnum.None, segment)
		{ }

		public ProgramSegment(MainProgram program, ProgramSegmentTypeEnum programSegmentType, uint segment)
		{
			this.parentProgram = program;
			this.programSegmentType = programSegmentType;

			if (segment < 0)
			{
				throw new ArgumentOutOfRangeException("segment", "The segment address can't be a negative value");
			}

			this.segment = segment;
		}

		public ILVariable GetOrDefineGlobalVariable(CPUParameterSizeEnum type, int offset)
		{
			ILValueType valueType = this.parentProgram.FromCPUParameterSizeEnum(type);

			if (this.globalVariables.ContainsKey(offset))
			{
				ILVariable variable = this.globalVariables.GetValueByKey(offset);
				if (variable.ValueType != valueType)
				{
					Console.WriteLine($"Variable type at offset 0x{((uint)offset):x} type '{variable.ValueType}' doesnt match requested type '{valueType}'");
				}

				return variable;
			}
			else
			{
				ILVariable variable = new(null, ILVariableScopeEnum.Global, valueType, offset);

				this.globalVariables.Add(offset, variable);

				return variable;
			}
		}

		public void WriteAsmCS(string path, int verbosity)
		{
			StreamWriter writer = new($"{path}{Path.DirectorySeparatorChar}{this.Name}Asm.cs");

			if (this.functions.Count > 0)
			{
				ProgramFunction[] functions = this.functions.Values.ToArray();
				string className = this.Name;

				// sort by offsets
				Array.Sort(functions, (item1, item2) => item1.FunctionOffset.CompareTo(item2.FunctionOffset));

				writer.WriteLine("using IRB.VirtualCPU;");
				writer.WriteLine();
				writer.WriteLine($"namespace {this.parentProgram.Namespace}");
				writer.WriteLine("{");
				writer.WriteLine("\tpublic class {0}", className);
				writer.WriteLine("\t{");
				if (this.CPUOverlay == 0)
				{
					writer.WriteLine($"\t\t// Segment 0x{this.CPUSegment:x}");
				}
				else if (this.CPUSegment != 0)
				{
					writer.WriteLine($"\t\t// Overlay 0x{this.CPUOverlay:x}, segment 0x{this.CPUSegment:x}");
				}
				else
				{
					writer.WriteLine($"\t\t// Overlay 0x{this.CPUOverlay:x}");
				}

				writer.WriteLine($"\t\tprivate {this.parentProgram.ParentClass} oParent;");
				writer.WriteLine("\t\tprivate VCPU oCPU;");
				writer.WriteLine();
				writer.WriteLine("\t\tpublic {0}({1} parent)", className, this.parentProgram.ParentClass);
				writer.WriteLine("\t\t{");
				writer.WriteLine("\t\t\tthis.oParent = parent;");
				writer.WriteLine("\t\t\tthis.oCPU = parent.CPU;");
				writer.WriteLine("\t\t}");

				if (this.globalVariables.Count > 0)
				{
					writer.WriteLine();
					writer.WriteLine("\t\t// Global variables");

					ILVariable[] variables = this.globalVariables.Values.ToArray();
					Array.Sort(variables, (item1, item2) => item1.Offset.CompareTo(item2.Offset));

					for (int k = 0; k < variables.Length; k++)
					{
						writer.WriteLine($"\t\t{variables[k].CSDeclaration};");
					}
				}

				for (int j = 0; j < functions.Length; j++)
				{
					ProgramFunction function = functions[j];

					// A function that threw part way through disassembly has instructions but no
					// consistent jump targets or call references, and writing it fails on the first
					// one that doesn't resolve. Leave a note in its place.
					if (function.DisassemblyFailed)
					{
						writer.WriteLine($"\t\t// {function.Name} could not be disassembled");
						continue;
					}

					function.WriteAsmCS(writer, 2, verbosity);
				}

				writer.WriteLine("\t}");
				writer.WriteLine("}");
			}

			writer.Close();
		}

		public MainProgram ParentProgram
		{
			get => this.parentProgram;
		}

		public ProgramSegmentTypeEnum ProgramSegmentType
		{
			get => this.programSegmentType;
			set => this.programSegmentType = value;
		}

		public uint Segment
		{
			get => this.segment;
		}

		public ushort CPUSegment
		{
			get => MainProgram.ToCPUSegment(this.segment);
		}

		public ushort CPUOverlay
		{
			get => MainProgram.ToCPUOverlay(this.segment);
		}

		public int Ordinal
		{
			get => this.ordinal;
			set => this.ordinal = value;
		}

		public string Name
		{
			get
			{
				if (this.name != null)
				{
					return this.name;
				}

				if (this.parentProgram.AddressNames)
				{
					if (this.CPUOverlay != 0 && this.CPUSegment != 0)
					{
						return $"Overlay_{this.CPUOverlay}_{this.CPUSegment:x4}";
					}
					else if (this.CPUOverlay != 0)
					{
						return $"Overlay_{this.CPUOverlay}";
					}
					else
					{
						return $"Segment_{this.CPUSegment + this.parentProgram.LoadSegment:x4}";
					}
				}

				if (this.ordinal == -1)
				{
					if (MainProgram.ToCPUOverlay(this.segment) != 0)
					{
						return $"Ovr_{MainProgram.ToCPUOverlay(this.segment):x4}";
					}
					else
					{
						return $"Seg_{this.segment:x4}";
					}
				}

				if (MainProgram.ToCPUOverlay(this.segment) != 0 && this.CPUSegment != 0)
				{
					return $"Ovr{this.ordinal}_{this.CPUSegment:x}";
				}
				else if (MainProgram.ToCPUOverlay(this.segment) != 0)
				{
					return $"Ovr{this.ordinal}";
				}
				else
				{
					return $"Seg{this.ordinal}";
				}
			}

			set => this.name = value;
		}

		public BDictionary<int, ILVariable> GlobalVariables
		{
			get => this.globalVariables;
		}

		public BDictionary<ushort, ProgramFunction> Functions
		{
			get => this.functions;
		}

		public static bool operator ==(ProgramSegment item1, ProgramSegment item2)
		{
			return IsEqual(item1, item2);
		}

		public static bool operator !=(ProgramSegment item1, ProgramSegment item2)
		{
			return !IsEqual(item1, item2);
		}

		private static bool IsEqual(ProgramSegment? item1, ProgramSegment? item2)
		{
			// comparing against null is legitimate, don't dereference it
			if (item1 is null || item2 is null)
			{
				return item1 is null && item2 is null;
			}

			// we will not compare functions that segments contain, because we are comparing only the segment objects
			return item1.segment == item2.segment && item1.ordinal == item2.ordinal && item1.programSegmentType == item2.programSegmentType;
		}

		public override bool Equals(object? obj)
		{
			return (obj is ProgramSegment) && IsEqual(this, (ProgramSegment)obj);
		}

		public override string ToString()
		{
			return this.Name;
		}

		public override int GetHashCode()
		{
			return this.segment.GetHashCode();
		}
	}
}
