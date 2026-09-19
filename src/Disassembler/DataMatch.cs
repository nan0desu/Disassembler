using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Disassembler
{
	public class DataMatch
	{
		private int position;
		private int offset;
		private int length;

		public DataMatch(int position, int offset, int length)
		{
			this.position = position;
			this.offset = offset;
			this.length = length;
		}

		public int Position { get => this.position; }

		public int Offset { get => this.offset; }

		public int Length { get => this.length; }
	}
}
