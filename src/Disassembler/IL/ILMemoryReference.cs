namespace Disassembler
{
	/// <summary>
	/// Represents an indirect memory access: segment:[base + index + displacement]
	/// Used for 16-bit addressing modes like [BX+SI], [BX+DI+disp], [SI+disp], etc.
	/// </summary>
	public class ILMemoryReference : ILExpression
	{
		private ProgramSegment segment;
		private ILExpression? baseExpr;
		private ILExpression? indexExpr;
		private int displacement;
		private bool hasDisplacement;
		private ILValueType valueType;

		public ILMemoryReference(ProgramSegment segment, ILValueType valueType, ILExpression? baseExpr, ILExpression? indexExpr, int displacement, bool hasDisplacement)
		{
			this.segment = segment;
			this.valueType = valueType;
			this.baseExpr = baseExpr;
			this.indexExpr = indexExpr;
			this.displacement = displacement;
			this.hasDisplacement = hasDisplacement;
		}

		public ProgramSegment Segment
		{
			get => this.segment;
		}

		public ILValueType ValueType
		{
			get => this.valueType;
		}

		public override string ToCSString()
		{
			string addr = BuildAddressExpression();
			return $"this.oCPU.Read{AccessorSuffix()}(this.oParent.{this.segment.Name}.Segment, (ushort)({addr}))";
		}

		/// <summary>
		/// A memory reference used as an assignment target is a Write call, not a Read call,
		/// the value being stored becomes the last argument.
		/// </summary>
		public string ToCSWriteString(string value)
		{
			string addr = BuildAddressExpression();
			return $"this.oCPU.Write{AccessorSuffix()}(this.oParent.{this.segment.Name}.Segment, (ushort)({addr}), {value})";
		}

		/// <summary>
		/// The accessor width, taken from the base type rather than from SizeOf,
		/// because SizeOf reports the stack slot size and rounds a byte up to 2.
		/// </summary>
		private string AccessorSuffix()
		{
			switch (this.valueType.BaseType)
			{
				case ILBaseValueTypeEnum.UInt8:
				case ILBaseValueTypeEnum.Int8:
					return "UInt8";

				case ILBaseValueTypeEnum.UInt32:
				case ILBaseValueTypeEnum.Int32:
				case ILBaseValueTypeEnum.Ptr32:
				case ILBaseValueTypeEnum.FnPtr32:
					return "UInt32";

				default:
					return "UInt16";
			}
		}

		private string BuildAddressExpression()
		{
			List<string> parts = [];

			if (this.baseExpr != null)
				parts.Add(this.baseExpr.ToCSString());

			if (this.indexExpr != null)
				parts.Add(this.indexExpr.ToCSString());

			if (this.hasDisplacement && this.displacement != 0)
			{
				if (this.displacement > 0)
					parts.Add($"0x{this.displacement:x}");
				else
					parts.Add($"-0x{-this.displacement:x}");
			}
			else if (parts.Count == 0)
			{
				// Direct address with no registers
				parts.Add($"0x{(uint)this.displacement:x}");
			}

			return string.Join(" + ", parts);
		}

		public override string ToString()
		{
			return this.ToCSString();
		}
	}
}
