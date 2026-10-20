namespace Disassembler.Formats.MZ
{
	/// <summary>
	/// One overlay image from the container appended to an overlaid MicroSoft C executable.
	/// Unlike a Civ1 overlay this is not an MZ image of its own, it is a small header followed
	/// by a block of code that the overlay manager loads in one piece. The block holds one or more
	/// code segments, each starting on a paragraph, that are addressed relative to their own start.
	/// </summary>
	public class MZOverlay
	{
		private int iIndex;
		private int iHeaderPosition;
		private int iCodePosition;
		private int iConstant;
		private byte[] aData;
		private List<int> aRelocations;

		public MZOverlay(int index, int headerPosition, int codePosition, int constant,
			byte[] data, List<int> relocations)
		{
			this.iIndex = index;
			this.iHeaderPosition = headerPosition;
			this.iCodePosition = codePosition;
			this.iConstant = constant;
			this.aData = data;
			this.aRelocations = relocations;
		}

		/// <summary>
		/// One based overlay number, as stored in the thunks that call into this overlay
		/// </summary>
		public int Index
		{
			get => this.iIndex;
		}

		/// <summary>
		/// File offset of this overlay's header
		/// </summary>
		public int HeaderPosition
		{
			get => this.iHeaderPosition;
		}

		/// <summary>
		/// File offset of this overlay's code, which is what entry offsets are relative to
		/// </summary>
		public int CodePosition
		{
			get => this.iCodePosition;
		}

		/// <summary>
		/// The word at +0x04 of the header, 700 in every overlay of the Colonization build.
		/// Its meaning isn't established, it is kept so a different build can be recognised.
		/// </summary>
		public int Constant
		{
			get => this.iConstant;
		}

		/// <summary>
		/// The overlay's code
		/// </summary>
		public byte[] Data
		{
			get => this.aData;
		}

		/// <summary>
		/// Offsets into <see cref="Data"/> of the segment words that need relocating, already
		/// converted from the segment:offset pairs the header stores. In the Colonization build
		/// every one of them falls inside the code, nearly all on the segment word of a far call.
		/// </summary>
		public List<int> Relocations
		{
			get => this.aRelocations;
		}

		/// <summary>
		/// The relocation entries that address a word inside this overlay's code
		/// </summary>
		public IEnumerable<int> RelocationsWithinCode
		{
			get
			{
				for (int i = 0; i < this.aRelocations.Count; i++)
				{
					if (this.aRelocations[i] >= 0 && this.aRelocations[i] + 1 < this.aData.Length)
					{
						yield return this.aRelocations[i];
					}
				}
			}
		}

		/// <summary>
		/// True when a file offset falls inside this overlay's code
		/// </summary>
		public bool Contains(int filePosition)
		{
			return filePosition >= this.iCodePosition &&
				filePosition < this.iCodePosition + this.aData.Length;
		}

		public ushort ReadUInt16(int address)
		{
			return (ushort)((int)this.aData[address] | ((int)this.aData[address + 1] << 8));
		}

		public override string ToString()
		{
			return $"Overlay {this.iIndex} at 0x{this.iCodePosition:x}, {this.aData.Length} bytes";
		}
	}
}
