namespace Disassembler.Formats.MZ
{
	/// <summary>
	/// A far call trampoline in the resident image of an overlaid MicroSoft C executable.
	///
	/// The resident code never calls an overlaid function directly. It calls a thunk, which calls
	/// the overlay manager and then carries, in the operand of a JMP FAR that is never executed,
	/// the address the manager should transfer to. Three shapes exist:
	///
	///   overlay, 14 bytes:  CALL FAR manager; JMP FAR 0000:entry; u16 overlay number; u16 segment
	///   overlay, 12 bytes:  CALL FAR manager; JMP FAR 0000:entry; u16 overlay number
	///   resident, 10 bytes: CALL FAR manager; JMP FAR segment:offset
	///
	/// An overlay thunk's segment word is zero because the segment isn't known until the manager
	/// has loaded the overlay, which is what leaves the overlay number room to sit where the
	/// segment would be. An overlay is linked from several code segments, so the last word is the
	/// paragraph the target's segment starts at within the overlay, and the entry offset is
	/// relative to that segment, not to the start of the overlay. The short form, which is only
	/// ever followed directly by another thunk, targets the overlay's first segment.
	/// </summary>
	public class MZOverlayThunk
	{
		/// <summary>Length of an overlay thunk that carries its segment</summary>
		public const int OverlayLength = 14;

		/// <summary>Length of an overlay thunk into the overlay's first segment</summary>
		public const int ShortOverlayLength = 12;

		/// <summary>Length of a resident thunk</summary>
		public const int ResidentLength = 10;

		private int iPosition;
		private int iLength;
		private int iOverlayNumber;
		private int iTargetOffset;
		private ushort usTargetSegment;

		public MZOverlayThunk(int position, int length, int overlayNumber, int targetOffset, ushort targetSegment)
		{
			this.iPosition = position;
			this.iLength = length;
			this.iOverlayNumber = overlayNumber;
			this.iTargetOffset = targetOffset;
			this.usTargetSegment = targetSegment;
		}

		/// <summary>
		/// Offset of the thunk within the load module, which is also its segment:offset address
		/// </summary>
		public int Position
		{
			get => this.iPosition;
		}

		/// <summary>
		/// One based overlay number, or 0 when the target is already resident
		/// </summary>
		public int OverlayNumber
		{
			get => this.iOverlayNumber;
		}

		/// <summary>
		/// Entry offset within the target's segment
		/// </summary>
		public int TargetOffset
		{
			get => this.iTargetOffset;
		}

		/// <summary>
		/// Segment of a resident target, or for an overlay thunk the paragraph within the overlay
		/// that the target's segment starts at
		/// </summary>
		public ushort TargetSegment
		{
			get => this.usTargetSegment;
		}

		public bool IsOverlay
		{
			get => this.iOverlayNumber > 0;
		}

		public int Length
		{
			get => this.iLength;
		}

		public override string ToString()
		{
			return this.IsOverlay
				? $"thunk at 0x{this.iPosition:x} -> overlay {this.iOverlayNumber}:{this.usTargetSegment:x4}:{this.iTargetOffset:x4}"
				: $"thunk at 0x{this.iPosition:x} -> {this.usTargetSegment:x4}:{this.iTargetOffset:x4}";
		}
	}
}
