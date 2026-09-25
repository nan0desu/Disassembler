namespace Disassembler.Formats.MZ
{
	/// <summary>
	/// The overlay container appended to an overlaid MicroSoft C executable.
	///
	/// Civ1 appends its overlays as a chain of MZ images, which <see cref="MZExecutable"/> already
	/// understands. Colonization appends them in this container instead: a chain of overlay images
	/// starting at the first paragraph boundary after the load module, each one
	///
	///   +0x00  u16         total size of this image in paragraphs, header included
	///   +0x02  u16         header size in paragraphs, code starts at header + this * 16
	///   +0x04  u32         700 in every image of the Colonization build
	///   +0x08  u32         relocation count
	///   +0x0C  u32         zero
	///   +0x10  u32[count]  relocation offsets, then zero padding up to the code
	///
	/// with the next image at header + (the word at +0x00) * 16, the chain ending at the end of
	/// the file. Overlay code is reached from the resident image through thunks, and a thunk's
	/// target is simply the overlay's code position plus the entry offset the thunk carries.
	/// </summary>
	public class MZOverlayContainer
	{
		/// <summary>
		/// No sane overlay is larger than this, it is a guard against parsing random trailing data
		/// </summary>
		private const int MaximumOverlays = 1024;

		private int iPosition;
		private List<MZOverlay> aOverlays = new List<MZOverlay>();

		private MZOverlayContainer()
		{ }

		/// <summary>
		/// Reads the container at <paramref name="position"/>, or returns null when the data there
		/// isn't one. The whole chain has to parse and end at the end of the file before anything
		/// is returned, so a file that merely happens to start with plausible words is rejected
		/// rather than half loaded.
		/// </summary>
		public static MZOverlayContainer? TryRead(Stream stream, int position)
		{
			try
			{
				MZOverlayContainer container = new MZOverlayContainer();
				container.iPosition = position;

				int offset = position;
				int index = 1;

				while (offset < stream.Length)
				{
					if (offset + 16 > stream.Length || index > MaximumOverlays)
					{
						return null;
					}

					stream.Seek(offset, SeekOrigin.Begin);

					int totalParagraphs = MZExecutable.ReadUInt16(stream);
					int headerParagraphs = MZExecutable.ReadUInt16(stream);
					int constant = MZExecutable.ReadUInt16(stream) | (MZExecutable.ReadUInt16(stream) << 16);
					int relocationCount = MZExecutable.ReadUInt16(stream) | (MZExecutable.ReadUInt16(stream) << 16);

					if (totalParagraphs == 0 || headerParagraphs == 0 || headerParagraphs >= totalParagraphs)
					{
						return null;
					}

					int codePosition = offset + (headerParagraphs * 16);
					int endPosition = offset + (totalParagraphs * 16);

					// the relocation table has to fit between the header and the code
					if (relocationCount < 0 || offset + 16 + (relocationCount * 4) > codePosition)
					{
						return null;
					}

					List<int> relocations = new List<int>(relocationCount);
					stream.Seek(offset + 16, SeekOrigin.Begin);

					for (int i = 0; i < relocationCount; i++)
					{
						relocations.Add(MZExecutable.ReadUInt16(stream) | (MZExecutable.ReadUInt16(stream) << 16));
					}

					// the last overlay is padded to a paragraph, so it can overhang the final bytes
					int dataSize = endPosition - codePosition;

					if (codePosition + dataSize > stream.Length)
					{
						dataSize = (int)stream.Length - codePosition;
					}

					if (dataSize <= 0)
					{
						return null;
					}

					byte[] data = new byte[dataSize];
					stream.Seek(codePosition, SeekOrigin.Begin);

					if (stream.Read(data, 0, dataSize) != dataSize)
					{
						return null;
					}

					container.aOverlays.Add(new MZOverlay(index, offset, codePosition, constant,
						data, relocations));

					offset = endPosition;
					index++;
				}

				if (container.aOverlays.Count == 0)
				{
					return null;
				}

				// the chain has to land on the end of the file, within the final paragraph padding
				int overhang = offset - (int)stream.Length;

				if (overhang < 0 || overhang >= 16)
				{
					return null;
				}

				return container;
			}
			catch (Exception)
			{
				// a container this isn't can run off the end of the stream in any number of ways
				return null;
			}
		}

		/// <summary>
		/// File offset the container starts at
		/// </summary>
		public int Position
		{
			get => this.iPosition;
		}

		public List<MZOverlay> Overlays
		{
			get => this.aOverlays;
		}

		/// <summary>
		/// The overlay a thunk's overlay number refers to, or null when it is out of range.
		/// Overlay numbers are one based.
		/// </summary>
		public MZOverlay? GetByNumber(int number)
		{
			if (number < 1 || number > this.aOverlays.Count)
			{
				return null;
			}

			return this.aOverlays[number - 1];
		}

		/// <summary>
		/// File offset an overlay thunk points at, or -1 when the entry offset falls outside the
		/// overlay it names.
		/// </summary>
		public int Resolve(int overlayNumber, int entryOffset)
		{
			MZOverlay? overlay = GetByNumber(overlayNumber);

			if (overlay == null || entryOffset < 0 || entryOffset >= overlay.Data.Length)
			{
				return -1;
			}

			return overlay.CodePosition + entryOffset;
		}

		/// <summary>
		/// The overlay containing a file offset, or null when it is not in the container
		/// </summary>
		public MZOverlay? Locate(int filePosition)
		{
			for (int i = 0; i < this.aOverlays.Count; i++)
			{
				if (this.aOverlays[i].Contains(filePosition))
				{
					return this.aOverlays[i];
				}
			}

			return null;
		}
	}
}
